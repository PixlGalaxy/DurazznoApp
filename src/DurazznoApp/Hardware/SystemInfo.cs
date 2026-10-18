using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Durazzno.Hardware;

internal sealed record DriveSnapshot(string Letter, string Label, string Format, string Kind, long Total, long Free)
{
    public long Used => Total - Free;

    public double UsedPercent => Total > 0 ? Used * 100.0 / Total : 0;
}

/// <summary>Static hardware description, read once from the registry (no WMI, so it is fast and dependency-free).</summary>
internal static class SystemInfo
{
    public static string MachineName { get; } = Environment.MachineName;

    public static string OsName { get; } = ReadOsName();

    public static string Processor { get; } = Clean(ReadString(
        @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "Unknown processor");

    public static int LogicalProcessors { get; } = Environment.ProcessorCount;

    public static string Graphics { get; } = ReadGraphics();

    public static string Motherboard { get; } = ReadMotherboard();

    public static ulong TotalMemory { get; } = Memory.Read().Total;

    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static IReadOnlyList<DriveSnapshot> Drives()
    {
        var list = new List<DriveSnapshot>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;

                list.Add(new DriveSnapshot(
                    d.Name.TrimEnd('\\'),
                    string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "Removable" : "Local Disk") : d.VolumeLabel,
                    d.DriveFormat,
                    d.DriveType == DriveType.Removable ? "Removable" : "Fixed",
                    d.TotalSize,
                    d.TotalFreeSpace));
            }
            catch
            {
                // Drive went away or is locked; skip it.
            }
        }

        return list;
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }

        return i == 0 ? $"{bytes:0} {units[i]}" : $"{bytes:0.#} {units[i]}";
    }

    public static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" : $"{t.Minutes}m";

    private static string ReadOsName()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = ReadString(key, "ProductName") ?? "Windows";
        var build = int.TryParse(ReadString(key, "CurrentBuildNumber"), out var b) ? b : 0;
        if (build >= 22000)
            product = product.Replace("Windows 10", "Windows 11"); // ProductName still says 10 on Windows 11.
        var display = ReadString(key, "DisplayVersion");
        return display is null ? product : $"{product} {display}";
    }

    private static string ReadGraphics()
    {
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        var names = new List<string>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(classKey);
            foreach (var sub in root?.GetSubKeyNames() ?? [])
            {
                if (!sub.All(char.IsDigit))
                    continue;
                using var adapter = root!.OpenSubKey(sub);
                if (adapter?.GetValue("DriverDesc") is string desc && !names.Contains(desc) && !desc.Contains("Basic", StringComparison.OrdinalIgnoreCase))
                    names.Add(desc);
            }
        }
        catch
        {
            // Access denied on some keys; fall through.
        }

        return names.Count == 0 ? "Unknown graphics" : string.Join(" + ", names);
    }

    private static string ReadMotherboard()
    {
        const string key = @"HARDWARE\DESCRIPTION\System\BIOS";
        var maker = ReadString(key, "BaseBoardManufacturer");
        var product = ReadString(key, "BaseBoardProduct");
        var text = $"{maker} {product}".Trim();
        return string.IsNullOrEmpty(text) ? "Unknown" : text;
    }

    private static string? ReadString(string key, string value)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return (k?.GetValue(value) as string)?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string Clean(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Physical memory usage via GlobalMemoryStatusEx.</summary>
internal static class Memory
{
    public static (ulong Total, ulong Available) Read()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (status.TotalPhys, status.AvailPhys) : (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

/// <summary>Total CPU usage from GetSystemTimes deltas. Call <see cref="Sample"/> periodically.</summary>
internal sealed class CpuSampler
{
    private ulong _idle, _kernel, _user;

    public CpuSampler() => Sample();

    /// <summary>Usage (0–100) since the previous call.</summary>
    public double Sample()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            return 0;

        ulong idle = ToUlong(idleFt), kernel = ToUlong(kernelFt), user = ToUlong(userFt);
        ulong dIdle = idle - _idle, dKernel = kernel - _kernel, dUser = user - _user;
        (_idle, _kernel, _user) = (idle, kernel, user);

        var total = dKernel + dUser; // Kernel time includes idle time.
        return total == 0 ? 0 : Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
    }

    private static ulong ToUlong(System.Runtime.InteropServices.ComTypes.FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME idle,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
        out System.Runtime.InteropServices.ComTypes.FILETIME user);
}
