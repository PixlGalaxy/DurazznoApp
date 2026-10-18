using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Durazzno.Core;
using Durazzno.Network;

namespace Durazzno.Apps;

/// <summary>
/// One app in apps.json on Webin Cloud Server (folder durazznoapp/DurazznoApp/Apps, WebinCloud.AppsPath). url and icon may be file names next to
/// apps.json or full links. message, when set, is shown before downloading (requirements, warnings).
/// publish-apps.ps1 fills in sha256 and size for files it uploads.
/// </summary>
internal sealed class AppEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Icon { get; init; }
    public string? Message { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    /// <summary>
    /// How the download is checked: a SHA-256 (64 hex characters), a link or file name of a text file that contains it
    /// (e.g. AppHash.txt next to the exe), or empty / missing for no check.
    /// </summary>
    public string? Sha256 { get; init; }
    public long? Size { get; init; }

    /// <summary>Command-line arguments used when Durazzno opens the downloaded file.</summary>
    public string? Arguments { get; init; }

    [JsonIgnore]
    public string Key => string.IsNullOrWhiteSpace(Id) ? Name : Id;
}

internal sealed class AppsDocument
{
    public List<AppEntry> Apps { get; init; } = [];
}

/// <summary>An app Durazzno downloaded, listed in apps\apps-installed.json next to the app.</summary>
internal sealed record InstalledApp(string Key, string Name, string? Version, string FilePath, string? IconPath, string? Arguments, DateTime Installed);

internal sealed record AppDownloadProgress(double? Percent);

/// <summary>The app's file is open (the app is running), so it cannot be replaced or removed.</summary>
internal sealed class AppInUseException(string appName, Exception? inner = null)
    : IOException($"{appName} is running. Close it and try again.", inner)
{
    public string AppName { get; } = appName;
}

/// <summary>
/// The Apps page's data: the signed catalog on Webin Cloud Server (apps.json + apps.json.sig, checked with the same key
/// as updates, so only apps published with Durazzno's key can be downloaded) and the apps already downloaded.
/// Each download goes to apps\&lt;id&gt;\ next to DurazznoApp.exe and is checked against its SHA-256 when the catalog
/// lists one.
/// </summary>
internal static class AppCatalog
{
    public const string CatalogFile = "apps.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>apps\ next to DurazznoApp.exe: one folder per app, plus the installed list and cached icons.</summary>
    public static string AppsDir => Path.Combine(AppInfo.BaseDir, "apps");
    private static string InstalledFile => Path.Combine(AppsDir, "apps-installed.json");
    private static string IconsDir => Path.Combine(AppsDir, ".icons");

    public static Uri CatalogUri => WebinCloud.FileUri(WebinCloud.AppsPath, CatalogFile);

    /// <summary>Downloads and verifies the catalog.</summary>
    public static async Task<List<AppEntry>> GetOnlineAsync(CancellationToken ct)
    {
        var bytes = await ServerClient.GetSignedAsync(CatalogUri, "apps catalog", ct);
        var doc = JsonSerializer.Deserialize<AppsDocument>(bytes, Json) ?? new AppsDocument();
        return doc.Apps.Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Url)).ToList();
    }

    public static Uri Resolve(string urlOrFile) => WebinCloud.Sibling(CatalogUri, urlOrFile);

    public static List<InstalledApp> Installed()
    {
        lock (Gate)
        {
            try
            {
                var list = File.Exists(InstalledFile) ? JsonSerializer.Deserialize<List<InstalledApp>>(File.ReadAllText(InstalledFile), Json) ?? [] : [];
                return list.Where(a => File.Exists(a.FilePath)).ToList(); // deleted by hand: gone
            }
            catch (Exception ex)
            {
                Log.Warn($"Apps: installed list unreadable: {ex.Message}");
                return [];
            }
        }
    }

    /// <summary>Downloads the app (and its icon) and records it as installed.</summary>
    public static async Task<InstalledApp> DownloadAsync(AppEntry app, IProgress<AppDownloadProgress>? progress, CancellationToken ct)
    {
        var uri = Resolve(app.Url);
        var expected = await ExpectedHashAsync(app, ct); // before downloading: a missing hash file fails fast
        var folder = Path.Combine(AppsDir, Safe(app.Key));
        Directory.CreateDirectory(folder);
        var fileName = FileNameOf(uri, app);
        var target = Path.Combine(folder, fileName);
        var temp = target + ".download";
        if (IsInUse(target))
            throw new AppInUseException(app.Name); // updating a running app: say so before downloading it all

        try
        {
            var (source, length) = await ServerClient.OpenReadAsync(uri, ct);
            length ??= app.Size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (source)
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                var last = -1;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    var percent = length is > 0 ? (int)(total * 100 / length.Value) : -1;
                    if (percent != last)
                    {
                        last = percent;
                        progress?.Report(new AppDownloadProgress(percent >= 0 ? percent : null));
                    }
                }
            }
            if (expected is not null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download failed the integrity check.");
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }

        var icon = await DownloadIconAsync(app, ct);
        var installed = new InstalledApp(app.Key, app.Name, app.Version, target, icon, app.Arguments, DateTime.Now);
        lock (Gate)
        {
            var list = Installed().Where(a => a.Key != app.Key).Append(installed).ToList();
            Directory.CreateDirectory(AppsDir);
            File.WriteAllText(InstalledFile, JsonSerializer.Serialize(list, Json));
        }
        Log.Info($"Apps: downloaded {app.Name} {app.Version} to {target}");
        return installed;
    }

    /// <summary>
    /// The SHA-256 the download must match, from the entry's "sha256": the hash itself, or a hash file to fetch (the
    /// first 64-hex-character word in it). Null when the entry has none: the download is not checked.
    /// </summary>
    private static async Task<string?> ExpectedHashAsync(AppEntry app, CancellationToken ct)
    {
        var value = app.Sha256?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;
        if (value.Length == 64 && value.All(Uri.IsHexDigit))
            return value;

        string text;
        try
        {
            var (source, _) = await ServerClient.OpenReadAsync(Resolve(value), ct);
            await using (source)
            using (var reader = new StreamReader(source))
                text = await reader.ReadToEndAsync(ct);
        }
        catch (HttpRequestException)
        {
            throw; // shown as "Server error" with the code
        }
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\b[0-9A-Fa-f]{64}\b");
        return match.Success ? match.Value : throw new InvalidDataException($"The hash file for {app.Name} does not contain a SHA-256.");
    }

    /// <summary>Icon for a catalog entry, cached on disk (null when the entry has none or it cannot be read).</summary>
    public static async Task<string?> DownloadIconAsync(AppEntry app, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.Icon))
            return null;
        try
        {
            var uri = Resolve(app.Icon);
            Directory.CreateDirectory(IconsDir);
            var extension = Path.GetExtension(uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath).Replace("/download", ""));
            var path = Path.Combine(IconsDir, Safe(app.Key) + (extension is { Length: > 1 and < 6 } ? extension : ".png"));
            if (File.Exists(path) && DateTime.Now - File.GetLastWriteTime(path) < TimeSpan.FromDays(1))
                return path;
            var (source, _) = await ServerClient.OpenReadAsync(uri, ct);
            await using (source)
            await using (var output = File.Create(path))
                await source.CopyToAsync(output, ct);
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Apps: icon of {app.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Opens the downloaded file (runs an exe, opens anything else with its default program).</summary>
    public static string? Open(InstalledApp app)
    {
        try
        {
            Process.Start(new ProcessStartInfo(app.FilePath, app.Arguments ?? "")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(app.FilePath)!,
            })?.Dispose();
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null; // UAC declined
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Deletes the app's folder and its entry. Throws <see cref="AppInUseException"/> while the app is running.</summary>
    public static void Remove(InstalledApp app)
    {
        if (IsInUse(app.FilePath))
            throw new AppInUseException(app.Name);
        lock (Gate)
        {
            var folder = Path.GetDirectoryName(app.FilePath)!;
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AppInUseException(app.Name, ex); // a file in it is open: keep the entry, nothing half-removed
            }
            var list = Installed().Where(a => a.Key != app.Key).ToList();
            File.WriteAllText(InstalledFile, JsonSerializer.Serialize(list, Json));
        }
        Log.Info($"Apps: removed {app.Name}");
    }

    /// <summary>True while the file is open by another process (the app is running).</summary>
    public static bool IsInUse(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    // Webin Cloud links end in ".../<file>/download?inline=1": the file name is the segment before "download".
    private static string FileNameOf(Uri uri, AppEntry app)
    {
        var path = uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.EndsWith("/download", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/download".Length];
        var name = Path.GetFileName(path);
        return Safe(string.IsNullOrWhiteSpace(name) ? app.Key + ".exe" : name);
    }

    private static string Safe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
