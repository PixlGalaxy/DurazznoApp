using System.Management;
using Durazzno.Core;

namespace Durazzno.Hardware.Drivers;

/// <summary>
/// Checks every device right after its driver is updated. Some packages pass all the checks before installing (the
/// catalog lists this Windows, the INF targets this build) and still do not work. Example: a 2025 VirtIO network
/// driver on Windows 10 LTSC loaded with Code 39 "Driver Entry Point Not Found" because its .sys needs a newer kernel.
/// A device that worked before the update and has a driver-load problem afterwards is put back on the driver it had
/// (still in the driver store). Runs in the elevated installer.
/// </summary>
internal static class DriverGuard
{
    /// <summary>A device that worked before the update, and the driver it was using.</summary>
    internal sealed record Healthy(string DeviceId, string Name, string InfName, string Version);

    // Problem codes that are not a broken driver: 14 = restart needed, 21 = being removed, 22 = disabled,
    // 47 = prepared for safe removal.
    private static readonly HashSet<int> NotAFault = [0, 14, 21, 22, 47];

    /// <summary>The devices the update targets that work now with a driver (the ones it could break).</summary>
    public static List<Healthy> Snapshot(IEnumerable<DeviceInfo> devices, string hardwareId) => devices
        .Where(d => d.IsPresent && d.ProblemCode == 0 && d.InfName is { Length: > 0 } && d.DriverVersion is { Length: > 0 }
                    && (d.HardwareIds.Contains(hardwareId, StringComparer.OrdinalIgnoreCase) || d.CompatibleIds.Contains(hardwareId, StringComparer.OrdinalIgnoreCase)))
        .Select(d => new Healthy(d.DeviceId, d.Name, d.InfName!, d.DriverVersion!))
        .ToList();

    /// <summary>
    /// Re-reads each device's status after the install. When one now fails to load its driver, it is rolled back to
    /// its previous driver. Returns the message for the row (null = everything works).
    /// </summary>
    public static (string Message, bool Reboot)? VerifyOrRollBack(List<Healthy> before)
    {
        if (before.Count == 0)
            return null;

        // Devices restart with the new driver right after the install; give them a moment to settle.
        Thread.Sleep(3000);
        var broken = new List<(Healthy Device, int Code)>();
        foreach (var device in before)
        {
            var code = ProblemCode(device.DeviceId);
            if (code is { } c && !NotAFault.Contains(c))
            {
                Thread.Sleep(2000); // still failing a moment later: it is not just restarting
                code = ProblemCode(device.DeviceId);
                if (code is { } again && !NotAFault.Contains(again))
                    broken.Add((device, again));
            }
        }
        if (broken.Count == 0)
            return null;

        var reboot = false;
        var failures = new List<string>();
        foreach (var (device, code) in broken)
        {
            Log.Warn($"DriverHub guard: {device.Name} has Code {code} after the update; rolling back to {device.InfName} {device.Version}");
            try
            {
                var previous = DriverHistory.ForDevice(new DeviceInfo { DeviceId = device.DeviceId, Name = device.Name })
                    .FirstOrDefault(e => e.InfName.Equals(device.InfName, StringComparison.OrdinalIgnoreCase) && e.Version == device.Version)
                    ?? throw new InvalidOperationException("the previous driver is no longer in the driver store");
                reboot |= DriverHistory.InstallStoreDriver(device.DeviceId, previous.InfPath, previous.RawVersion);
            }
            catch (Exception ex)
            {
                Log.Warn($"DriverHub guard: rollback of {device.Name} failed: {ex.Message}");
                failures.Add($"{device.Name}: {ex.Message}");
            }
        }

        var codes = string.Join(", ", broken.Select(b => b.Code).Distinct());
        var message = failures.Count == 0
            ? $"Rolled back: this driver doesn't work on this Windows (Code {codes}). The previous driver is back and this update is hidden."
            : $"This driver doesn't work on this Windows (Code {codes}) and rolling back failed ({string.Join("; ", failures)}). Use History → ROLL BACK.";
        return (message, reboot);
    }

    private static int? ProblemCode(string deviceId)
    {
        try
        {
            var escaped = deviceId.Replace("\\", "\\\\").Replace("'", "\\'");
            using var searcher = new ManagementObjectSearcher($"SELECT ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPDeviceID = '{escaped}'");
            foreach (var e in searcher.Get())
                return e["ConfigManagerErrorCode"] is uint code ? (int)code : 0;
            return null; // gone (it may be re-enumerating)
        }
        catch (Exception ex)
        {
            Log.Warn($"DriverHub guard: status of {deviceId}: {ex.Message}");
            return null;
        }
    }
}
