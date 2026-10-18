using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Durazzno.Core;

namespace Durazzno.Hardware.Drivers;

/// <summary>A driver for a device that is in the Windows driver store: the installed one or an older/alternative one.</summary>
internal sealed record DriverVersionEntry(
    string Description,
    string Provider,
    string Version,
    DateTime? Date,
    string InfPath,
    ulong RawVersion,
    bool IsCurrent,
    DateTime? AddedToStore)
{
    public string InfName => Path.GetFileName(InfPath);
}

/// <summary>
/// Driver history and rollback. Every driver Windows ever installed for a device stays in the driver store until it is
/// cleaned up, so the versions a device can go back to are its compatible drivers in the store
/// (SetupDiBuildDriverInfoList, the list Device Manager's "Update driver → Let me pick" shows). Rolling back installs
/// exactly that driver on exactly that device (DiInstallDevice, elevated), with a restore point first.
/// </summary>
internal static class DriverHistory
{
    public const string RollbackFlag = "--rollback-driver"; // --rollback-driver <deviceId> <infPath> <rawVersion> <result.txt>

    /// <summary>The device's drivers in the store, newest first; IsCurrent marks the installed one.</summary>
    public static List<DriverVersionEntry> ForDevice(DeviceInfo device)
    {
        var list = new List<DriverVersionEntry>();
        var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == INVALID_HANDLE_VALUE)
            return list;
        try
        {
            var dev = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, device.DeviceId, IntPtr.Zero, 0, ref dev) || !SetupDiBuildDriverInfoList(set, ref dev, SPDIT_COMPATDRIVER))
                return list;

            var drv = new SP_DRVINFO_DATA_V2 { cbSize = (uint)Marshal.SizeOf<SP_DRVINFO_DATA_V2>() };
            var current = device.InfName is null ? null : Path.GetFileName(device.InfName);
            for (uint i = 0; SetupDiEnumDriverInfoW(set, ref dev, SPDIT_COMPATDRIVER, i, ref drv); i++)
            {
                var inf = InfPath(set, ref dev, ref drv);
                if (inf is null)
                    continue;
                var version = FormatVersion(drv.DriverVersion);
                var isCurrent = string.Equals(Path.GetFileName(inf), current, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(version, device.DriverVersion, StringComparison.OrdinalIgnoreCase);
                if (list.Any(e => e.InfPath.Equals(inf, StringComparison.OrdinalIgnoreCase) && e.RawVersion == drv.DriverVersion))
                    continue;
                list.Add(new DriverVersionEntry(drv.Description, drv.ProviderName, version,
                    drv.DriverDate > 0 ? DateTime.FromFileTimeUtc(drv.DriverDate).Date : null, inf, drv.DriverVersion, isCurrent,
                    File.Exists(inf) ? File.GetCreationTime(inf) : null));
            }
            SetupDiDestroyDriverInfoList(set, ref dev, SPDIT_COMPATDRIVER);
        }
        catch (Exception ex)
        {
            Log.Warn($"Driver history for {device.Name}: {ex.Message}");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list.OrderByDescending(e => e.IsCurrent).ThenByDescending(e => e.RawVersion).ToList();
    }

    /// <summary>Other versions from the same provider as the installed driver: the device's real history.</summary>
    public static List<DriverVersionEntry> Previous(IEnumerable<DriverVersionEntry> all, DeviceInfo device) =>
        all.Where(e => !e.IsCurrent && SameProvider(e.Provider, device.DriverProvider)).ToList();

    private static bool SameProvider(string a, string? b)
    {
        static string Key(string? s) => new((s ?? "").TakeWhile(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return Key(a).Length > 0 && Key(a) == Key(b);
    }

    /// <summary>Installs the given store driver on the device (elevated, one UAC prompt). Returns an error, "Cancelled" or null.</summary>
    public static async Task<(string? Error, bool Reboot)> RollbackAsync(DeviceInfo device, DriverVersionEntry entry)
    {
        var result = Path.Combine(Path.GetTempPath(), $"DurazznoRollback-{Guid.NewGuid():N}.txt");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(AppInfo.ExePath,
                $"{RollbackFlag} \"{device.DeviceId}\" \"{entry.InfPath}\" {entry.RawVersion} \"{result}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
                return ("Could not start the rollback.", false);
            await process.WaitForExitAsync();
            var text = File.Exists(result) ? (await File.ReadAllTextAsync(result)).Trim() : "The rollback did not report back.";
            return text switch
            {
                "ok" => (null, false),
                "ok-reboot" => (null, true),
                _ => (text, false),
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ("Cancelled", false);
        }
        finally
        {
            try { File.Delete(result); } catch { }
        }
    }

    /// <summary>Elevated entry point.</summary>
    public static int RunRollback(string deviceId, string infPath, string rawVersion, string resultFile)
    {
        string outcome;
        try
        {
            DriverUpdates.CreateRestorePointFor($"Durazzno App: before rolling back a driver");
            var reboot = InstallStoreDriver(deviceId, infPath, ulong.Parse(rawVersion));
            outcome = reboot ? "ok-reboot" : "ok";
        }
        catch (Exception ex)
        {
            outcome = ex.Message;
            Log.Warn($"DriverHub rollback failed: {ex.Message}");
        }
        File.WriteAllText(resultFile, outcome);
        return 0;
    }

    /// <summary>
    /// Installs one driver from the store on one device (DiInstallDevice). Needs admin rights: called by the elevated
    /// rollback and by the install guard. Returns whether Windows asks for a restart; throws on failure.
    /// </summary>
    internal static bool InstallStoreDriver(string deviceId, string infPath, ulong wanted)
    {
        var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        try
        {
            var dev = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, deviceId, IntPtr.Zero, 0, ref dev) || !SetupDiBuildDriverInfoList(set, ref dev, SPDIT_COMPATDRIVER))
                throw new InvalidOperationException("The device is no longer available.");

            var drv = new SP_DRVINFO_DATA_V2 { cbSize = (uint)Marshal.SizeOf<SP_DRVINFO_DATA_V2>() };
            var found = false;
            for (uint i = 0; SetupDiEnumDriverInfoW(set, ref dev, SPDIT_COMPATDRIVER, i, ref drv); i++)
            {
                if (drv.DriverVersion == wanted && string.Equals(InfPath(set, ref dev, ref drv), infPath, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                throw new InvalidOperationException("That driver is no longer in the driver store.");
            if (!DiInstallDevice(IntPtr.Zero, set, ref dev, ref drv, 0, out var reboot))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Log.Info($"DriverHub: rolled back {deviceId} to {infPath} {FormatVersion(wanted)}");
            return reboot;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string FormatVersion(ulong v) => $"{v >> 48}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";

    /// <summary>SP_DRVINFO_DETAIL_DATA_W.InfFileName (x64 layout: cbSize 1584, InfFileName at byte 544).</summary>
    private static string? InfPath(IntPtr set, ref SP_DEVINFO_DATA dev, ref SP_DRVINFO_DATA_V2 drv)
    {
        var buffer = new byte[8192];
        BitConverter.GetBytes(IntPtr.Size == 8 ? 1584 : 1570).CopyTo(buffer, 0);
        if (!SetupDiGetDriverInfoDetailW(set, ref dev, ref drv, buffer, buffer.Length, out _))
            return null;
        var offset = IntPtr.Size == 8 ? 544 : 540;
        var text = System.Text.Encoding.Unicode.GetString(buffer, offset, 520);
        var end = text.IndexOf('\0');
        return end > 0 ? text[..end] : null;
    }

    private const uint SPDIT_COMPATDRIVER = 2;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DRVINFO_DATA_V2
    {
        public uint cbSize;
        public uint DriverType;
        public UIntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string MfgName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProviderName;
        public long DriverDate;
        public ulong DriverVersion;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwnd, uint flags, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiBuildDriverInfoList(IntPtr set, ref SP_DEVINFO_DATA data, uint driverType);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDriverInfoW(IntPtr set, ref SP_DEVINFO_DATA data, uint driverType, uint index, ref SP_DRVINFO_DATA_V2 driver);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDriverInfoDetailW(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DRVINFO_DATA_V2 driver, byte[] detail, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDriverInfoList(IntPtr set, ref SP_DEVINFO_DATA data, uint driverType);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("newdev.dll", SetLastError = true)]
    private static extern bool DiInstallDevice(IntPtr hwnd, IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DRVINFO_DATA_V2 driver, uint flags, out bool reboot);
}
