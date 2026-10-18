using Microsoft.Win32;

namespace Durazzno.Core;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights, no helper scripts).</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Durazzno App";
    private const string LegacyValueName = "DurazznoApp Daemon"; // before 3.0.1

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
        bool legacy;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            legacy = key?.GetValue(LegacyValueName) is string;
            if (!legacy && key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                SetEnabled(true);
        }
        if (legacy)
        {
            // Renamed from "DurazznoApp Daemon" (DurazznoAppDaemon.exe): move the entry to the new name and exe.
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                key.SetValue(ValueName, Command);
                Log.Info("Startup entry renamed to \"Durazzno App\"");
            }
            catch (Exception ex)
            {
                Log.Error("Could not rename the startup entry", ex);
            }
        }
    }
}
