using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Durazzno.Core;
using Microsoft.Win32;

namespace Durazzno.Tweaks;

/// <summary>
/// Applies and undoes tweaks. Before a tweak first changes a value, the value as it was is kept in
/// tweaks-backup.json, so undoing puts back exactly what this PC had (for a tweak applied some other way, undo uses
/// Windows' default). Per-user values are written directly; HKEY_LOCAL_MACHINE ones go through reg.exe with one UAC
/// prompt per change.
/// </summary>
internal static class TweakEngine
{
    private sealed record Saved(string Hive, string Key, string Name, string? Data, string Kind);

    private static string BackupFile => Path.Combine(AppInfo.DataDir, "tweaks-backup.json");
    private static readonly object Gate = new();

    /// <summary>True when every value of the tweak is set.</summary>
    public static bool IsApplied(Tweak tweak) => tweak.Values.All(v => Same(Read(v.Hive, v.Key, v.Name), v.Applied));

    /// <summary>Tweak IDs with saved originals (applied by Durazzno): what UNDO ALL reverts.</summary>
    public static IReadOnlyCollection<string> AppliedByDurazzno()
    {
        lock (Gate)
            return LoadBackup().Keys.ToList();
    }

    /// <summary>Turns a tweak on. Returns an error, "Cancelled" (UAC declined) or null.</summary>
    public static async Task<string?> ApplyAsync(Tweak tweak)
    {
        lock (Gate)
        {
            var backup = LoadBackup();
            if (!backup.ContainsKey(tweak.Id))
            {
                backup[tweak.Id] = tweak.Values.Select(v =>
                {
                    var current = Read(v.Hive, v.Key, v.Name);
                    return new Saved(v.Hive.ToString(), v.Key, v.Name, current is null ? null : ToText(current), v.Kind.ToString());
                }).ToList();
                SaveBackup(backup);
            }
        }

        var error = await WriteAsync(tweak.Values.Select(v => (v, (object?)v.Applied)).ToList());
        if (error is not null)
        {
            lock (Gate)
            {
                // Nothing changed: forget the snapshot so a later apply takes a fresh one.
                var backup = LoadBackup();
                if (!IsApplied(tweak) && backup.Remove(tweak.Id))
                    SaveBackup(backup);
            }
            return error;
        }
        tweak.Refresh?.Invoke();
        Log.Info($"WinTweaks: applied {tweak.Id}");
        return null;
    }

    /// <summary>Turns a tweak off: puts back the saved values, or Windows' defaults. Returns an error, "Cancelled" or null.</summary>
    public static async Task<string?> UndoAsync(Tweak tweak)
    {
        List<Saved>? saved;
        lock (Gate)
            saved = LoadBackup().GetValueOrDefault(tweak.Id);

        var targets = tweak.Values.Select(v =>
        {
            var original = saved?.FirstOrDefault(s => s.Key == v.Key && s.Name == v.Name);
            object? value = original is null ? v.Default : original.Data is null ? null : FromText(original.Data, v.Kind);
            return (v, value);
        }).ToList();

        var error = await WriteAsync(targets);
        if (error is not null)
            return error;

        lock (Gate)
        {
            var backup = LoadBackup();
            if (backup.Remove(tweak.Id))
                SaveBackup(backup);
        }
        tweak.Refresh?.Invoke();
        Log.Info($"WinTweaks: undid {tweak.Id}");
        return null;
    }

    /// <summary>Restarts File Explorer (taskbar, Start, open folders) so Explorer tweaks show.</summary>
    public static void RestartExplorer()
    {
        foreach (var explorer in Process.GetProcessesByName("explorer"))
        {
            using (explorer)
            {
                try
                {
                    explorer.Kill();
                    explorer.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // already gone
                }
            }
        }
        // Windows usually brings the shell back by itself; start it if it did not.
        Thread.Sleep(1500);
        if (Process.GetProcessesByName("explorer").Length == 0)
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
    }

    // ── Registry ─────────────────────────────────────────────────────────────────────────

    /// <summary>Sets (or, for null, deletes) the values. Machine values go through one elevated reg.exe run.</summary>
    private static async Task<string?> WriteAsync(List<(RegValue Value, object? Data)> targets)
    {
        try
        {
            foreach (var (v, data) in targets.Where(t => t.Value.Hive != RegistryHive.LocalMachine))
            {
                using var root = RegistryKey.OpenBaseKey(v.Hive, RegistryView.Registry64);
                if (data is null)
                {
                    if (v.DeleteKeyOnUndo is { } key)
                        root.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
                    else
                        root.OpenSubKey(v.Key, writable: true)?.DeleteValue(v.Name, throwOnMissingValue: false);
                }
                else
                {
                    using var key = root.CreateSubKey(v.Key, writable: true);
                    key.SetValue(v.Name, data, v.Kind);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return ex.Message;
        }

        var machine = targets.Where(t => t.Value.Hive == RegistryHive.LocalMachine).ToList();
        if (machine.Count == 0)
            return null;

        var commands = machine.Select(t =>
        {
            var path = $"\"HKLM\\{t.Value.Key}\"";
            var name = t.Value.Name.Length == 0 ? "/ve" : $"/v \"{t.Value.Name}\"";
            if (t.Data is null)
                return $"reg delete {path} {name} /f";
            var type = t.Value.Kind == RegistryValueKind.DWord ? "REG_DWORD" : "REG_SZ";
            return $"reg add {path} {name} /t {type} /d \"{ToText(t.Data)}\" /f";
        });
        try
        {
            // Deleting a value that is not there fails harmlessly, so each command runs regardless of the one before.
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", commands))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
                return "Could not start reg.exe.";
            await process.WaitForExitAsync();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Cancelled";
        }

        // Check the result instead of trusting the exit code of the last command.
        var wrong = machine.FirstOrDefault(t => t.Data is null ? Read(RegistryHive.LocalMachine, t.Value.Key, t.Value.Name) is not null
                                                              : !Same(Read(RegistryHive.LocalMachine, t.Value.Key, t.Value.Name), t.Data));
        return wrong.Value is null ? null : "Windows did not accept the change.";
    }

    private static object? Read(RegistryHive hive, string key, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var sub = root.OpenSubKey(key);
            return sub?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch
        {
            return null;
        }
    }

    private static bool Same(object? current, object expected) => current is not null && ToText(current) == ToText(expected);

    private static string ToText(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static object FromText(string text, RegistryValueKind kind) =>
        kind == RegistryValueKind.DWord ? int.Parse(text, CultureInfo.InvariantCulture) : text;

    // ── Backup file ──────────────────────────────────────────────────────────────────────

    private static Dictionary<string, List<Saved>> LoadBackup()
    {
        try
        {
            return File.Exists(BackupFile) ? JsonSerializer.Deserialize<Dictionary<string, List<Saved>>>(File.ReadAllText(BackupFile)) ?? [] : [];
        }
        catch (Exception ex)
        {
            Log.Warn($"WinTweaks: backup unreadable: {ex.Message}");
            return [];
        }
    }

    private static void SaveBackup(Dictionary<string, List<Saved>> backup)
    {
        try
        {
            if (backup.Count == 0)
                File.Delete(BackupFile);
            else
                File.WriteAllText(BackupFile, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn($"WinTweaks: backup not saved: {ex.Message}");
        }
    }
}

/// <summary>Tells Windows about settings written to the registry, so they work without signing out.</summary>
internal static class SystemSettings
{
    private const uint SpiSetMouse = 0x0004;
    private const uint SpiSetMenuShowDelay = 0x006B;
    private const uint SpifSendChange = 0x0002;

    public static void ApplyMouse()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
        int Get(string name, int fallback) => int.TryParse(key?.GetValue(name) as string, out var v) ? v : fallback;
        var values = new[] { Get("MouseThreshold1", 6), Get("MouseThreshold2", 10), Get("MouseSpeed", 1) };
        SystemParametersInfo(SpiSetMouse, 0, values, SpifSendChange);
    }

    public static void ApplyMenuDelay()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        var delay = uint.TryParse(key?.GetValue("MenuShowDelay") as string, out var v) ? v : 400;
        SystemParametersInfo(SpiSetMenuShowDelay, delay, IntPtr.Zero, SpifSendChange);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] values, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr values, uint winIni);
}
