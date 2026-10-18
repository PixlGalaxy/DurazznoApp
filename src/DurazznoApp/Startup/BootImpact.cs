using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Durazzno.Core;

namespace Durazzno.Startup;

/// <summary>A process Windows recorded while the user was logging on, with what it cost.</summary>
internal sealed record LogonProcess(string Image, int Pid, int ParentPid, double CpuMs, long DiskBytes);

/// <summary>One boot from the Diagnostics-Performance log: total, until the desktop (main path) and after it.</summary>
internal sealed record BootRecord(DateTime Time, int TotalMs, int MainPathMs, int PostBootMs);

internal sealed record BootImpactData(DateTime MeasuredAt, DateTime? LogonTime, List<LogonProcess> Processes, List<BootRecord> Boots);

internal enum StartupImpact { NotMeasured, None, Low, Medium, High }

/// <summary>
/// Startup impact, the same data Task Manager uses. At every logon Windows records each process started for the user
/// with its CPU time and disk I/O (C:\Windows\System32\wdi\LogFiles\StartupInfo\&lt;SID&gt;_StartupInfo*.xml), and every
/// boot's duration (event 100 in Microsoft-Windows-Diagnostics-Performance/Operational). Both need admin rights, so an
/// elevated copy of the app reads them (--boot-impact) and saves the result in %LOCALAPPDATA%\DurazznoApp\boot-impact.json;
/// StartupHub shows the saved result until it is measured again.
/// Ratings follow Task Manager: High = over 1 s of CPU or 3 MB of disk, Medium = over 300 ms or 300 KB, Low = below.
/// An entry's cost includes the processes it started.
/// </summary>
internal static class BootImpact
{
    public const string Flag = "--boot-impact"; // --boot-impact <out.json>

    public static string CacheFile => Path.Combine(AppInfo.DataDir, "boot-impact.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static BootImpactData? LoadCached()
    {
        try
        {
            return File.Exists(CacheFile) ? JsonSerializer.Deserialize<BootImpactData>(File.ReadAllText(CacheFile)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Measures in an elevated copy of the app (one UAC prompt). Returns an error, "Cancelled" or null.</summary>
    public static async Task<string?> MeasureAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(AppInfo.ExePath, $"{Flag} \"{CacheFile}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
                return "Could not start the measurement.";
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? null : "Windows has no startup data for this user yet (it is recorded at logon).";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Cancelled";
        }
    }

    /// <summary>Elevated entry point.</summary>
    public static int RunElevated(string output)
    {
        try
        {
            var (logon, processes) = ReadStartupInfo();
            var data = new BootImpactData(DateTime.Now, logon, processes, ReadBoots());
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(data, Json));
            Log.Info($"Boot impact: {processes.Count} logon processes, {data.Boots.Count} boots");
            return processes.Count > 0 || data.Boots.Count > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Warn($"Boot impact failed: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// The entry's cost at the last logon: the processes running its program plus everything they started (unless that
    /// child is another entry's program).
    /// </summary>
    public static (StartupImpact Impact, double CpuMs, long DiskBytes) For(StartupEntry entry, BootImpactData? data, IReadOnlySet<string> entryImages)
    {
        if (data is null || data.Processes.Count == 0)
            return (StartupImpact.NotMeasured, 0, 0);
        if (entry.ImagePath is not { Length: > 0 } image)
            return (StartupImpact.NotMeasured, 0, 0);

        var roots = data.Processes.Where(p => p.Image.Equals(image, StringComparison.OrdinalIgnoreCase)).ToList();
        if (roots.Count == 0)
            return (StartupImpact.None, 0, 0);

        var children = data.Processes.ToLookup(p => p.ParentPid);
        double cpu = 0;
        long disk = 0;
        var seen = new HashSet<int>();
        var stack = new Stack<LogonProcess>(roots);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (!seen.Add(p.Pid))
                continue;
            cpu += p.CpuMs;
            disk += p.DiskBytes;
            foreach (var child in children[p.Pid])
                if (child.Pid != p.Pid && !entryImages.Contains(child.Image))
                    stack.Push(child);
        }
        return (Rate(cpu, disk), cpu, disk);
    }

    public static StartupImpact Rate(double cpuMs, long diskBytes) =>
        cpuMs > 1000 || diskBytes > 3 * 1024 * 1024 ? StartupImpact.High
        : cpuMs > 300 || diskBytes > 300 * 1024 ? StartupImpact.Medium
        : StartupImpact.Low;

    // ── Readers (elevated) ────────────────────────────────────────────────────────────────

    private static (DateTime? Logon, List<LogonProcess> Processes) ReadStartupInfo()
    {
        var folder = Path.Combine(Environment.SystemDirectory, "wdi", "LogFiles", "StartupInfo");
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var file = Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles($"{sid}_StartupInfo*.xml").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
            : null;
        if (file is null)
            return (null, []);

        var devices = DeviceToDrive();
        var list = new List<LogonProcess>();
        var doc = XDocument.Load(file.FullName);
        foreach (var p in doc.Descendants("Process"))
        {
            var name = (string?)p.Attribute("Name") ?? "";
            if (!int.TryParse((string?)p.Attribute("PID"), out var pid))
                continue;
            int.TryParse((string?)p.Element("ParentPID"), out var parent);
            list.Add(new LogonProcess(ToDosPath(name, devices), pid, parent, CpuMs(p.Element("CpuUsage")), Bytes(p.Element("DiskUsage"))));
        }
        return (file.LastWriteTime, list);
    }

    private static double CpuMs(XElement? e)
    {
        if (e is null || !double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            return 0;
        return ((string?)e.Attribute("Unit"))?.ToLowerInvariant() switch
        {
            "ms" => value,
            "s" => value * 1000,
            "100ns" => value / 10_000,
            _ => value / 1000, // "us" (what Windows writes)
        };
    }

    private static long Bytes(XElement? e) =>
        e is not null && long.TryParse(e.Value, out var value) ? value : 0;

    private static List<BootRecord> ReadBoots()
    {
        var boots = new List<BootRecord>();
        try
        {
            var query = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[EventID=100]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null && boots.Count < 10; record = reader.ReadEvent())
            {
                using (record)
                {
                    var xml = XDocument.Parse(record.ToXml());
                    XNamespace ns = xml.Root!.Name.Namespace;
                    int Field(string name) => int.TryParse(xml.Descendants(ns + "Data").FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value, out var v) ? v : 0;
                    var total = Field("BootTime");
                    if (total > 0)
                        boots.Add(new BootRecord(record.TimeCreated ?? DateTime.MinValue, total, Field("MainPathBootTime"), Field("BootPostBootTime")));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Boot times: {ex.Message}");
        }
        return boots;
    }

    // "\Device\HarddiskVolume3\Program Files\X\x.exe" -> "C:\Program Files\X\x.exe"
    private static string ToDosPath(string path, Dictionary<string, string> devices)
    {
        foreach (var (device, drive) in devices)
            if (path.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
                return drive + path[device.Length..];
        return path;
    }

    private static Dictionary<string, string> DeviceToDrive()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            var letter = drive.Name.TrimEnd('\\');
            var buffer = new StringBuilder(512);
            if (QueryDosDevice(letter, buffer, buffer.Capacity) > 0)
                map[buffer.ToString()] = letter;
        }
        return map;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int max);
}
