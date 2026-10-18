using Microsoft.Win32;

namespace Durazzno.Core;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights, no helper scripts).</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DurazznoApp Daemon";

    private static string Command => $"\"{AppInfo.ExePath}\" {Program.BackgroundFlag}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, Command);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Could not change startup registration", ex);
        }
    }

    /// <summary>If the exe was moved, point the existing startup entry at the new location.</summary>
    public static void RepairPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            SetEnabled(true);
    }
}
