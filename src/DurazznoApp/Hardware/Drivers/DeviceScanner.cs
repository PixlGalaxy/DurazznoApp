using System.Globalization;
using System.Management;
using Microsoft.Win32;
using Durazzno.Core;

namespace Durazzno.Hardware.Drivers;

/// <summary>A device and the driver it is using.</summary>
internal sealed class DeviceInfo
{
    public string DeviceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string[] HardwareIds { get; init; } = [];
    public string[] CompatibleIds { get; init; } = [];

    /// <summary>Configuration Manager error code (0 = working, 28 = no driver installed, ...).</summary>
    public int ProblemCode { get; init; }

    public string? DriverVersion { get; init; }
    public DateTime? DriverDate { get; init; }
    public string? DriverProvider { get; init; }
    public string? InfName { get; init; }
    /// <summary>The hardware or compatible ID the current driver was matched on.</summary>
    public string? MatchingDeviceId { get; init; }
    public bool? Signed { get; init; }

    /// <summary>Update offered by Windows Update for this device, if any.</summary>
    public DriverUpdate? Update { get; set; }

    /// <summary>False for an unused device: remembered by Windows but not connected (see DeviceCleanup).</summary>
    public bool IsPresent { get; init; } = true;

    /// <summary>When an unused device was last connected (or last removed), if Windows recorded it.</summary>
    public DateTime? LastSeen { get; init; }

    /// <summary>Code 22 is "disabled by the user": not a fault.</summary>
    public bool IsDisabled => ProblemCode == 22;

    public bool HasProblem => IsPresent && ProblemCode != 0 && !IsDisabled;
    public bool MissingDriver => ProblemCode is 28 or 1 or 18;
    /// <summary>
    /// Older than 4 years. Windows' own in-box drivers carry a fixed placeholder date (2006-06-21) even when current,
    /// so Microsoft-provided drivers are never flagged.
    /// </summary>
    public bool IsOld => IsPresent && DriverDate is { } d && d < DateTime.Now.AddYears(-4)
        && !(DriverProvider?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ?? false)
        && d != new DateTime(2006, 6, 21);

    /// <summary>Plumbing devices most people never need to look at (shown with "Show system devices").</summary>
    public bool IsSystemPlumbing => SystemClasses.Contains(ClassName) || DeviceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)
        || DeviceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) || DeviceId.StartsWith(@"STORAGE\VOLUME", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> SystemClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "SoftwareDevice", "SoftwareComponent", "Volume", "VolumeSnapshot", "LegacyDriver", "Computer",
        "Processor", "AudioEndpoint", "PrintQueue", "WPD", "Firmware", "SecurityDevices", "Extension",
    };

    public static string Problem(int code) => code switch
    {
        0 => "Working properly",
        1 or 18 => "Driver needs to be reinstalled",
        3 => "Driver may be corrupted",
        10 => "Device cannot start",
        12 => "Not enough free resources",
        14 => "Restart required",
        19 => "Registry configuration is damaged",
        21 or 24 => "Device is not present or not working",
        22 => "Device is disabled",
        28 => "No driver installed",
        31 => "Windows cannot load the driver",
        39 => "Driver is corrupted or missing",
        43 => "Windows stopped the device (it reported a problem)",
        45 => "Device is not connected",
        52 => "Driver signature could not be verified",
        _ => $"Problem code {code}",
    };
}

/// <summary>
/// Device inventory through WMI, the same sources Driver Easy uses: Win32_PnPEntity for every device and its
/// Configuration Manager error code, joined with Win32_PnPSignedDriver for the installed driver's version, date,
/// provider, INF and signature.
/// </summary>
internal static class DeviceScanner
{
    public static List<DeviceInfo> Scan()
    {
        var drivers = new Dictionary<string, ManagementBaseObject>(StringComparer.OrdinalIgnoreCase);
        using (var searcher = new ManagementObjectSearcher("SELECT DeviceID, DriverVersion, DriverDate, DriverProviderName, InfName, IsSigned, DeviceClass FROM Win32_PnPSignedDriver"))
        {
            foreach (var d in searcher.Get())
                if (d["DeviceID"] is string id)
                    drivers[id] = d;
        }

        var list = new List<DeviceInfo>();
        using var entities = new ManagementObjectSearcher("SELECT PNPDeviceID, Name, PNPClass, Manufacturer, HardwareID, CompatibleID, ConfigManagerErrorCode, Present FROM Win32_PnPEntity");
        foreach (var e in entities.Get())
        {
            try
            {
                if (e["Present"] is false || e["PNPDeviceID"] is not string id)
                    continue;

                drivers.TryGetValue(id, out var drv);
                list.Add(new DeviceInfo
                {
                    DeviceId = id,
                    Name = (e["Name"] as string)?.Trim() is { Length: > 0 } n ? n : "Unknown device",
                    ClassName = e["PNPClass"] as string ?? drv?["DeviceClass"] as string ?? "Other",
                    Manufacturer = (e["Manufacturer"] as string)?.Trim() ?? "",
                    HardwareIds = e["HardwareID"] as string[] ?? [],
                    CompatibleIds = e["CompatibleID"] as string[] ?? [],
                    ProblemCode = e["ConfigManagerErrorCode"] is uint code ? (int)code : 0,
                    DriverVersion = drv?["DriverVersion"] as string,
                    DriverDate = ParseCimDate(drv?["DriverDate"] as string),
                    DriverProvider = drv?["DriverProviderName"] as string,
                    InfName = drv?["InfName"] as string,
                    Signed = drv?["IsSigned"] as bool?,
                    MatchingDeviceId = drv is null ? null : ReadMatchingDeviceId(id),
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"DriverHub device: {ex.Message}");
            }
        }

        // Devices Windows remembers but that are not connected: WMI leaves them out.
        try
        {
            list.AddRange(DeviceCleanup.ScanUnused(list.Select(d => d.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase)));
        }
        catch (Exception ex)
        {
            Log.Warn($"DriverHub unused devices: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// The ID the installed driver was matched on, from the driver's class key. (Win32_PnPSignedDriver.HardWareID
    /// only repeats the first hardware ID.) Readable without admin rights.
    /// </summary>
    private static string? ReadMatchingDeviceId(string deviceId)
    {
        try
        {
            using var device = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceId}");
            if (device?.GetValue("Driver") is not string driver)
                return null;
            using var cls = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driver}");
            return cls?.GetValue("MatchingDeviceId") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>CIM datetime "yyyyMMddHHmmss.ffffff+zzz" → date.</summary>
    private static DateTime? ParseCimDate(string? value) =>
        value is { Length: >= 8 } && DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
