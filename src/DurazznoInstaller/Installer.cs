using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Durazzno.Network;
using Durazzno.Updates;

namespace Durazzno.Installer;

/// <summary>The release on Webin Cloud Server, from the signed manifest.json.</summary>
internal sealed record Release(string Version, Uri ExeUri, string Sha256, long Size);

/// <summary>Something went wrong with a short, user-facing reason.</summary>
internal sealed class InstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// What the installer does: read the signed manifest from Webin Cloud Server (the same one Durazzno App updates from),
/// download DurazznoApp.exe and check it against the manifest's SHA-256 and size, copy it to the install folder, add
/// Desktop and Start Menu shortcuts and register itself as the uninstaller. Starting with Windows is left to the app:
/// it turns that on the first time it runs. Uninstalling removes the folder (with the apps downloaded into it), the
/// shortcuts and startup entries pointing there, and the app's settings.
/// </summary>
internal static class Installer
{
    public const string ExeFileName = "DurazznoApp.exe";
    // A test install (--dir) gets its own names, so it never replaces a real install's shortcuts.
    public static string ShortcutName => Testing ? "Durazzno App (test).lnk" : "Durazzno App.lnk";

    /// <summary>
    /// Per-user, no admin rights needed: Durazzno replaces its own exe when it updates, which Program Files would
    /// turn into a UAC prompt every time. Overridable with --dir for testing.
    /// </summary>
    public static string InstallDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DurazznoApp");

    /// <summary>Durazzno App's settings, logs and history (AppInfo.DataDir in the app).</summary>
    public static string DataDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DurazznoApp");

    /// <summary>--dir: install somewhere else, with its own data folder and Apps &amp; features entry (testing).</summary>
    public static bool Testing { get; private set; }

    public static void UseTestFolder(string folder)
    {
        InstallDir = Path.GetFullPath(folder);
        DataDir = Path.Combine(InstallDir, "data");
        Testing = true;
    }

    public static string ExePath => Path.Combine(InstallDir, ExeFileName);

    /// <summary>A copy of this installer kept in the install folder: Start Menu's "Uninstall Durazzno App" opens it.</summary>
    public static string InstallerCopyPath => Path.Combine(InstallDir, "DurazznoAppInstaller.exe");

    public static string UninstallShortcutName => Testing ? "Uninstall Durazzno App (test).lnk" : "Uninstall Durazzno App.lnk";

    public static bool IsInstalled => File.Exists(ExePath);

    private static string UninstallKey => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + (Testing ? "DurazznoApp (test)" : "DurazznoApp");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DurazznoAppInstaller/1.0");
        return client;
    }

    // ── Check ───────────────────────────────────────────────────────────────────────────

    /// <summary>Downloads manifest.json and its signature; throws unless the server answers.</summary>
    public static async Task<(byte[] Manifest, string Signature)> ReachServerAsync(CancellationToken ct)
    {
        var manifestUri = WebinCloud.DefaultManifestUri;
        var manifest = await GetBytesAsync(manifestUri, "the release information", ct);
        var signature = Encoding.ASCII.GetString(await GetBytesAsync(WebinCloud.SignatureOf(manifestUri), "the release signature", ct));
        return (manifest, signature);
    }

    /// <summary>Checks the manifest's signature and reads the release from it.</summary>
    public static Release ReadRelease(byte[] manifest, string signature)
    {
        if (!UpdateSignature.IsValid(manifest, signature))
            throw new InstallException("The release on the server is not signed by Galaxy Apps, so it was not trusted.");
        ManifestDoc? doc;
        try
        {
            using var stream = new MemoryStream(manifest);
            doc = (ManifestDoc?)new DataContractJsonSerializer(typeof(ManifestDoc)).ReadObject(stream);
        }
        catch (Exception ex)
        {
            throw new InstallException("The release information on the server could not be read.", ex);
        }
        if (doc?.App is not { Version.Length: > 0, Url.Length: > 0, Sha256.Length: 64 } app)
            throw new InstallException("The release information on the server is incomplete.");
        return new Release(app.Version, WebinCloud.Sibling(WebinCloud.DefaultManifestUri, app.Url), app.Sha256, app.Size);
    }

    /// <summary>Makes sure DurazznoApp.exe is on the server (HTTP 200) without downloading it.</summary>
    public static Task CheckExeAsync(Release release, CancellationToken ct) => WithTimeout(ct, async token =>
    {
        using var response = await SendAsync(release.ExeUri, "Durazzno App", token);
        if (response.Content.Headers.ContentLength is { } length && release.Size > 0 && length != release.Size)
            throw new InstallException("The Durazzno App file on the server is still uploading or does not match the release. Try again in a moment.");
        return true;
    });

    // ── Install ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the exe to a temporary file and checks its size and SHA-256 against the signed manifest. Returns the
    /// file; it is deleted when anything does not match.
    /// </summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<(long Got, long Total)> progress, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"DurazznoApp-{Guid.NewGuid():N}.exe");
        try
        {
            using var response = await SendAsync(release.ExeUri, "Durazzno App", ct);
            var total = response.Content.Headers.ContentLength ?? release.Size;
            using var sha = SHA256.Create();
            long got = 0;
            using (var source = await response.Content.ReadAsStreamAsync())
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer, 0, read, ct);
                    got += read;
                    progress.Report((got, total));
                }
            }
            sha.TransformFinalBlock([], 0, 0);

            if (release.Size > 0 && got != release.Size)
                throw new InstallException("The download is incomplete. Check your connection and try again.");
            var hash = BitConverter.ToString(sha.Hash!).Replace("-", "");
            if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InstallException("The download does not match the signed release (SHA-256 check failed), so it was not installed.");
            return temp;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>A Durazzno App already running from the install folder (it is closed before its exe is replaced).</summary>
    public static Process? RunningCopy()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeFileName)))
        {
            try
            {
                if (string.Equals(ImagePath(process.Id), ExePath, StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch
            {
                // another user's or elevated process: not ours to replace
            }
            process.Dispose();
        }
        return null;
    }

    /// <summary>Closes a running copy, then puts the verified download in the install folder.</summary>
    public static void CopyIntoPlace(string download)
    {
        using (var running = RunningCopy())
        {
            if (running is not null)
            {
                running.Kill();
                running.WaitForExit(10_000);
            }
        }

        Directory.CreateDirectory(InstallDir);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(download, ExePath, overwrite: true);
                break;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(500); // the closed app may still be letting go of its exe
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                Thread.Sleep(500);
            }
        }
        TryDelete(download);
    }

    /// <summary>Desktop and Start Menu shortcuts to the installed exe.</summary>
    public static void CreateShortcuts()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
            SaveShortcut(Path.Combine(Environment.GetFolderPath(folder), ShortcutName), ExePath, "Durazzno App");
    }

    /// <summary>
    /// Keeps a copy of this installer in the install folder and makes it the uninstaller: a Start Menu shortcut and an
    /// entry in Windows' Apps &amp; features (per user, no admin rights).
    /// </summary>
    public static void RegisterUninstaller(Release release)
    {
        var self = Process.GetCurrentProcess().MainModule!.FileName;
        if (!string.Equals(self, InstallerCopyPath, StringComparison.OrdinalIgnoreCase))
            File.Copy(self, InstallerCopyPath, overwrite: true);
        SaveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), UninstallShortcutName), InstallerCopyPath,
            "Repair or uninstall Durazzno App");

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", Testing ? "Durazzno App (test)" : "Durazzno App");
        key.SetValue("DisplayIcon", ExePath + ",0");
        key.SetValue("Publisher", "Galaxy Apps");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstallerCopyPath}\"");
        key.SetValue("URLInfoAbout", "https://www.itzgalaxy.com");
        key.SetValue("EstimatedSize", (int)(release.Size / 1024), Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
    }

    private static void SaveShortcut(string path, string target, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InstallException("Windows could not create the shortcuts.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = InstallDir;
        link.IconLocation = ExePath + ",0";
        link.Description = description;
        link.Save();
    }

    // ── Uninstall ───────────────────────────────────────────────────────────────────────

    /// <summary>Closes Durazzno App and anything else running from the install folder (apps downloaded through it).</summary>
    public static void CloseEverything()
    {
        var self = Process.GetCurrentProcess().Id;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == self || !IsInside(ImagePath(process.Id)))
                        continue;
                    process.Kill();
                    process.WaitForExit(10_000);
                }
                catch
                {
                    // gone already, or not ours to close
                }
            }
        }
    }

    /// <summary>
    /// A process's exe path, the quick way Task Manager reads it (Process.MainModule loads every process's module list
    /// and takes minutes across a whole PC). Null for processes this user cannot query.
    /// </summary>
    private static string? ImagePath(int pid)
    {
        const uint QueryLimitedInformation = 0x1000;
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Removes what points into the install folder: Desktop and Start Menu shortcuts (Durazzno's and the ones made for
    /// downloaded apps), "start with Windows" entries (Durazzno's and its apps'), and the Apps &amp; features entry.
    /// Shortcuts and entries pointing anywhere else are left alone.
    /// </summary>
    public static void RemoveShortcutsAndStartup()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        dynamic? shell = type is null ? null : Activator.CreateInstance(type);
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
        {
            foreach (var lnk in Directory.EnumerateFiles(Environment.GetFolderPath(folder), "*.lnk"))
            {
                try
                {
                    string target = shell is null ? "" : (string)shell.CreateShortcut(lnk).TargetPath;
                    if (IsInside(target))
                        File.Delete(lnk);
                }
                catch
                {
                    // unreadable shortcut: not ours
                }
            }
        }

        using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            foreach (var name in run?.GetValueNames() ?? [])
                if (run!.GetValue(name) is string command && command.IndexOf(InstallDir, StringComparison.OrdinalIgnoreCase) >= 0)
                    run.DeleteValue(name, throwOnMissingValue: false);
        }
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
    }

    /// <summary>Deletes the install folder (the app and every downloaded app) and Durazzno's settings, logs and history.</summary>
    public static void DeleteFiles()
    {
        foreach (var folder in new[] { InstallDir, DataDir })
        {
            for (var attempt = 1; Directory.Exists(folder); attempt++)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 20)
                        throw new InstallException($"Some files in {folder} are still in use. Close any app from Durazzno and try again.", ex);
                    Thread.Sleep(500); // closed processes may still be letting go of their files
                }
            }
        }
    }

    private static bool IsInside(string? path) =>
        !string.IsNullOrEmpty(path) && Path.GetFullPath(path).StartsWith(InstallDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts the installed app: with its window (--open), or in the tray.</summary>
    public static void Launch(bool openWindow) =>
        Process.Start(new ProcessStartInfo(ExePath, openWindow ? "--open" : "") { UseShellExecute = false, WorkingDirectory = InstallDir })?.Dispose();

    // ── HTTP ────────────────────────────────────────────────────────────────────────────

    private static Task<byte[]> GetBytesAsync(Uri uri, string what, CancellationToken ct) => WithTimeout(ct, async token =>
    {
        using var response = await SendAsync(uri, what, token);
        return await response.Content.ReadAsByteArrayAsync();
    });

    /// <summary>Runs a server call with a 20 s limit; running out of time is an error, not a cancel.</summary>
    private static async Task<T> WithTimeout<T>(CancellationToken ct, Func<CancellationToken, Task<T>> call)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            return await call(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InstallException("Webin Cloud Server did not answer in time. Try again in a moment.");
        }
    }

    /// <summary>GET with only the headers read; throws an <see cref="InstallException"/> unless the answer is 200.</summary>
    private static async Task<HttpResponseMessage> SendAsync(Uri uri, string what, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InstallException("Webin Cloud Server can't be reached. Check your internet connection.", ex);
        }
        if (response.StatusCode == HttpStatusCode.OK)
            return response;
        var code = (int)response.StatusCode;
        response.Dispose();
        throw new InstallException(code == 404
            ? $"Webin Cloud Server is online, but {what} is not there (404)."
            : $"Webin Cloud Server answered with an error ({code}) for {what}.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    [DataContract]
    private sealed class ManifestDoc
    {
        [DataMember(Name = "app")] public PackageDoc? App { get; set; }
    }

    [DataContract]
    private sealed class PackageDoc
    {
        [DataMember(Name = "version")] public string Version { get; set; } = "";
        [DataMember(Name = "url")] public string Url { get; set; } = "";
        [DataMember(Name = "sha256")] public string Sha256 { get; set; } = "";
        [DataMember(Name = "size")] public long Size { get; set; }
    }
}
