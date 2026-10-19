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

    /// <summary>
    /// The version as text ("1.2"), or a link to a text file that holds it (e.g. AppVersion.txt next to the exe, so the
    /// app's own release script can bump it without re-signing the catalog). Links are read when the catalog loads.
    /// </summary>
    public string? Version { get; set; }
    /// <summary>
    /// How the download is checked: a SHA-256 (64 hex characters), a link or file name of a text file that contains it
    /// (e.g. AppHash.txt next to the exe), or empty / missing for no check.
    /// </summary>
    public string? Sha256 { get; init; }
    public long? Size { get; init; }

    /// <summary>Command-line arguments used when Durazzno opens the downloaded file.</summary>
    public string? Arguments { get; init; }

    /// <summary>Put a shortcut to the downloaded file on the Desktop (for apps with a window).</summary>
    public bool DesktopShortcut { get; init; }

    /// <summary>Put a shortcut to the downloaded file in the Start Menu (Programs).</summary>
    public bool StartMenuShortcut { get; init; }

    [JsonIgnore]
    public string Key => string.IsNullOrWhiteSpace(Id) ? Name : Id;
}

internal sealed class AppsDocument
{
    public List<AppEntry> Apps { get; init; } = [];
}

/// <summary>
/// An app Durazzno downloaded, listed in apps\apps-installed.json next to the app. Shortcuts are the .lnk files Durazzno
/// created for it, removed with the app.
/// </summary>
internal sealed record InstalledApp(string Key, string Name, string? Version, string FilePath, string? IconPath, string? Arguments, DateTime Installed,
    List<string>? Shortcuts = null);

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
        var apps = doc.Apps.Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Url)).ToList();
        await Task.WhenAll(apps.Where(a => IsLink(a.Version)).Select(async a => a.Version = await ReadVersionAsync(a, ct)));
        return apps;
    }

    private static readonly Dictionary<string, (string? Version, DateTime Read)> VersionCache = [];

    private static bool IsLink(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// The first line of the version file an entry links to. Kept for a minute (the catalog reloads every 10 s); when
    /// the file cannot be read, the last known version is used, or none (no update offered).
    /// </summary>
    private static async Task<string?> ReadVersionAsync(AppEntry app, CancellationToken ct)
    {
        var link = app.Version!.Trim();
        lock (VersionCache)
        {
            if (VersionCache.TryGetValue(link, out var cached) && DateTime.Now - cached.Read < TimeSpan.FromMinutes(1))
                return cached.Version;
        }
        string? version;
        try
        {
            var (source, _) = await ServerClient.OpenReadAsync(Resolve(link), ct);
            await using (source)
            using (var reader = new StreamReader(source))
                version = (await reader.ReadToEndAsync(ct)).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (version is { Length: > 32 })
                version = version[..32]; // not a version file
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Apps: version of {app.Name}: {ex.Message}");
            lock (VersionCache)
                return VersionCache.TryGetValue(link, out var stale) ? stale.Version : null;
        }
        lock (VersionCache)
            VersionCache[link] = (version, DateTime.Now);
        return version;
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
        // An update re-creates the shortcuts: the file name or the catalog's choice may have changed.
        if (Installed().FirstOrDefault(a => a.Key == app.Key) is { } previous)
            RemoveShortcuts(previous);
        var shortcuts = CreateShortcuts(app, target, icon);
        var installed = new InstalledApp(app.Key, app.Name, app.Version, target, icon, app.Arguments, DateTime.Now, shortcuts);
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
            RemoveShortcuts(app);
            var list = Installed().Where(a => a.Key != app.Key).ToList();
            File.WriteAllText(InstalledFile, JsonSerializer.Serialize(list, Json));
        }
        Log.Info($"Apps: removed {app.Name}");
    }

    /// <summary>
    /// Desktop / Start Menu shortcuts the catalog asks for ("desktopShortcut", "startMenuShortcut"). A shortcut with the
    /// same name that points somewhere else belongs to the user and is left alone.
    /// </summary>
    private static List<string> CreateShortcuts(AppEntry app, string target, string? iconImage)
    {
        var created = new List<string>();
        var folders = new List<string>();
        if (app.DesktopShortcut)
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        if (app.StartMenuShortcut)
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        if (folders.Count == 0)
            return created;

        var icon = ShortcutIcon(target, iconImage);
        foreach (var folder in folders.Where(f => !string.IsNullOrEmpty(f)))
        {
            var lnk = Path.Combine(folder, Safe(app.Name) + ".lnk");
            if (File.Exists(lnk) && !IsOurs(lnk))
            {
                Log.Info($"Apps: {lnk} already exists and is not Durazzno's, left as is");
                continue;
            }
            if (Durazzno.Startup.Shortcut.Create(lnk, target, app.Arguments ?? "", Path.GetDirectoryName(target)!, icon, app.Description))
                created.Add(lnk);
        }
        return created;
    }

    private static void RemoveShortcuts(InstalledApp app)
    {
        foreach (var lnk in app.Shortcuts ?? [])
        {
            try
            {
                if (File.Exists(lnk) && IsOurs(lnk))
                    File.Delete(lnk);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Apps: could not delete {lnk}: {ex.Message}");
            }
        }
    }

    // A shortcut Durazzno made points into apps\.
    private static bool IsOurs(string lnk)
    {
        var (target, _) = Durazzno.Startup.Shortcut.Resolve(lnk);
        return Path.GetFullPath(target).StartsWith(Path.GetFullPath(AppsDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Icon for a shortcut: an exe keeps its own; anything else (a script, a document) gets the catalog icon as an .ico
    /// next to the file. Null means the target's default icon.
    /// </summary>
    private static string? ShortcutIcon(string target, string? image)
    {
        if (image is null || Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var source = System.Drawing.Image.FromFile(image);
            var size = Math.Min(256, Math.Max(source.Width, source.Height));
            var scale = (double)size / Math.Max(source.Width, source.Height);
            int w = (int)Math.Round(source.Width * scale), h = (int)Math.Round(source.Height * scale);
            using var bitmap = new System.Drawing.Bitmap(size, size);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, (size - w) / 2, (size - h) / 2, w, h);
            }
            using var png = new MemoryStream();
            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);

            // An .ico with one PNG frame (supported since Windows Vista).
            var path = Path.Combine(Path.GetDirectoryName(target)!, ".shortcut.ico");
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write((short)0); writer.Write((short)1); writer.Write((short)1);  // header: icon, 1 image
            var side = (byte)(size >= 256 ? 0 : size);                               // 0 means 256
            writer.Write(side); writer.Write(side); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((short)1); writer.Write((short)32);                         // planes, bits per pixel
            writer.Write((int)png.Length); writer.Write(22);                         // size, offset after the 22-byte header
            writer.Write(png.ToArray());
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn($"Apps: shortcut icon for {target}: {ex.Message}");
            return null;
        }
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
