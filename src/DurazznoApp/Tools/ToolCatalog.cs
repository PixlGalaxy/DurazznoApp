using System.Diagnostics;
using Durazzno.Core;

namespace Durazzno.Tools;

/// <param name="Id">Stable key (used for navigation).</param>
/// <param name="Name">Name shown in the app.</param>
/// <param name="Tagline">One line describing what the tool does.</param>
/// <param name="Icon">Lucide icon name (Assets\Lucide).</param>
/// <param name="Accent">Accent colour as #RRGGBB.</param>
internal sealed record ToolDefinition(string Id, string Name, string Tagline, string Icon, string Accent);

internal static class ToolCatalog
{
    /// <summary>Navigation / menu order.</summary>
    public static readonly IReadOnlyList<ToolDefinition> All =
    [
        new("diskinfo", "DiskInfo", "Disk health, temperature and S.M.A.R.T. data", "hard-drive", "#3B82F6"),
        new("diskmark", "DiskMark", "Sequential and random read/write speed tests", "gauge", "#10B981"),
        new("diskmap", "DiskMap", "See what is taking up space on your drives", "chart-pie", "#F59E0B"),
        new("startuphub", "StartupHub", "Everything that starts with Windows, in one list", "rocket", "#EF4444"),
        new("timerresolution", "TimerResolution", "Windows timer resolution for lower input latency", "timer", "#8B5CF6"),
        new("driverhub", "DriverHub", "Find outdated and missing drivers", "cpu", "#06B6D4"),
    ];

    public static ToolDefinition? Find(string id) => All.FirstOrDefault(t => t.Id == id);
}

/// <summary>Opens URLs and folders with the shell.</summary>
internal static class Launcher
{
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {url}", ex);
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
        }
    }
}
