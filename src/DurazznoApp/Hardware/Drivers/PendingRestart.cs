using System.Text.Json;
using Durazzno.Core;

namespace Durazzno.Hardware.Drivers;

/// <summary>A driver update that installed and is waiting for Windows to restart.</summary>
internal sealed record PendingDriver(string UpdateId, string HardwareId, string Title, string? Version, DateTime Installed);

/// <summary>
/// Driver updates installed this Windows session that asked for a restart (kept in pending-restart.json). Until the PC
/// restarts the device still reports the old driver, so the same update would be offered again: DriverHub and the
/// background check leave these out and show "Restart Windows to finish" instead. An entry is dropped once Windows
/// has started after it was installed.
/// </summary>
internal static class PendingRestart
{
    private static string FilePath => Path.Combine(AppInfo.DataDir, "pending-restart.json");
    private static readonly object Gate = new();

    /// <summary>When this Windows session started.</summary>
    private static DateTime BootTime => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>The updates still waiting for a restart.</summary>
    public static List<PendingDriver> List()
    {
        lock (Gate)
        {
            var all = Read();
            var waiting = all.Where(p => p.Installed > BootTime).ToList();
            if (waiting.Count != all.Count)
                Write(waiting); // the PC restarted since: those are done
            return waiting;
        }
    }

    public static void Add(DriverUpdate update)
    {
        lock (Gate)
        {
            var list = Read().Where(p => p.Installed > BootTime && p.UpdateId != update.UpdateId).ToList();
            list.Add(new PendingDriver(update.UpdateId, update.HardwareId, update.Title, update.ParsedVersion?.ToString(), DateTime.Now));
            Write(list);
        }
    }

    /// <summary>The pending install an offer repeats: same update, or same hardware and version from another source.</summary>
    public static PendingDriver? Find(List<PendingDriver> pending, DriverUpdate update) =>
        pending.FirstOrDefault(p => p.UpdateId.Equals(update.UpdateId, StringComparison.OrdinalIgnoreCase)
            || (p.Version is not null && p.Version == update.ParsedVersion?.ToString()
                && p.HardwareId.Equals(update.HardwareId, StringComparison.OrdinalIgnoreCase)));

    private static List<PendingDriver> Read()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<PendingDriver>>(File.ReadAllText(FilePath)) ?? [] : [];
        }
        catch (Exception ex)
        {
            Log.Warn($"DriverHub: pending restart list unreadable: {ex.Message}");
            return [];
        }
    }

    private static void Write(List<PendingDriver> list)
    {
        try
        {
            if (list.Count == 0)
                File.Delete(FilePath);
            else
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
        }
        catch (Exception ex)
        {
            Log.Warn($"DriverHub: could not save the pending restart list: {ex.Message}");
        }
    }
}
