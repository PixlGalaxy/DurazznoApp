using System.ComponentModel;
using System.Diagnostics;
using Durazzno.Core;

namespace Durazzno.Tools;

/// <param name="Id">Stable key (used for navigation).</param>
/// <param name="Name">Durazzno name shown in the app.</param>
/// <param name="Tagline">One line describing what the tool does.</param>
/// <param name="ClassicName">The bundled third-party program it replaces (launchable until the native page exists).</param>
/// <param name="RelativePath">Path of the classic executable inside the Tools folder.</param>
/// <param name="Icon">Lucide icon name (Assets\Lucide).</param>
/// <param name="Accent">Accent colour as #RRGGBB.</param>
/// <param name="RequiresInternet">Disabled while offline.</param>
/// <param name="External">Only launches the classic program; no Durazzno page is planned.</param>
internal sealed record ToolDefinition(
    string Id,
    string Name,
    string Tagline,
    string ClassicName,
    string RelativePath,
    string Icon,
    string Accent,
    bool RequiresInternet = false,
    bool External = false)
{
    public string FullPath => Path.Combine(AppInfo.ToolsDir, RelativePath);

    public bool IsInstalled => File.Exists(FullPath);
}

internal static class ToolCatalog
{
    /// <summary>Navigation / menu order.</summary>
    public static readonly IReadOnlyList<ToolDefinition> All =
    [
        new("diskinfo", "DiskInfo", "Disk health, temperature and S.M.A.R.T. data", "CrystalDiskInfo",
            "DiskInfo32.exe", "hard-drive", "#3B82F6"),
        new("diskmark", "DiskMark", "Sequential and random read/write speed tests", "CrystalDiskMark",
            "DiskMark32.exe", "gauge", "#10B981"),
        new("diskmap", "DiskMap", "See what is taking up space on your drives", "WinDirStat",
            "windirstat.exe", "chart-pie", "#F59E0B"),
        new("startuphub", "StartupHub", "Everything that starts with Windows, in one list", "Autoruns",
            "Autoruns.exe", "rocket", "#EF4444"),
        new("timerresolution", "TimerResolution", "Windows timer resolution for lower input latency", "Timer Resolution",
            "TimerResolution.exe", "timer", "#8B5CF6"),
        new("driverhub", "DriverHub", "Find outdated and missing drivers", "Driver Easy",
            @"DriverEasy\DriverEasy.exe", "cpu", "#06B6D4", RequiresInternet: true),
    ];

    public static ToolDefinition? Find(string id) => All.FirstOrDefault(t => t.Id == id);
}

internal static class ToolsPackage
{
    public const string VersionFileName = "tools-version.txt";

    /// <summary>Files the tools create at runtime (settings, SMART history). Carried over when Tools is replaced.</summary>
    public static readonly string[] UserDataPaths = ["DiskInfo.ini", "DiskMark32.ini", "Smart"];

    public static string? InstalledVersion
    {
        get
        {
            try
            {
                var file = Path.Combine(AppInfo.ToolsDir, VersionFileName);
                return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
            }
            catch
            {
                return null;
            }
        }
    }
}

internal static class Launcher
{
    private const int ErrorCancelled = 1223; // User said "No" to the UAC prompt.

    /// <summary>Starts a tool's classic program. Returns an error message, or null on success/cancel.</summary>
    public static string? Start(ToolDefinition tool)
    {
        if (!tool.IsInstalled)
            return $"{tool.ClassicName} was not found in {AppInfo.ToolsDir}.";

        try
        {
            Process.Start(new ProcessStartInfo(tool.FullPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(tool.FullPath),
            })?.Dispose();
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not start {tool.ClassicName}", ex);
            return $"Could not start {tool.ClassicName}: {ex.Message}";
        }
    }

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
