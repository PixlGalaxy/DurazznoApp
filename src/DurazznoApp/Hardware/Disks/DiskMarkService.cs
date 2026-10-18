using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Durazzno.Core;

namespace Durazzno.Hardware.Disks;

/// <param name="Name">Label like "SEQ1M".</param>
/// <param name="Detail">Queue/thread label like "Q8T1".</param>
internal sealed record BenchTest(string Name, string Detail, int BlockKiB, int Queue, int Threads, bool Random, string Description)
{
    public string Key => $"{Name} {Detail}";

    /// <summary>Speed that fills the bar: roughly a fast PCIe 4.0 NVMe drive for this test.</summary>
    public double ReferenceMBs => (Random, Queue) switch
    {
        (false, >= 8) => 7400,
        (false, _) => 4500,
        (true, >= 32) => 1000,
        _ => 100,
    };
}

internal enum BenchProfile { Default, Nvme }

internal enum BenchMode { ReadWrite, ReadOnly, WriteOnly }

internal sealed record BenchResult(double MBs, double Iops, double LatencyUs);

internal sealed class BenchRun
{
    public string Drive { get; init; } = "";
    public BenchProfile Profile { get; init; }
    public int SizeMiB { get; init; }
    public int Passes { get; init; }
    public DateTime Finished { get; set; }
    public Dictionary<string, BenchResult> Read { get; init; } = [];
    public Dictionary<string, BenchResult> Write { get; init; } = [];
}

/// <summary>
/// DiskMark engine. Runs the same DiskSpd workloads as CrystalDiskMark 9 (DiskBench.cpp): unbuffered
/// (<c>-S</c>), no warm-up inside DiskSpd (<c>-W0</c>), latency stats (<c>-L</c>), 5 s per pass, best of N passes
/// after one warm-up pass, on a test file pre-filled with random data. Runs in the background so it keeps going when
/// the window is closed.
/// </summary>
internal sealed class DiskMarkService
{
    public static readonly IReadOnlyDictionary<BenchProfile, BenchTest[]> Profiles = new Dictionary<BenchProfile, BenchTest[]>
    {
        [BenchProfile.Default] =
        [
            new("SEQ1M", "Q8T1", 1024, 8, 1, false, "Sequential · 1 MiB blocks · queue 8"),
            new("SEQ1M", "Q1T1", 1024, 1, 1, false, "Sequential · 1 MiB blocks · queue 1"),
            new("RND4K", "Q32T1", 4, 32, 1, true, "Random · 4 KiB blocks · queue 32"),
            new("RND4K", "Q1T1", 4, 1, 1, true, "Random · 4 KiB blocks · queue 1"),
        ],
        [BenchProfile.Nvme] =
        [
            new("SEQ1M", "Q8T1", 1024, 8, 1, false, "Sequential · 1 MiB blocks · queue 8"),
            new("SEQ128K", "Q32T1", 128, 32, 1, false, "Sequential · 128 KiB blocks · queue 32"),
            new("RND4K", "Q32T16", 4, 32, 16, true, "Random · 4 KiB blocks · queue 32 · 16 threads"),
            new("RND4K", "Q1T1", 4, 1, 1, true, "Random · 4 KiB blocks · queue 1"),
        ],
    };

    private const int MeasureSeconds = 5;
    private static readonly string HistoryPath = Path.Combine(AppInfo.DataDir, "diskmark.json");
    private static readonly string DiskSpdPath = ExtractDiskSpd();

    private readonly Dictionary<string, BenchRun> _history = LoadHistory();
    private CancellationTokenSource? _cts;
    private Process? _current;

    public bool IsRunning => _cts is not null;

    public bool IsAvailable => File.Exists(DiskSpdPath);

    /// <summary>
    /// DiskSpd (Microsoft, MIT licence, Assets\Bin) ships inside the exe and is written to %LOCALAPPDATA%\DurazznoApp\bin
    /// the first time, or again when this build carries a different copy.
    /// </summary>
    private static string ExtractDiskSpd()
    {
        var path = Path.Combine(AppInfo.DataDir, "bin", "DiskSpd64.exe");
        try
        {
            using var resource = typeof(DiskMarkService).Assembly.GetManifestResourceStream("Bin.DiskSpd64.exe");
            if (resource is null)
                return path;
            if (!File.Exists(path) || new FileInfo(path).Length != resource.Length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var file = File.Create(path);
                resource.CopyTo(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"DiskSpd could not be prepared: {ex.Message}");
        }
        return path;
    }

    /// <summary>The run in progress, or the last finished one.</summary>
    public BenchRun? Current { get; private set; }

    public string Status { get; private set; } = "";

    /// <summary>Overall progress 0–1.</summary>
    public double Progress { get; private set; }

    /// <summary>Raised on the UI thread whenever status, progress or a result changes.</summary>
    public event EventHandler? Changed;

    public BenchRun? LastRun(string drive) => _history.GetValueOrDefault(drive);

    public void Start(string drive, BenchProfile profile, int sizeMiB, int passes, BenchMode mode)
    {
        if (IsRunning)
            return;

        var ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _cts = new CancellationTokenSource();
        Current = new BenchRun { Drive = drive, Profile = profile, SizeMiB = sizeMiB, Passes = passes };
        Progress = 0;
        Report(ui, "Preparing…");

        var token = _cts.Token;
        _ = Task.Run(() => Run(Current, mode, ui, token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _current?.Kill(); } catch { }
    }

    private void Run(BenchRun run, BenchMode mode, SynchronizationContext ui, CancellationToken token)
    {
        var folder = Path.Combine($"{run.Drive}\\", $"DurazznoDiskMark{Environment.TickCount64:X}");
        var file = Path.Combine(folder, "DiskMark.tmp");
        var keepAwake = SetThreadExecutionState(EsContinuous | EsSystemRequired);
        try
        {
            var free = new DriveInfo(run.Drive).AvailableFreeSpace;
            if (free < (long)run.SizeMiB * 1024 * 1024 + 256L * 1024 * 1024)
                throw new IOException($"Not enough free space on {run.Drive} for a {run.SizeMiB} MiB test file.");

            Directory.CreateDirectory(folder);
            Report(ui, $"Creating {FormatSize(run.SizeMiB)} test file…");
            CreateTestFile(file, run.SizeMiB, token);

            var tests = Profiles[run.Profile];
            var phases = (mode == BenchMode.ReadWrite ? 2 : 1) * tests.Length;
            var done = 0;

            foreach (var write in mode switch { BenchMode.ReadOnly => new[] { false }, BenchMode.WriteOnly => new[] { true }, _ => new[] { false, true } })
            {
                foreach (var test in tests)
                {
                    BenchResult? best = null;
                    for (var pass = 0; pass <= run.Passes; pass++)
                    {
                        token.ThrowIfCancellationRequested();
                        var label = $"{(write ? "Write" : "Read")} {test.Key}";
                        Progress = (done + (double)pass / (run.Passes + 1)) / phases;
                        Report(ui, pass == 0 ? $"{label} · warm-up" : $"{label} · pass {pass}/{run.Passes}");

                        var result = RunDiskSpd(test, write, file, token);
                        if (pass == 0 || result is null)
                            continue;

                        // Best throughput of all passes, lowest latency (same rule as CrystalDiskMark).
                        best = best is null ? result : new BenchResult(
                            Math.Max(best.MBs, result.MBs), Math.Max(best.Iops, result.Iops), Math.Min(best.LatencyUs, result.LatencyUs));
                        (write ? run.Write : run.Read)[test.Key] = best;
                        Report(ui, null);
                    }

                    done++;
                }
            }

            run.Finished = DateTime.Now;
            _history[run.Drive] = run;
            SaveHistory();
            Progress = 1;
            Report(ui, $"Finished at {run.Finished:HH:mm}");
        }
        catch (OperationCanceledException)
        {
            Report(ui, "Stopped");
        }
        catch (Exception ex)
        {
            Log.Error("DiskMark failed", ex);
            Report(ui, $"Error: {ex.Message}");
        }
        finally
        {
            if (keepAwake != 0)
                SetThreadExecutionState(EsContinuous);
            TryDelete(file, folder);
            ui.Post(_ =>
            {
                _cts?.Dispose();
                _cts = null;
                _current = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }, null);
        }
    }

    private BenchResult? RunDiskSpd(BenchTest test, bool write, string file, CancellationToken token)
    {
        var args = $"-b{test.BlockKiB}K -o{test.Queue} -t{test.Threads} -W0 -S -w{(write ? 100 : 0)}"
            + (test.Random ? " -r" : "")
            + (write ? $" -Z{test.BlockKiB}K" : "")
            + " -ag"
            + (!test.Random && test.Threads > 1 ? " -si" : "")
            + $" -d{MeasureSeconds} -L \"{file}\"";

        using var process = Process.Start(new ProcessStartInfo(DiskSpdPath, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        }) ?? throw new IOException("Could not start DiskSpd.");

        _current = process;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        _current = null;
        token.ThrowIfCancellationRequested();
        return ParseTotal(output);
    }

    /// <summary>First "total:" row after "Total IO": bytes | I/Os | MiB/s | I/O per s | AvgLat (ms) | LatStdDev.</summary>
    internal static BenchResult? ParseTotal(string output)
    {
        var start = output.IndexOf("Total IO", StringComparison.Ordinal);
        if (start < 0)
            return null;

        foreach (var line in output[start..].Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("total:", StringComparison.Ordinal))
                continue;

            var cells = trimmed["total:".Length..].Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 5)
                return null;

            static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            var mibPerSecond = D(cells[2]);
            return new BenchResult(mibPerSecond * 1.048576, D(cells[3]), D(cells[4]) * 1000);
        }

        return null;
    }

    /// <summary>Allocates the file and fills it with random data using unbuffered, sector-aligned 1 MiB writes.</summary>
    private static unsafe void CreateTestFile(string path, int sizeMiB, CancellationToken token)
    {
        const int block = 1024 * 1024;
        const FileOptions noBuffering = (FileOptions)0x20000000; // FILE_FLAG_NO_BUFFERING
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            noBuffering | FileOptions.WriteThrough | FileOptions.SequentialScan, (long)sizeMiB * block);

        var buffer = (byte*)NativeMemory.AlignedAlloc(block, 4096);
        try
        {
            var span = new Span<byte>(buffer, block);
            System.Random.Shared.NextBytes(span);
            for (long i = 0; i < sizeMiB; i++)
            {
                token.ThrowIfCancellationRequested();
                RandomAccess.Write(handle, span, i * block);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    public static string FormatSize(int mib) => mib >= 1024 ? $"{mib / 1024} GiB" : $"{mib} MiB";

    private void Report(SynchronizationContext ui, string? status)
    {
        if (status is not null)
            Status = status;
        ui.Post(_ => Changed?.Invoke(this, EventArgs.Empty), null);
    }

    private static void TryDelete(string file, string folder)
    {
        try { File.Delete(file); } catch { }
        try { Directory.Delete(folder); } catch { }
    }

    private static Dictionary<string, BenchRun> LoadHistory()
    {
        try
        {
            return File.Exists(HistoryPath)
                ? JsonSerializer.Deserialize<Dictionary<string, BenchRun>>(File.ReadAllText(HistoryPath)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveHistory()
    {
        try
        {
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(_history));
        }
        catch (Exception ex)
        {
            Log.Warn($"DiskMark history not saved: {ex.Message}");
        }
    }

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
