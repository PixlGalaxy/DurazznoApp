using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Durazzno.Core;

namespace Durazzno.Hardware.Drivers;

internal sealed record RemoveResult(string DeviceId, bool Success, string Message);

/// <summary>
/// Unused devices, the way Uwe Sieber's DeviceCleanup finds them: devices Windows still remembers but that are not
/// connected (old USB sticks, monitors, phones, VPN adapters...). WMI does not list them, so they are enumerated with
/// SetupAPI including non-present devices; "last connected" comes from DEVPKEY_Device_LastRemovalDate / LastArrivalDate.
/// Removing one (CM_Uninstall_DevNode, admin) deletes the leftover entry and its driver binding; if the device is
/// plugged in again, Windows simply installs it again.
/// </summary>
internal static class DeviceCleanup
{
    public const string RemoveFlag = "--remove-devices"; // --remove-devices <ids.txt> <result.json>

    /// <summary>Non-present devices, skipping any ID already listed as present.</summary>
    public static List<DeviceInfo> ScanUnused(ISet<string> presentIds)
    {
        var list = new List<DeviceInfo>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == INVALID_HANDLE_VALUE)
            return list;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                try
                {
                    // Present devices have a live devnode; leftovers answer CR_NO_SUCH_DEVINST.
                    if (CM_Get_DevNode_Status(out _, out _, data.DevInst, 0) != CR_NO_SUCH_DEVINST)
                        continue;
                    var id = InstanceId(set, ref data);
                    if (id is null || presentIds.Contains(id))
                        continue;

                    var name = RegistryString(set, ref data, SPDRP_FRIENDLYNAME) ?? RegistryString(set, ref data, SPDRP_DEVICEDESC) ?? id;
                    list.Add(new DeviceInfo
                    {
                        DeviceId = id,
                        Name = name,
                        ClassName = RegistryString(set, ref data, SPDRP_CLASS) ?? "Other",
                        Manufacturer = RegistryString(set, ref data, SPDRP_MFG) ?? "",
                        HardwareIds = RegistryStrings(set, ref data, SPDRP_HARDWAREID),
                        IsPresent = false,
                        LastSeen = DateProperty(set, ref data, LastRemovalDate) ?? DateProperty(set, ref data, LastArrivalDate),
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"Unused device entry: {ex.Message}");
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    /// <summary>Removes the devices through an elevated helper (one UAC prompt). Null when UAC was declined.</summary>
    public static async Task<List<RemoveResult>?> RemoveAsync(IReadOnlyCollection<string> deviceIds)
    {
        var idsFile = Path.Combine(Path.GetTempPath(), $"DurazznoRemove-{Guid.NewGuid():N}.txt");
        var resultFile = Path.ChangeExtension(idsFile, ".json");
        try
        {
            await File.WriteAllLinesAsync(idsFile, deviceIds);
            using var process = Process.Start(new ProcessStartInfo(AppInfo.ExePath, $"{RemoveFlag} \"{idsFile}\" \"{resultFile}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
                return [];
            await process.WaitForExitAsync();
            return File.Exists(resultFile) ? JsonSerializer.Deserialize<List<RemoveResult>>(await File.ReadAllTextAsync(resultFile)) : [];
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null;
        }
        finally
        {
            try { File.Delete(idsFile); } catch { }
            try { File.Delete(resultFile); } catch { }
        }
    }

    /// <summary>Elevated entry point.</summary>
    public static int RunRemove(string idsFile, string resultFile)
    {
        var results = new List<RemoveResult>();
        foreach (var id in File.ReadAllLines(idsFile).Where(l => l.Length > 0))
        {
            try
            {
                var cr = CM_Locate_DevNodeW(out var inst, id, CM_LOCATE_DEVNODE_PHANTOM);
                if (cr == CR_SUCCESS)
                    cr = CM_Uninstall_DevNode(inst, 0);
                results.Add(new RemoveResult(id, cr == CR_SUCCESS, cr switch
                {
                    CR_SUCCESS => "Removed",
                    CR_ACCESS_DENIED => "Admin rights are required",
                    CR_NO_SUCH_DEVNODE => "Already gone",
                    _ => $"Windows refused (CONFIGRET {cr})",
                }));
            }
            catch (Exception ex)
            {
                results.Add(new RemoveResult(id, false, ex.Message));
            }
        }
        Log.Info($"DriverHub: removed {results.Count(r => r.Success)} of {results.Count} unused devices");
        File.WriteAllText(resultFile, JsonSerializer.Serialize(results));
        return 0;
    }

    // ── SetupAPI / CfgMgr32 ───────────────────────────────────────────────────────────────

    private static string? InstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var buffer = new char[512];
        return SetupDiGetDeviceInstanceIdW(set, ref data, buffer, buffer.Length, out var required)
            ? new string(buffer, 0, Math.Max(0, required - 1))
            : null;
    }

    private static string? RegistryString(IntPtr set, ref SP_DEVINFO_DATA data, uint property) =>
        RegistryStrings(set, ref data, property).FirstOrDefault();

    private static string[] RegistryStrings(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, (uint)buffer.Length, out var required) || required < 2)
            return [];
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)required)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static readonly Guid DevicePropertyGuid = new("83da6326-97a6-4088-9453-a1923f573b29");
    private static readonly DEVPROPKEY LastArrivalDate = new() { fmtid = DevicePropertyGuid, pid = 102 };
    private static readonly DEVPROPKEY LastRemovalDate = new() { fmtid = DevicePropertyGuid, pid = 103 };

    private static DateTime? DateProperty(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var buffer = new byte[8];
        var k = key;
        if (!SetupDiGetDevicePropertyW(set, ref data, ref k, out var type, buffer, 8, out _, 0) || type != DEVPROP_TYPE_FILETIME)
            return null;
        var ticks = BitConverter.ToInt64(buffer, 0);
        return ticks > 0 ? DateTime.FromFileTime(ticks) : null;
    }

    private const uint DIGCF_ALLCLASSES = 0x4;
    private const uint SPDRP_DEVICEDESC = 0x0, SPDRP_HARDWAREID = 0x1, SPDRP_CLASS = 0x7, SPDRP_MFG = 0xB, SPDRP_FRIENDLYNAME = 0xC;
    private const uint DEVPROP_TYPE_FILETIME = 0x10;
    private const uint CM_LOCATE_DEVNODE_PHANTOM = 0x1;
    private const int CR_SUCCESS = 0, CR_NO_SUCH_DEVNODE = 0x0D, CR_NO_SUCH_DEVINST = 0x0D, CR_ACCESS_DENIED = 0x33;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, char[] id, int size, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint regType,
        byte[] buffer, uint size, out uint required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type,
        byte[] buffer, uint size, out uint required, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Uninstall_DevNode(uint devInst, uint flags);
}
