using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using Durazzno.Core;

namespace Durazzno.Plugins;

internal enum PowerAction { Shutdown, Restart }

/// <summary>
/// PowerCuts, as the original 2022 plugin worked: Durazzno App counts down itself (also from the tray), warns 5 minutes
/// and 1 minute before with PowerCuts-branded notifications, and with 30 seconds left hands the shutdown to Windows
/// (<c>shutdown /t 30</c>), which shows its own final warning and carries it out even if the app closes. The deadline is
/// saved, so the countdown resumes if Durazzno App restarts.
/// </summary>
internal sealed class PowerCutsService
{
    public const string Name = "PowerCuts";
    public const string Accent = "#00FFBB";

    /// <summary>With this much time left the shutdown is handed to Windows.</summary>
    public const int HandOffSeconds = 30;

    private static readonly (int Seconds, string Text)[] Warnings = [(300, "5 minutes"), (60, "1 minute")];
    private const int MaxSeconds = 315_360_000; // shutdown.exe limit (10 years)
    private static readonly string StatePath = Path.Combine(AppInfo.DataDir, "powercuts.json");

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<int> _warned = [];

    public PowerCutsService()
    {
        _tick.Tick += (_, _) => OnTick();
        try
        {
            if (File.Exists(StatePath) && JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) is { } s)
            {
                if (s.Deadline > DateTime.Now)
                {
                    (Deadline, Started, Action, HandedToWindows) = (s.Deadline, s.Started, s.Action, s.HandedToWindows);
                    // Warnings whose moment already passed while the app was closed are not shown late.
                    foreach (var (seconds, _) in Warnings)
                        if (Remaining.TotalSeconds <= seconds)
                            _warned.Add(seconds);
                    _tick.Start();
                    Log.Info($"PowerCuts: resumed, {Action} at {Deadline:HH:mm:ss}");
                }
                else
                {
                    File.Delete(StatePath); // the moment passed while Durazzno App was not running
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"PowerCuts state unreadable: {ex.Message}");
        }
    }

    public DateTime? Deadline { get; private set; }
    public DateTime? Started { get; private set; }
    public PowerAction Action { get; private set; }

    /// <summary>The last 30 seconds: Windows owns the shutdown now (closing Durazzno no longer stops it).</summary>
    public bool HandedToWindows { get; private set; }

    public bool IsActive => Deadline is { } d && d > DateTime.Now;

    public TimeSpan Remaining => IsActive ? Deadline!.Value - DateTime.Now : TimeSpan.Zero;

    /// <summary>Fraction of the scheduled time that has passed (0–1).</summary>
    public double Progress => IsActive && Started is { } s && Deadline is { } d && d > s
        ? Math.Clamp((DateTime.Now - s).TotalSeconds / (d - s).TotalSeconds, 0, 1)
        : 0;

    public event EventHandler? Changed;

    /// <summary>A warning to show the user (title, message), raised 5 minutes and 1 minute before.</summary>
    public event Action<string, string>? Warning;

    private string Verb => Action == PowerAction.Restart ? "restart" : "shut down";

    /// <summary>Starts the countdown. Returns an error message, or null on success.</summary>
    public string? Start(TimeSpan delay, PowerAction action)
    {
        var seconds = (int)Math.Clamp(Math.Round(delay.TotalSeconds), 0, MaxSeconds);
        if (seconds <= 0)
            return "Choose how long to wait first.";

        if (HandedToWindows)
            RunShutdown("/a");
        Started = DateTime.Now;
        Deadline = Started.Value.AddSeconds(seconds);
        Action = action;
        HandedToWindows = false;
        _warned.Clear();
        foreach (var (s, _) in Warnings)
            if (seconds <= s)
                _warned.Add(s); // a 3-minute timer gets the 1-minute warning only

        if (seconds <= HandOffSeconds && HandOff() is { } error)
        {
            Deadline = Started = null;
            return error;
        }

        Save();
        _tick.Start();
        Log.Info($"PowerCuts: {action} at {Deadline:yyyy-MM-dd HH:mm:ss}");
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    public void Cancel()
    {
        if (HandedToWindows)
            RunShutdown("/a");
        _tick.Stop();
        Deadline = Started = null;
        HandedToWindows = false;
        Save();
        Log.Info("PowerCuts: cancelled");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gives the whole remaining time to Windows now (used when Durazzno App exits mid-countdown): the shutdown still
    /// happens on time, only without PowerCuts' own warnings.
    /// </summary>
    public string? HandOffNow() => IsActive && !HandedToWindows ? HandOff() : null;

    private void OnTick()
    {
        if (!IsActive)
        {
            _tick.Stop();
            Deadline = Started = null;
            HandedToWindows = false;
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var left = Remaining.TotalSeconds;
        foreach (var (seconds, text) in Warnings)
        {
            if (left <= seconds && _warned.Add(seconds))
                Warning?.Invoke("PowerCuts Task Notification", $"Your computer will {Verb} in {text}.");
        }

        if (!HandedToWindows && left <= HandOffSeconds && HandOff() is { } error)
        {
            Warning?.Invoke("PowerCuts", error);
            Cancel();
        }
    }

    private string? HandOff()
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(Remaining.TotalSeconds));
        RunShutdown("/a"); // replace any other pending shutdown (Windows refuses a second one)
        var flag = Action == PowerAction.Restart ? "/r" : "/s";
        var code = RunShutdown($"{flag} /t {seconds} /c \"Durazzno App PowerCuts: this PC will {Verb}.\"");
        if (code != 0)
            return $"Windows refused to {Verb} (code {code}).";

        HandedToWindows = true;
        Save();
        Log.Info($"PowerCuts: handed to Windows, {Verb} in {seconds}s");
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    private void Save()
    {
        try
        {
            if (Deadline is null)
                File.Delete(StatePath);
            else
                File.WriteAllText(StatePath, JsonSerializer.Serialize(new State(Deadline.Value, Started ?? DateTime.Now, Action, HandedToWindows)));
        }
        catch (Exception ex)
        {
            Log.Warn($"PowerCuts state not saved: {ex.Message}");
        }
    }

    private static int RunShutdown(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("shutdown.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            })!;
            process.WaitForExit(10_000);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error($"shutdown {arguments} failed", ex);
            return -1;
        }
    }

    private sealed record State(DateTime Deadline, DateTime Started, PowerAction Action, bool HandedToWindows = false);
}
