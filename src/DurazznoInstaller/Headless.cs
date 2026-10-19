namespace Durazzno.Installer;

/// <summary>
/// --headless: the whole install with no window, for scripts (the old Python Durazzno App runs it to move its users to
/// the new app, then deletes its own files and starts the new one). It checks Webin Cloud Server and the signed release,
/// downloads and verifies the exe, installs it, adds the shortcuts and the uninstaller, and does not start the app.
/// Progress goes to %TEMP%\DurazznoAppInstaller.log (and to standard output when the caller captures it).
/// </summary>
internal static class Headless
{
    public const string Flag = "--headless";

    // Exit codes, for the caller.
    public const int Installed = 0;
    public const int UnexpectedError = 1;
    public const int ServerUnavailable = 2;
    public const int NotTrusted = 3;
    public const int InstallFailed = 4;

    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), "DurazznoAppInstaller.log");

    public static int Run()
    {
        try
        {
            File.WriteAllText(LogFile, "");
        }
        catch
        {
            // logging is best effort
        }
        Write($"Durazzno App Installer (headless) installing to {Installer.InstallDir}");
        try
        {
            return RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Write($"Unexpected error: {ex}");
            return UnexpectedError;
        }
    }

    private static async Task<int> RunAsync()
    {
        var ct = CancellationToken.None;

        Release release;
        try
        {
            var (manifest, signature) = await Installer.ReachServerAsync(ct);
            Write("Webin Cloud Server: online");
            release = Installer.ReadRelease(manifest, signature);
            Write($"Release: Durazzno App {release.Version}, signed by Galaxy Apps ({release.Size} bytes)");
            await Installer.CheckExeAsync(release, ct);
            Write("DurazznoApp.exe is available");
        }
        catch (InstallException ex)
        {
            Write($"Server check failed: {ex.Message}");
            return ex.Message.Contains("signed") ? NotTrusted : ServerUnavailable;
        }

        string download;
        try
        {
            var lastTenth = -1;
            download = await Installer.DownloadAsync(release, new SyncProgress<(long Got, long Total)>(p =>
            {
                var tenth = p.Total > 0 ? (int)(p.Got * 10 / p.Total) : -1;
                if (tenth != lastTenth)
                {
                    lastTenth = tenth;
                    Write($"Downloading: {Math.Max(0, tenth) * 10}%");
                }
            }), ct);
            Write("Download matches the signed release (SHA-256 and size)");
        }
        catch (InstallException ex)
        {
            Write($"Download failed: {ex.Message}");
            // A connection problem can be tried again later; a file that does not match the signed release cannot.
            return ex.Message.Contains("SHA-256") || ex.Message.Contains("incomplete") ? NotTrusted : ServerUnavailable;
        }

        try
        {
            Installer.CopyIntoPlace(download);
            Write($"Installed: {Installer.ExePath}");
            Installer.CreateShortcuts();
            Installer.RegisterUninstaller(release);
            Write("Shortcuts and uninstaller added");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InstallException)
        {
            Write($"Install failed: {ex.Message}");
            return InstallFailed;
        }

        Write("Done");
        return Installed;
    }

    private static void Write(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        try
        {
            File.AppendAllText(LogFile, stamped + Environment.NewLine);
        }
        catch
        {
            // logging is best effort
        }
        try
        {
            Console.WriteLine(stamped); // reaches the caller when it redirects output
        }
        catch
        {
            // no console
        }
    }

    /// <summary>Progress reported on the calling thread (there is no UI thread to post to).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
