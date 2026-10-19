using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Durazzno.Core;
using Durazzno.Network;

namespace Durazzno.Updates;

internal sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Update progress: Phase is "downloading" (Percent 0–100 when the size is known) or "installing".</summary>
internal sealed record UpdateProgress(string Phase, double? Percent);

/// <summary>
/// Durazzno App updates in two steps:
///  1. Stage: the new exe is downloaded next to the current one as DurazznoApp.exe.staged and checked against
///     the SHA-256 in the signed manifest. This can happen in the background while the app runs.
///  2. Apply: the running exe is renamed to .old (Windows allows that), the staged exe takes its place and the app
///     restarts. Done at the next start, or right away when the user asks.
/// If the new version crashes right after starting, the .old exe is put back (see <see cref="RunFinish"/>). The update
/// stays on offer: it is tried again at the next start or on APPLY UPDATE, so no version in the chain gets skipped.
/// </summary>
internal static class UpdateService
{
    public const string UpdatedFlag = "--updated";   // started by an update: notify "updated"
    public const string OpenFlag = "--open";         // after a user-started update: show the window again
    public const string FailedFlag = "--update-failed"; // back on the previous version: notify that the update failed

    private static string NewExe => AppInfo.ExePath + ".new";
    private static string StagedExe => AppInfo.ExePath + ".staged";
    private static string OldExe => AppInfo.ExePath + ".old";
    private static string StagedInfo => Path.Combine(AppInfo.DataDir, "update-staged.json");
    private static string WhatsNewFile => Path.Combine(AppInfo.DataDir, "whats-new.json");
    /// <summary>How long a freshly installed version must keep running before the update counts as good.</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    private sealed record Staged(string Version, string Sha256);
    private sealed record ReleaseNotes(string Version, string Notes);

    /// <summary>
    /// Keeps the manifest's "notes" for a version, so the updated app can show what changed (notification, Settings)
    /// even before it reads the manifest again.
    /// </summary>
    public static void SaveNotes(PackageInfo app)
    {
        if (string.IsNullOrWhiteSpace(app.Notes) || NotesFor(app.Version) == app.Notes.Trim())
            return;
        try
        {
            File.WriteAllText(WhatsNewFile, JsonSerializer.Serialize(new ReleaseNotes(app.Version, app.Notes.Trim())));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save the release notes: {ex.Message}");
        }
    }

    /// <summary>The saved release notes of <paramref name="version"/>, if any.</summary>
    public static string? NotesFor(string version)
    {
        try
        {
            if (File.Exists(WhatsNewFile) && JsonSerializer.Deserialize<ReleaseNotes>(File.ReadAllText(WhatsNewFile)) is { } notes
                && notes.Version == version)
                return notes.Notes;
        }
        catch
        {
            // a broken file just means no notes
        }
        return null;
    }

    /// <summary>Removes half-finished downloads and staged copies that are not newer than this version. Call at startup.</summary>
    public static void CleanupLeftovers()
    {
        TryDeleteFile(NewExe);
        TryDeleteFile(AppInfo.ExePath + ".broken");
        TryDeleteFile(Path.Combine(AppInfo.BaseDir, "DurazznoTerminal.exe")); // shipped up to 3.0.0, no longer used
        if (ReadStaged() is null)
        {
            TryDeleteFile(StagedExe);
            TryDeleteFile(StagedInfo);
        }
    }

    /// <summary>Version of the staged update when it is newer than this app and its file is present.</summary>
    public static string? StagedVersion => ReadStaged()?.Version;

    public static bool IsStaged(UpdateManifest manifest) =>
        ReadStaged() is { } staged && staged.Version == manifest.App.Version && staged.Sha256.Equals(manifest.App.Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads and verifies the update into the .staged file (no-op when it is already staged).</summary>
    public static async Task StageAsync(UpdateManifest manifest, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        if (!manifest.IsAppNewer || IsStaged(manifest))
            return;
        EnsureInstallFolderWritable();
        Log.Info($"Downloading Durazzno App {manifest.App.Version}");
        await DownloadVerifiedAsync(manifest.Resolve(manifest.App), manifest.App.Sha256, manifest.App.Size, NewExe, progress, ct);
        File.Move(NewExe, StagedExe, overwrite: true);
        File.WriteAllText(StagedInfo, JsonSerializer.Serialize(new Staged(manifest.App.Version, manifest.App.Sha256)));
        SaveNotes(manifest.App);
        Log.Info($"Durazzno App {manifest.App.Version} is staged");
    }

    public const string FinishFlag = "--finish-update"; // --finish-update <old pid> [--open]

    /// <summary>
    /// Installs the staged update by handing over to it: the staged exe is started with --finish-update and this
    /// process must exit right away. (A single-file app cannot rename its own exe and keep running: assemblies it has
    /// not loaded yet are read from that file. So the new exe does the swap once this one has exited.)
    /// </summary>
    public static void HandOff(bool open)
    {
        var staged = ReadStaged() ?? throw new UpdateException("There is no downloaded update to install.");

        // Re-check the file: it waited on disk since it was downloaded.
        bool intact;
        using (var stream = File.OpenRead(StagedExe))
            intact = Convert.ToHexString(SHA256.HashData(stream)).Equals(staged.Sha256, StringComparison.OrdinalIgnoreCase);
        if (!intact)
        {
            TryDeleteFile(StagedExe);
            TryDeleteFile(StagedInfo);
            throw new UpdateException("The downloaded update is damaged; it will be downloaded again.");
        }

        Log.Info($"Installing Durazzno App {staged.Version}");
        Process.Start(new ProcessStartInfo(StagedExe, $"{FinishFlag} {Environment.ProcessId}{(open ? " " + OpenFlag : "")}") { UseShellExecute = false })?.Dispose();
    }

    /// <summary>
    /// Runs in the staged exe: waits for the old version to exit, keeps it as .old, copies itself over
    /// DurazznoApp.exe and starts it. If anything fails the old exe is put back and started, so the PC never
    /// ends up without Durazzno. The same happens when the new version exits with an error within
    /// <see cref="StartupGrace"/>.
    /// </summary>
    public static int RunFinish(string[] args)
    {
        var staged = Environment.ProcessPath!;
        // The exe being replaced (DurazznoAppDaemon.exe on versions before 3.0.1) and the name it gets now.
        var previous = staged.EndsWith(".staged", StringComparison.OrdinalIgnoreCase) ? staged[..^".staged".Length] : staged;
        var target = Path.Combine(Path.GetDirectoryName(previous)!, AppInfo.ExeFileName);
        var old = target + ".old";
        var open = args.Contains(OpenFlag);
        try
        {
            if (args.Length > 1 && int.TryParse(args[1], out var pid))
            {
                try
                {
                    using var running = Process.GetProcessById(pid);
                    running.WaitForExit(30_000);
                }
                catch (ArgumentException)
                {
                    // already gone
                }
            }

            Retry(() => File.Move(previous, old, overwrite: true));
            try
            {
                Retry(() => File.Copy(staged, target, overwrite: false));
            }
            catch
            {
                if (!File.Exists(previous) && File.Exists(old))
                    File.Move(old, previous);
                throw;
            }

            TryDeleteFile(StagedInfo);
            Log.Info($"Durazzno App {AppInfo.VersionText} installed");
            using var started = Process.Start(new ProcessStartInfo(target, open ? $"{UpdatedFlag} {OpenFlag}" : UpdatedFlag) { UseShellExecute = false });
            // The app keeps running in the tray, so exiting with an error this early means it crashed on start.
            if (started is not null && started.WaitForExit(StartupGrace) && started.ExitCode != 0 && File.Exists(old))
            {
                Log.Error($"Durazzno App {AppInfo.VersionText} exited with code {started.ExitCode} right after the update; going back to the previous version");
                RollBack(target, old, open);
                return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Finishing the update failed; starting the previous version", ex);
            var start = File.Exists(target) ? target : previous;
            if (File.Exists(start))
                Process.Start(new ProcessStartInfo(start, open ? OpenFlag : "--background") { UseShellExecute = false })?.Dispose();
            return 1;
        }
    }

    private static void RollBack(string target, string old, bool open)
    {
        try
        {
            Retry(() => File.Move(target, target + ".broken", overwrite: true));
            Retry(() => File.Move(old, target));
            // --updated keeps every version (also ones older than this code) from installing the update again right
            // away, which would loop; it is retried at the next start or on APPLY UPDATE. --update-failed shows why.
            var args = $"{UpdatedFlag} {FailedFlag}" + (open ? " " + OpenFlag : "");
            Process.Start(new ProcessStartInfo(target, args) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Going back to the previous version failed", ex);
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static Staged? ReadStaged()
    {
        try
        {
            if (!File.Exists(StagedInfo) || !File.Exists(StagedExe))
                return null;
            var staged = JsonSerializer.Deserialize<Staged>(File.ReadAllText(StagedInfo));
            return staged is not null && Version.TryParse(staged.Version, out var v) && AppInfo.Normalize(v) > AppInfo.Version ? staged : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task DownloadVerifiedAsync(Uri uri, string expectedSha256, long? expectedSize, string destination,
        IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new UpdateProgress("downloading", 0));
        try
        {
            var (source, length) = await ServerClient.OpenReadAsync(uri, ct);
            length ??= expectedSize;
            await using (source)
            await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                var lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    if (length is > 0)
                    {
                        var percent = (int)(total * 100 / length.Value);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report(new UpdateProgress("downloading", percent));
                        }
                    }
                }

                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("The download failed the integrity check.");
            }
        }
        catch (Exception ex) when (ex is not UpdateException and not OperationCanceledException)
        {
            TryDeleteFile(destination);
            throw new UpdateException($"Download failed: {ex.Message}", ex);
        }
        catch
        {
            TryDeleteFile(destination);
            throw;
        }
    }

    private static void EnsureInstallFolderWritable()
    {
        var probe = Path.Combine(AppInfo.BaseDir, $".write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new UpdateException(
                $"Durazzno can't write to {AppInfo.BaseDir}. Move it to a folder you own (for example %LOCALAPPDATA%\\Programs\\DurazznoApp) to enable updates.", ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
