using System.Text.Json;
using Durazzno.Core;

namespace Durazzno.Hardware.Disks;

/// <summary>One S.M.A.R.T. reading kept in the history. Counters are null when the drive did not report them.</summary>
internal sealed record SmartPoint(
    DateTime Time,
    int? TemperatureC,
    int? LifePercent,
    ulong? PowerOnHours,
    double? WrittenBytes,
    long? Reallocated,
    long? Pending,
    long? Uncorrectable,
    long? MediaErrors,
    DiskHealth Health);

/// <summary>Something that got worse between two readings.</summary>
internal sealed record SmartChange(DiskReport Disk, DateTime Time, string Text, bool Serious);

/// <summary>
/// S.M.A.R.T. history per disk, in %LOCALAPPDATA%\DurazznoApp\smart-history.json. A point is added every time
/// Durazzno reads the disks (DiskInfo open, startup check), at most one per hour unless a damage counter or the health
/// changed. To stay small it is compacted on every save: hourly points for the last 7 days, one point per day after
/// that (the one with the highest temperature, or one where a counter changed), nothing older than a year, and never
/// more than 600 points per disk (~80 KB). Comparing a new reading with the last one finds disks that are
/// getting worse: new reallocated / pending / uncorrectable sectors, new NVMe media errors, or a lower health.
/// </summary>
internal static class SmartHistory
{
    private static readonly string FilePath = Path.Combine(AppInfo.DataDir, "smart-history.json");
    private static readonly object Gate = new();
    private static Dictionary<string, List<SmartPoint>>? _data;

    public static IReadOnlyList<SmartPoint> For(DiskReport disk)
    {
        lock (Gate)
            return Load().TryGetValue(Key(disk), out var points) ? points.ToList() : [];
    }

    /// <summary>Stores the new readings and returns what got worse since each disk's previous reading.</summary>
    public static List<SmartChange> Record(IEnumerable<DiskReport> disks)
    {
        var changes = new List<SmartChange>();
        lock (Gate)
        {
            var data = Load();
            var now = DateTime.Now;
            foreach (var disk in disks.Where(d => !d.IsVirtual && d.HasSmart))
            {
                var point = Read(disk, now);
                var key = Key(disk);
                if (!data.TryGetValue(key, out var points))
                    data[key] = points = [];
                var last = points.LastOrDefault();
                var worse = last is null ? [] : Compare(disk, last, point);
                changes.AddRange(worse);

                if (last is null || worse.Count > 0 || point.Health != last.Health || now - last.Time >= TimeSpan.FromHours(1))
                    points.Add(point);
                Compact(points, now);
            }
            Save(data);
        }
        return changes;
    }

    /// <summary>Problems that appeared between consecutive points, newest first (for the History card).</summary>
    public static List<(DateTime Time, string Text)> Events(IReadOnlyList<SmartPoint> points)
    {
        var events = new List<(DateTime, string)>();
        for (var i = 1; i < points.Count; i++)
        {
            var (a, b) = (points[i - 1], points[i]);
            void Counter(long? old, long? current, string what)
            {
                if (old is { } x && current is { } y && y > x)
                    events.Add((b.Time, $"{y - x} new {what} (total {y})"));
            }
            Counter(a.Reallocated, b.Reallocated, "reallocated sectors");
            Counter(a.Pending, b.Pending, "pending sectors");
            Counter(a.Uncorrectable, b.Uncorrectable, "uncorrectable sectors");
            Counter(a.MediaErrors, b.MediaErrors, "media errors");
            if (b.Health > a.Health && a.Health != DiskHealth.Unknown)
                events.Add((b.Time, $"Health went from {a.Health} to {b.Health}"));
        }
        events.Reverse();
        return events;
    }

    private const int MaxPointsPerDisk = 600;

    private static void Compact(List<SmartPoint> points, DateTime now)
    {
        points.RemoveAll(p => now - p.Time > TimeSpan.FromDays(365));

        var recent = points.Where(p => now - p.Time <= TimeSpan.FromDays(7));
        var older = points.Where(p => now - p.Time > TimeSpan.FromDays(7))
            .GroupBy(p => p.Time.Date)
            .Select(day =>
            {
                // Keep the day's reading where something changed, otherwise its hottest one.
                var ordered = day.OrderBy(p => p.Time).ToList();
                var changed = ordered.Zip(ordered.Skip(1)).Where(x => Damage(x.Second) != Damage(x.First) || x.Second.Health != x.First.Health)
                    .Select(x => x.Second).LastOrDefault();
                return changed ?? ordered.MaxBy(p => p.TemperatureC ?? int.MinValue)!;
            });
        var kept = older.Concat(recent).OrderBy(p => p.Time).ToList();
        if (kept.Count > MaxPointsPerDisk)
            kept = kept.Skip(kept.Count - MaxPointsPerDisk).ToList();

        points.Clear();
        points.AddRange(kept);
    }

    private static long Damage(SmartPoint p) => (p.Reallocated ?? 0) + (p.Pending ?? 0) + (p.Uncorrectable ?? 0) + (p.MediaErrors ?? 0);

    private static List<SmartChange> Compare(DiskReport disk, SmartPoint before, SmartPoint now)
    {
        var list = new List<SmartChange>();
        void Counter(long? old, long? current, string what)
        {
            if (old is { } a && current is { } b && b > a)
                list.Add(new SmartChange(disk, now.Time, $"{b - a} new {what} (now {b})", true));
        }
        Counter(before.Reallocated, now.Reallocated, "reallocated sectors");
        Counter(before.Pending, now.Pending, "pending sectors");
        Counter(before.Uncorrectable, now.Uncorrectable, "uncorrectable sectors");
        Counter(before.MediaErrors, now.MediaErrors, "media errors");
        if (now.Health > before.Health && before.Health != DiskHealth.Unknown)
            list.Add(new SmartChange(disk, now.Time, $"Health went from {before.Health} to {now.Health}", true));
        if (before.LifePercent is { } lifeBefore && now.LifePercent is { } lifeNow && lifeNow <= 10 && lifeBefore > 10)
            list.Add(new SmartChange(disk, now.Time, $"Less than 10% of rated endurance left ({lifeNow}%)", false));
        return list;
    }

    private static SmartPoint Read(DiskReport d, DateTime now)
    {
        long? Raw(byte id) => d.Attributes.FirstOrDefault(a => a.Id == id) is { } a ? (long)(a.Raw & 0xFFFFFFFF) : null;
        return new SmartPoint(now, d.TemperatureC, d.LifePercent, d.PowerOnHours, d.HostWritesBytes,
            Raw(0x05), Raw(0xC5), Raw(0xC6), d.Nvme is { } n ? (long)Math.Min((double)n.MediaErrors, long.MaxValue) : null, d.Health);
    }

    // Model + serial survive drive letter and index changes; the index is a fallback for drives without a serial.
    private static string Key(DiskReport d) => $"{d.Model.Trim()}|{(string.IsNullOrWhiteSpace(d.Serial) ? $"#{d.Index}" : d.Serial.Trim())}";

    private static Dictionary<string, List<SmartPoint>> Load()
    {
        if (_data is not null)
            return _data;
        try
        {
            if (File.Exists(FilePath))
                _data = JsonSerializer.Deserialize<Dictionary<string, List<SmartPoint>>>(File.ReadAllText(FilePath));
        }
        catch (Exception ex)
        {
            Log.Warn($"SMART history could not be read, starting a new one: {ex.Message}");
        }
        return _data ??= [];
    }

    private static void Save(Dictionary<string, List<SmartPoint>> data)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"SMART history could not be saved: {ex.Message}");
        }
    }
}
