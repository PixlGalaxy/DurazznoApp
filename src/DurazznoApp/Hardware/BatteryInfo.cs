using System.Management;
using System.Windows.Forms;

namespace Durazzno.Hardware;

/// <summary>Battery charge (live) and health: full-charge capacity against the design capacity, plus cycle count.</summary>
internal sealed record BatteryHealth(int DesignCapacity, int FullChargeCapacity, int? Cycles)
{
    public double Percent => DesignCapacity > 0 ? Math.Min(100, FullChargeCapacity * 100.0 / DesignCapacity) : 0;
}

internal static class BatteryInfo
{
    public static bool HasBattery => SystemInformation.PowerStatus.BatteryChargeStatus is not BatteryChargeStatus.NoSystemBattery and not BatteryChargeStatus.Unknown;

    /// <summary>Charge 0–100, whether it is plugged in / charging, and the time left on battery when Windows knows it.</summary>
    public static (int Percent, bool PluggedIn, bool Charging, TimeSpan? Remaining) Status()
    {
        var p = SystemInformation.PowerStatus;
        var remaining = p.BatteryLifeRemaining > 0 ? TimeSpan.FromSeconds(p.BatteryLifeRemaining) : (TimeSpan?)null;
        return ((int)Math.Round(p.BatteryLifePercent * 100), p.PowerLineStatus == PowerLineStatus.Online,
            p.BatteryChargeStatus.HasFlag(BatteryChargeStatus.Charging), remaining);
    }

    /// <summary>From the battery driver (root\WMI). Null when the driver does not report capacities.</summary>
    public static BatteryHealth? Health()
    {
        try
        {
            var design = First("SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity");
            var full = First("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
            if (design is not > 0 || full is not > 0)
                return null;
            var cycles = First("SELECT CycleCount FROM BatteryCycleCount", "CycleCount");
            return new BatteryHealth(design.Value, full.Value, cycles is > 0 ? cycles : null);
        }
        catch
        {
            return null;
        }
    }

    private static int? First(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", query);
            foreach (var o in searcher.Get())
                if (o[property] is { } value)
                    return Convert.ToInt32(value);
        }
        catch (ManagementException)
        {
            // class not provided by this battery driver
        }
        return null;
    }
}
