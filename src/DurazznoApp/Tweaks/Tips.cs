using Durazzno.Core;
using Durazzno.Hardware;
using Durazzno.Hardware.Drivers;
using Microsoft.Win32;

namespace Durazzno.Tweaks;

/// <summary>What a tip's button does.</summary>
internal enum TipAction { None, RestartWindows, OpenDiskMap, TurnOnSystemRestore, OpenDriverHub }

/// <summary>A suggestion for this PC, shown only when it applies.</summary>
internal sealed record Tip(string Icon, string Tone, string Title, string Text, TipAction Action = TipAction.None, string? ActionLabel = null);

/// <summary>Looks at this PC and lists what is worth doing (cheap checks: registry, drive space, uptime).</summary>
internal static class Tips
{
    public static List<Tip> ForThisPc(AppSettings settings)
    {
        var tips = new List<Tip>();

        var pending = PendingRestartReason();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (pending is not null)
            tips.Add(new("rotate-ccw", "Warning", "Restart to finish updates", pending, TipAction.RestartWindows, "RESTART NOW"));
        else if (uptime.TotalDays >= 7)
            tips.Add(new("rotate-ccw", "Info", $"On for {(int)uptime.TotalDays} days",
                "A restart now and then frees memory that programs never gave back and lets Windows finish its housekeeping.",
                TipAction.RestartWindows, "RESTART NOW"));

        try
        {
            var system = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            var free = (double)system.AvailableFreeSpace / system.TotalSize;
            if (free < 0.10)
                tips.Add(new("hard-drive", "Warning", $"{system.Name.TrimEnd('\\')} is almost full",
                    $"Only {SystemInfo.FormatBytes(system.AvailableFreeSpace)} free. Windows slows down and can't update with little space; see what's taking it.",
                    TipAction.OpenDiskMap, "OPEN DISKMAP"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // no system drive info: skip the tip
        }

        if (!SystemRestore.IsOn)
            tips.Add(new("shield-alert", "Warning", "System Restore is off",
                "Without it there is no way back from a bad driver or update. It uses a bit of disk space.",
                TipAction.TurnOnSystemRestore, "TURN ON"));

        var lastCheck = settings.DriverCheckLast;
        if (lastCheck is null || DateTime.Now - lastCheck.Value > TimeSpan.FromDays(30))
            tips.Add(new("cpu", "Info", "Drivers not checked in a while",
                lastCheck is { } at ? $"Last checked on {at:MMM d}. Newer drivers can fix bugs and add performance." : "DriverHub hasn't looked for driver updates on this PC yet.",
                TipAction.OpenDriverHub, "OPEN DRIVERHUB"));

        return tips;
    }

    /// <summary>Why Windows waits for a restart (Windows Update, servicing, or drivers DriverHub installed), or null.</summary>
    private static string? PendingRestartReason()
    {
        static bool KeyExists(string path)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                return key is not null;
            }
            catch
            {
                return false;
            }
        }

        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            return "Windows Update installed updates that finish when you restart.";
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            return "Windows installed components that finish when you restart.";
        var drivers = PendingRestart.List().Count;
        if (drivers > 0)
            return drivers == 1 ? "A driver DriverHub installed finishes when you restart." : $"{drivers} drivers DriverHub installed finish when you restart.";
        return null;
    }
}
