using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Durazzno.Core;

namespace Durazzno.Hardware.Drivers;

internal enum DriverSource { WindowsUpdate, Catalog, Intel, Nvidia, Amd, Dell, HP, Lenovo }

/// <summary>A newer driver for a device, offered by Windows Update or found in the Microsoft Update Catalog.</summary>
internal sealed record DriverUpdate(
    string UpdateId,
    string Title,
    string HardwareId,
    string Model,
    string Provider,
    DateTime? Date,
    string DriverClass,
    long DownloadBytes)
{
    public DriverSource Source { get; init; } = DriverSource.WindowsUpdate;

    /// <summary>Driver version when the source states it (the catalog does); otherwise parsed from the title.</summary>
    public string? Version { get; init; }

    // Manufacturer installers (Intel, NVIDIA).
    public string? DownloadUrl { get; init; }
    public string? Sha1 { get; init; }
    public string? Sha256 { get; init; }
    public string? Md5 { get; init; }

    /// <summary>Lenovo packages: unpack with this command, then run InstallCommand (%PACKAGEPATH% = unpack folder).</summary>
    public string? ExtractCommand { get; init; }
    public string? InstallCommand { get; init; }
    public string? InstallArgs { get; init; }
    public string? ExpectedSigner { get; init; }
    public string? DetailsUrl { get; init; }

    /// <summary>The maker does not allow a silent install: its installer window opens and the user steps through it.</summary>
    public bool Interactive { get; init; }

    /// <summary>
    /// Installed version of the maker's package when that is what gets compared (AMD Software / AMD Chipset Software
    /// versions such as 26.8.1 have nothing to do with the device driver's 32.0.x version).
    /// </summary>
    public string? InstalledPackage { get; init; }

    /// <summary>The download needs this Referer (AMD).</summary>
    public string? Referer { get; init; }

    [JsonIgnore]
    public bool IsManufacturer => Source is DriverSource.Intel or DriverSource.Nvidia or DriverSource.Amd
        or DriverSource.Dell or DriverSource.HP or DriverSource.Lenovo;

    [JsonIgnore]
    public Version? ParsedVersion => System.Version.TryParse(Version, out var v) ? v : DriverUpdates.ParseVersion(Title) ?? DriverUpdates.ParseVersion(Model);
}

/// <summary>What the elevated installer has to do: these updates, one after the other, in this order.</summary>
internal sealed record InstallJob(List<DriverUpdate> Items);

/// <summary>
/// One progress event from the elevated installer (a JSON line in the progress file). Phase: queued, downloading,
/// installing, done, error, rolledback (installed, did not work, previous driver restored), cancelled;
/// "restorepoint" carries the restore point outcome (UpdateId empty).
/// </summary>
internal sealed record InstallProgress(string UpdateId, string Phase, double? Percent = null, string? Message = null, bool RebootRequired = false);

internal sealed record InstallResult(string UpdateId, string Title, bool Success, string Message, bool RebootRequired);

/// <summary>
/// Driver updates through the Windows Update Agent API (the same IUpdateSearcher / IWindowsDriverUpdate interfaces
/// Driver Easy's WUA class uses), so every driver comes from Microsoft's catalog and is signed. Searching needs no
/// admin rights; installing runs in an elevated copy of the app (one UAC prompt per batch), which creates a system
/// restore point first, as Driver Easy does.
/// </summary>
internal static class DriverUpdates
{
    public const string InstallFlag = "--install-drivers";   // --install-drivers <job.json> <progress.jsonl>
    public const string BackupFlag = "--backup-drivers";     // --backup-drivers <folder>

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Asks Windows Update which drivers it would offer this PC. Can take a minute.</summary>
    public static Task<List<DriverUpdate>> SearchAsync(CancellationToken ct) => Task.Run(() =>
    {
        var updates = new List<DriverUpdate>();
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.Online = true;
        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        ct.ThrowIfCancellationRequested();

        foreach (dynamic u in result.Updates)
        {
            try
            {
                updates.Add(new DriverUpdate(
                    (string)u.Identity.UpdateID,
                    (string)u.Title,
                    (string)(u.DriverHardwareID ?? ""),
                    (string)(u.DriverModel ?? ""),
                    (string)(u.DriverProvider ?? ""),
                    u.DriverVerDate is DateTime date ? (DateTime?)date : null,
                    (string)(u.DriverClass ?? ""),
                    Convert.ToInt64(u.MaxDownloadSize)));
            }
            catch (Exception ex)
            {
                Log.Warn($"DriverHub update entry: {ex.Message}");
            }
        }

        return updates;
    }, ct);

    /// <summary>
    /// Links each update to every device whose hardware IDs (or, failing that, compatible IDs) it targets, keeping the
    /// newest offer per device across both sources.
    /// </summary>
    public static void Match(List<DeviceInfo> devices, IEnumerable<DriverUpdate> updates)
    {
        foreach (var d in devices)
            d.Update = null;

        foreach (var u in updates)
        {
            var targets = devices.Where(d => d.IsPresent && d.HardwareIds.Contains(u.HardwareId, StringComparer.OrdinalIgnoreCase)).ToList();
            if (targets.Count == 0)
                targets = devices.Where(d => d.IsPresent && d.CompatibleIds.Contains(u.HardwareId, StringComparer.OrdinalIgnoreCase)).ToList();

            foreach (var device in targets)
            {
                if (!IsNewer(u, device))
                    continue; // Windows Update sometimes offers an older "certified" driver: never downgrade.
                if (device.Update is null || IsBetter(u, device.Update))
                    device.Update = u;
            }
        }
    }

    /// <summary>Higher version wins, then newer date; on a tie Windows Update is preferred (no download step).</summary>
    private static bool IsBetter(DriverUpdate a, DriverUpdate b)
    {
        // AMD package versions (26.8.1) cannot be compared with driver versions (32.0.x): the maker's package wins.
        if ((a.Source == DriverSource.Amd) != (b.Source == DriverSource.Amd))
            return a.Source == DriverSource.Amd;
        if (a.ParsedVersion is { } va && b.ParsedVersion is { } vb && va != vb)
            return va > vb;
        if ((a.Date ?? DateTime.MinValue) != (b.Date ?? DateTime.MinValue))
            return (a.Date ?? DateTime.MinValue) > (b.Date ?? DateTime.MinValue);
        return a.Source == DriverSource.WindowsUpdate && b.Source != DriverSource.WindowsUpdate;
    }

    /// <summary>True only when the offered driver is newer than the installed one (version first, then date).</summary>
    public static bool IsNewer(DriverUpdate update, DeviceInfo device)
    {
        // Intel / NVIDIA state the Windows driver version: once it is installed (e.g. re-matching after an install),
        // the offer is no longer an update. AMD compares package versions, already checked by VendorDrivers.
        if (update.Source is DriverSource.Intel or DriverSource.Nvidia or DriverSource.Dell or DriverSource.HP or DriverSource.Lenovo)
            return !(Version.TryParse(update.Version, out var offeredVersion) && Version.TryParse(device.DriverVersion, out var installedVersion)
                     && offeredVersion <= installedVersion);
        if (update.IsManufacturer)
            return true;
        if (device.DriverVersion is null)
            return true; // No driver installed: anything helps.

        // Same version with a newer date counts too: Windows itself ranks drivers by date first.
        if (update.ParsedVersion is { } offered && Version.TryParse(device.DriverVersion, out var installed) && offered != installed)
            return offered > installed;

        return update.Date is { } date && device.DriverDate is { } current && date > current;
    }

    /// <summary>Driver titles end with the version, e.g. "Intel Corporation - Display - 31.0.101.4146" or "... (24.40.0.4)".</summary>
    internal static Version? ParseVersion(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+\.\d+(\.\d+){0,2})\)?\s*$");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var v) ? v : null;
    }

    /// <summary>
    /// Starts the elevated installer (one UAC prompt for the whole batch). It downloads, verifies and installs each
    /// update in turn and appends <see cref="InstallProgress"/> lines to <paramref name="progressFile"/>.
    /// Returns null when the UAC prompt was declined.
    /// </summary>
    public static Process? StartInstall(InstallJob job, string jobFile, string progressFile)
    {
        File.WriteAllText(jobFile, JsonSerializer.Serialize(job, Json));
        File.WriteAllText(progressFile, "");
        try
        {
            return Process.Start(new ProcessStartInfo(AppInfo.ExePath, $"{InstallFlag} \"{jobFile}\" \"{progressFile}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks a running install to stop: the current download is abandoned and the drivers still queued are skipped.
    /// A driver already installing always finishes (stopping a driver install halfway can leave the device broken).
    /// </summary>
    public static void RequestCancel(string jobFile)
    {
        try { File.WriteAllText(CancelFile(jobFile), ""); }
        catch (IOException) { }
    }

    private static string CancelFile(string jobFile) => jobFile + ".cancel";

    public static InstallProgress? ParseProgress(string line)
    {
        try { return JsonSerializer.Deserialize<InstallProgress>(line, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Exports every third-party driver package with pnputil (elevated). Returns an error or null.</summary>
    public static async Task<string?> BackupAsync(string folder)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(AppInfo.ExePath, $"{BackupFlag} \"{folder}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
                return "Could not start the backup.";
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? null : $"pnputil finished with code {process.ExitCode}.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Cancelled";
        }
    }

    // ── Elevated entry points (run from Program.Main) ─────────────────────────────────────

    public static int RunInstall(string jobFile, string progressFile) => RunInstallAsync(jobFile, progressFile).GetAwaiter().GetResult();

    private static async Task<int> RunInstallAsync(string jobFile, string progressFile)
    {
        using var writer = new StreamWriter(new FileStream(progressFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var gate = new object();
        void Report(InstallProgress p)
        {
            lock (gate)
                writer.WriteLine(JsonSerializer.Serialize(p, Json));
        }

        var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DurazznoApp", "Drivers", Guid.NewGuid().ToString("N")[..8]);
        using var cancel = new CancellationTokenSource();
        var watching = true;
        _ = Task.Run(async () =>
        {
            while (watching && !cancel.IsCancellationRequested)
            {
                if (File.Exists(CancelFile(jobFile)))
                    cancel.Cancel();
                await Task.Delay(300);
            }
        });
        try
        {
            var job = JsonSerializer.Deserialize<InstallJob>(File.ReadAllText(jobFile), Json)!;
            foreach (var u in job.Items)
                Report(new InstallProgress(u.UpdateId, "queued"));

            // Devices as they are now: after each install the ones that worked are checked again (DriverGuard).
            List<DeviceInfo> devices;
            try
            {
                devices = DeviceScanner.Scan();
            }
            catch (Exception ex)
            {
                Log.Warn($"DriverHub install: device scan for the guard failed: {ex.Message}");
                devices = [];
            }

            var restore = RestorePoint.Begin("Durazzno App: before driver updates");
            Report(new InstallProgress("", "restorepoint", Message: restore.Error));

            foreach (var u in job.Items)
            {
                if (cancel.IsCancellationRequested)
                {
                    Report(new InstallProgress(u.UpdateId, "cancelled", Message: "Cancelled before it started"));
                    continue;
                }

                InstallResult result;
                var healthy = DriverGuard.Snapshot(devices, u.HardwareId);
                try
                {
                    Report(new InstallProgress(u.UpdateId, "downloading", 0));
                    var lastPercent = -1;
                    void Bytes(long got, long total)
                    {
                        var percent = total > 0 ? (int)(got * 100 / total) : -1;
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            Report(new InstallProgress(u.UpdateId, "downloading", percent >= 0 ? percent : null));
                        }
                    }

                    switch (u.Source)
                    {
                        case DriverSource.WindowsUpdate:
                            result = InstallFromWindowsUpdate(u, () => Report(new InstallProgress(u.UpdateId, "installing")));
                            break;
                        case DriverSource.Catalog:
                            var inf = await DriverCatalog.PrepareAsync(u, work, Bytes, cancel.Token);
                            Report(new InstallProgress(u.UpdateId, "installing"));
                            result = InstallInf(inf);
                            break;
                        default:
                            var installer = await VendorDrivers.PrepareAsync(u, work, Bytes, cancel.Token);
                            Report(new InstallProgress(u.UpdateId, "installing"));
                            result = RunInstaller(installer, u.Source);
                            break;
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    Report(new InstallProgress(u.UpdateId, "cancelled", Message: "Download cancelled"));
                    continue;
                }
                catch (Exception ex)
                {
                    result = new InstallResult(u.UpdateId, u.Title, false, ex.Message, false);
                }

                if (result.Success && DriverGuard.VerifyOrRollBack(healthy) is { } rolledBack)
                {
                    Log.Info($"DriverHub install {u.Title}: rolled back ({rolledBack.Message})");
                    Report(new InstallProgress(u.UpdateId, "rolledback", 100, rolledBack.Message, rolledBack.Reboot));
                    continue;
                }

                Log.Info($"DriverHub install {u.Title}: {(result.Success ? "ok" : "failed")} ({result.Message})");
                Report(new InstallProgress(u.UpdateId, result.Success ? "done" : "error", 100, result.Message, result.RebootRequired));
            }

            RestorePoint.End(restore);
        }
        catch (Exception ex)
        {
            Log.Error("DriverHub install failed", ex);
            Report(new InstallProgress("", "error", Message: ex.Message));
        }
        finally
        {
            watching = false;
            try { Directory.Delete(work, true); } catch { }
            try { File.Delete(CancelFile(jobFile)); } catch { }
        }
        return 0;
    }

    private static InstallResult InstallFromWindowsUpdate(DriverUpdate update, Action installing)
    {
        try
        {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
            session.ClientApplicationID = "Durazzno App DriverHub";
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic found = searcher.Search($"UpdateID='{update.UpdateId}'");

            dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
            foreach (dynamic u in found.Updates)
            {
                if (!(bool)u.EulaAccepted)
                    u.AcceptEula();
                collection.Add(u);
            }
            if ((int)collection.Count == 0)
                return new InstallResult(update.UpdateId, update.Title, false, "Windows Update no longer offers this driver", false);

            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = collection;
            downloader.Download();

            installing();
            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = collection;
            dynamic outcome = installer.Install();
            dynamic r = outcome.GetUpdateResult(0);
            int code = r.ResultCode; // 2 = succeeded, 3 = succeeded with errors
            return new InstallResult(update.UpdateId, update.Title, code is 2 or 3,
                code switch { 2 => "Installed", 3 => "Installed with errors", 4 => "Failed", 5 => "Aborted", _ => $"Result {code}" },
                (bool)r.RebootRequired);
        }
        catch (Exception ex)
        {
            Log.Error("DriverHub Windows Update install failed", ex);
            return new InstallResult(update.UpdateId, update.Title, false, ex.Message, false);
        }
    }

    private const uint INSTALLFLAG_FORCE = 0x1;

    [DllImport("newdev.dll", EntryPoint = "UpdateDriverForPlugAndPlayDevicesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hardwareId, string fullInfPath, uint flags, out bool rebootRequired);

    /// <summary>
    /// Installs an unpacked catalog package on every present device with that hardware ID, like Device Manager's
    /// "Have Disk" (and Driver Easy's WinDDK.UpdateDriverForPlugAndPlayDevices call). FORCE is used because the
    /// package was already checked to be newer; Windows still enforces the driver signature.
    /// </summary>
    private static InstallResult InstallInf(CatalogInstall c)
    {
        try
        {
            if (UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, c.HardwareId, c.InfPath, INSTALLFLAG_FORCE, out var reboot))
                return new InstallResult(c.UpdateId, c.Title, true, "Installed", reboot);
            var error = Marshal.GetLastWin32Error();
            var message = unchecked((uint)error) switch
            {
                0xE000020B => "No device with this hardware ID is connected",
                0xE0000247 => "Windows rejected the driver signature",
                _ => new Win32Exception(error).Message,
            };
            return new InstallResult(c.UpdateId, c.Title, false, message, false);
        }
        catch (Exception ex)
        {
            return new InstallResult(c.UpdateId, c.Title, false, ex.Message, false);
        }
    }

    /// <summary>Runs a maker's installer (silent when the maker allows it) and maps its exit code.</summary>
    private static InstallResult RunInstaller(VendorInstall v, DriverSource source)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(v.ExePath, v.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(v.ExePath)!,
                WindowStyle = v.Interactive ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            })!;
            process.WaitForExit();
            return process.ExitCode switch
            {
                0 => new InstallResult(v.UpdateId, v.Title, true, "Installed", false),
                3010 or 1641 => new InstallResult(v.UpdateId, v.Title, true, "Installed", true),
                // Intel graphics: Installer.exe exits 14 ("success, restart needed") and the gfx_win_*.exe bootstrapper
                // passes it on as 1000 + code (1014; 1000 = plain success).
                14 or 1014 when source == DriverSource.Intel => new InstallResult(v.UpdateId, v.Title, true, "Installed", true),
                1000 when source == DriverSource.Intel => new InstallResult(v.UpdateId, v.Title, true, "Installed", false),
                1602 or 1223 => new InstallResult(v.UpdateId, v.Title, false, "Cancelled in the installer", false),
                var code => new InstallResult(v.UpdateId, v.Title, false, $"The installer finished with code {code}", false),
            };
        }
        catch (Exception ex)
        {
            return new InstallResult(v.UpdateId, v.Title, false, ex.Message, false);
        }
    }

    public static int RunBackup(string folder)
    {
        Directory.CreateDirectory(folder);
        using var process = Process.Start(new ProcessStartInfo("pnputil.exe", $"/export-driver * \"{folder}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    /// System restore point around the driver installs (DEVICE_DRIVER_INSTALL), opened before the first driver and
    /// closed after the last, through SRSetRestorePoint as Windows Setup does. Windows creates at most one every 24 hours
    /// and none when System Restore is off; the reason is reported, installs still go ahead.
    /// </summary>
    /// <summary>A restore point for a single change (rollback, restore from backup, cleanup).</summary>
    internal static string? CreateRestorePointFor(string description)
    {
        var handle = RestorePoint.Begin(description);
        RestorePoint.End(handle);
        return handle.Error;
    }

    private static class RestorePoint
    {
        public sealed record Handle(long Sequence, string? Error);

        public static Handle Begin(string description)
        {
            try
            {
                var info = new RestorePointInfo { dwEventType = BEGIN_SYSTEM_CHANGE, dwRestorePtType = DEVICE_DRIVER_INSTALL, szDescription = description };
                if (SRSetRestorePointW(ref info, out var status))
                {
                    Log.Info($"Restore point created (sequence {status.llSequenceNumber})");
                    return new Handle(status.llSequenceNumber, null);
                }
                var error = status.nStatus == 1058 ? "System Restore is turned off on this PC, so no restore point was created"
                    : $"No restore point was created ({new Win32Exception(status.nStatus).Message})";
                Log.Warn(error);
                return new Handle(-1, error);
            }
            catch (Exception ex)
            {
                Log.Warn($"Restore point skipped: {ex.GetType().Name} {ex.Message}");
                return new Handle(-1, $"No restore point was created ({ex.Message})");
            }
        }

        public static void End(Handle handle)
        {
            if (handle.Sequence < 0)
                return;
            try
            {
                var info = new RestorePointInfo { dwEventType = END_SYSTEM_CHANGE, llSequenceNumber = handle.Sequence, szDescription = "" };
                SRSetRestorePointW(ref info, out _);
            }
            catch (Exception ex)
            {
                Log.Warn($"Restore point not closed: {ex.Message}");
            }
        }

        private const int BEGIN_SYSTEM_CHANGE = 100;
        private const int END_SYSTEM_CHANGE = 101;
        private const int DEVICE_DRIVER_INSTALL = 10;

        // srrestoreptapi.h declares both structures with #pragma pack(1).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        private struct RestorePointInfo
        {
            public int dwEventType;
            public int dwRestorePtType;
            public long llSequenceNumber;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szDescription;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct StateMgrStatus
        {
            public int nStatus;
            public long llSequenceNumber;
        }

        [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
        private static extern bool SRSetRestorePointW(ref RestorePointInfo info, out StateMgrStatus status);
    }
}
