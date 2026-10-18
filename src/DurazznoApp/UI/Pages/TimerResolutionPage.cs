using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Durazzno.Hardware;
using Durazzno.Tools;
using Durazzno.UI.Controls;

namespace Durazzno.UI.Pages;

/// <summary>TimerResolution: see and set the Windows timer resolution, and measure what Sleep(1) really takes.</summary>
internal sealed class TimerResolutionPage : UserControl, IDisposable
{
    private static readonly (string Label, double Ms)[] Presets = [("0.5 ms", 0.5), ("1 ms", 1.0), ("2 ms", 2.0), ("5 ms", 5.0)];

    private readonly AppController _app;
    private readonly TimerResolutionService _timer;
    private readonly ToolDefinition _tool;
    private readonly Brush _accent;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly TextBlock _current;
    private readonly TextBlock _currentHint = Ui.Text("", "CardHint");
    private readonly Border _badge = new() { Padding = new Thickness(10, 3, 10, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _badgeText = new() { FontWeight = FontWeights.Bold, FontSize = 12 };
    private readonly ContentControl _scale = new() { Focusable = false, Margin = new Thickness(0, 18, 0, 0) };
    private readonly WrapPanel _presets = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _always = new() { Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(18, 9, 18, 9) };
    private readonly TextBlock _alwaysHint = Ui.Text("", "CardHint");

    private readonly TextBlock _sleepAvg;
    private readonly TextBlock _sleepHint = Ui.Text("Press Run test to measure.", "CardHint");
    private readonly ContentControl _sleepBar = new() { Focusable = false, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button _runTest;

    public TimerResolutionPage(AppController app, ToolDefinition tool)
    {
        _app = app;
        _timer = app.TimerResolution;
        _tool = tool;
        _accent = Imaging.Brush(tool.Accent);

        var title = Ui.Stack(Ui.Text(tool.Name, "PageTitle"), Ui.Text(tool.Tagline, "PageSubtitle"));
        title.Margin = new Thickness(20, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        var header = Ui.Row(Ui.IconChip(tool.Icon, _accent, Imaging.Brush(tool.Accent, 0x26), 64, 30), title);

        // Current resolution ────────────────────────────────────────
        _current = new TextBlock { FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 72, Foreground = _accent, Margin = new Thickness(0, -2, 0, -6) };
        _badge.Child = _badgeText;
        var current = Ui.Card(Ui.Stack(Ui.Text("CURRENT TIMER RESOLUTION", "CardLabel"), _current, _currentHint, _badge, _scale), new Thickness(28, 24, 28, 26));

        // Set ───────────────────────────────────────────────────────
        foreach (var (label, ms) in Presets)
        {
            var b = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(16, 8, 16, 8) };
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            b.Click += (_, _) => _timer.Apply(ms);
            _presets.Children.Add(b);
        }
        var release = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = "WINDOWS DEFAULT", Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(16, 8, 16, 8) };
        System.Windows.Automation.AutomationProperties.SetName(release, "WINDOWS DEFAULT");
        release.Click += (_, _) => _timer.Release();
        _presets.Children.Add(release);

        System.Windows.Automation.AutomationProperties.SetName(_always, "ALWAYS ACTIVE");
        _always.Click += (_, _) => ToggleAlways();
        _alwaysHint.Margin = new Thickness(0, 8, 0, 0);
        _alwaysHint.TextWrapping = TextWrapping.Wrap;
        _alwaysHint.TextTrimming = TextTrimming.None;

        var setText = Ui.Text("0.5 ms gives the smoothest frame pacing and lowest input lag in games. Durazzno App keeps the setting while it runs in the tray.", "Body", Ui.B("TextMuted"));
        setText.Margin = new Thickness(0, 12, 0, 14);
        var set = Ui.Card(Ui.Stack(Ui.PanelTitle("SET RESOLUTION"), setText, _presets, _always, _alwaysHint));

        // Sleep test ────────────────────────────────────────────────
        _sleepAvg = new TextBlock { FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 44, Foreground = Ui.B("Text"), Text = "—" };
        _runTest = new Button { Style = Ui.Res<Style>("PrimaryButton"), Content = IconLabel("timer", "RUN TEST", Ui.B("AccentText")), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(_runTest, "RUN TEST");
        _runTest.Click += async (_, _) => await RunTestAsync();
        var sleepText = Ui.Text("Asks Windows to wait 1 ms, 120 times, and measures how long it really took. With the default 15.6 ms timer this is often ~2–16 ms; at 0.5 ms it should be close to 1 ms.", "Body", Ui.B("TextMuted"));
        sleepText.Margin = new Thickness(0, 12, 0, 12);
        var test = Ui.Card(Ui.Stack(Ui.PanelTitle("SLEEP(1) TEST"), sleepText, Ui.Text("AVERAGE SLEEP(1)", "CardLabel"), _sleepAvg, _sleepHint, _sleepBar, _runTest));

        // Notes ─────────────────────────────────────────────────────
        var notes = Ui.Stack(Ui.PanelTitle("GOOD TO KNOW"));
        foreach (var line in new[]
        {
            "Since Windows 10 2004, a resolution request mainly speeds up the program that asks for it. Games that request their own timer still benefit; others keep the default.",
            "Higher resolution wakes the CPU more often, so laptops on battery use slightly more power.",
            "Windows goes back to the default as soon as Durazzno App closes or you choose Windows default.",
        })
        {
            var t = Ui.Text("•  " + line, "Body", Ui.B("TextMuted"));
            t.Margin = new Thickness(0, 12, 0, 0);
            notes.Children.Add(t);
        }

        if (TimerResolutionService.SupportsGlobalRequests)
        {
            var global = new CheckBox { Content = "Make timer requests global (Windows 11, needs restart)", Style = Ui.Res<Style>("ToggleSwitch"), IsChecked = TimerResolutionService.GlobalRequestsEnabled, Margin = new Thickness(0, 18, 0, 0) };
            global.Click += async (_, _) =>
            {
                var want = global.IsChecked == true;
                var error = await TimerResolutionService.SetGlobalRequestsAsync(want);
                if (error is not null)
                {
                    global.IsChecked = !want;
                    if (error != "Cancelled")
                        _app.NotifyTool(_tool, error, System.Windows.Forms.ToolTipIcon.Error);
                }
                else
                {
                    _app.NotifyTool(_tool, "Saved. Restart Windows for it to take effect.");
                }
            };
            notes.Children.Add(global);
        }

        var top = Ui.Columns(2, 20);
        top.ColumnDefinitions[0].Width = new GridLength(1.2, GridUnitType.Star);
        top.Margin = new Thickness(0, 28, 0, 0);
        top.Children.Add(current.At(0));
        top.Children.Add(set.At(2));

        var bottom = Ui.Columns(2, 20);
        bottom.ColumnDefinitions[0].Width = new GridLength(1.2, GridUnitType.Star);
        bottom.Margin = new Thickness(0, 20, 0, 0);
        bottom.Children.Add(test.At(0));
        bottom.Children.Add(Ui.Card(notes).At(2));

        Content = Ui.Stack(header, top, bottom);

        _timer.Changed += OnChanged;
        _refresh.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Refresh(); _refresh.Start(); }
            else _refresh.Stop();
        };
        Refresh();
    }

    public void Dispose()
    {
        _refresh.Stop();
        _timer.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var info = TimerResolutionService.Query();
        // Exact values from NtQueryTimerResolution (100 ns units), always with three decimals.
        _current.Text = $"{info.CurrentMs:0.000} ms";
        _currentHint.Text = $"Windows default {info.DefaultMs:0.000} ms  ·  best this PC supports {info.BestMs:0.000} ms";

        var (text, tone) = _timer.HeldMs is { } held ? ($"HELD BY DURAZZNO AT {held:0.000} MS", _tool.Accent)
            : info.CurrentMs < info.DefaultMs - 0.01 ? ("RAISED BY ANOTHER PROGRAM", "#38BDF8")
            : ("WINDOWS DEFAULT", "#94A3B8");
        _badgeText.Text = text;
        _badgeText.Foreground = Imaging.Brush(tone);
        _badge.Background = Imaging.Brush(tone, 0x22);

        // Bar: how far from the default toward the best resolution we are.
        var range = info.DefaultMs - info.BestMs;
        var percent = range > 0 ? (info.DefaultMs - info.CurrentMs) / range * 100 : 0;
        var labels = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        labels.Children.Add(Ui.Text($"{info.DefaultMs:0.000} ms", "CardHint"));
        labels.Children.Add(new TextBlock { Text = $"{info.BestMs:0.000} ms", Style = Ui.Res<Style>("CardHint"), HorizontalAlignment = HorizontalAlignment.Right });
        _scale.Content = Ui.Stack(Ui.UsageBar(percent, _accent, 8), labels);

        foreach (var b in _presets.Children.OfType<Button>())
        {
            var match = Presets.FirstOrDefault(p => p.Label == (string)b.Content);
            var active = match.Label is not null ? _timer.HeldMs is { } h && Math.Abs(h - match.Ms) < 0.001 : _timer.HeldMs is null;
            b.Style = Ui.Res<Style>(active ? "PrimaryButton" : "SecondaryButton");
        }

        var on = _app.Settings.Current.TimerResolutionOnStartup;
        _always.Style = Ui.Res<Style>(on ? "PrimaryButton" : "SecondaryButton");
        _always.Content = IconLabel("power", on ? "ALWAYS ACTIVE: ON" : "ALWAYS ACTIVE: OFF", on ? Ui.B("AccentText") : Ui.B("Text"));
        _alwaysHint.Text = on
            ? $"Applied automatically every time Durazzno App starts ({_app.Settings.Current.TimerResolutionMs ?? info.BestMs:0.000} ms)."
            : "Turn on to apply the chosen resolution automatically whenever Durazzno App starts.";
    }

    /// <summary>Keep the resolution active on every start. Turning it on with nothing chosen applies the best value.</summary>
    private void ToggleAlways()
    {
        var on = !_app.Settings.Current.TimerResolutionOnStartup;
        _timer.SetApplyOnStartup(on);
        if (on && _timer.HeldMs is null)
            _timer.Apply(TimerResolutionService.Query().BestMs);
        Refresh();
    }

    private async Task RunTestAsync()
    {
        _runTest.IsEnabled = false;
        _sleepHint.Text = "Measuring…";
        try
        {
            var r = await TimerResolutionService.MeasureSleepAsync();
            _sleepAvg.Text = $"{r.AverageMs:0.00} ms";
            _sleepAvg.Foreground = r.AverageMs < 1.6 ? Ui.B("Success") : r.AverageMs < 4 ? Ui.B("Warning") : Ui.B("Danger");
            _sleepHint.Text = $"best {r.MinMs:0.00} ms  ·  worst {r.MaxMs:0.00} ms  ·  ideal 1.00 ms";
            _sleepBar.Content = Ui.UsageBar(Math.Min(100, 100 / r.AverageMs), _sleepAvg.Foreground, 8);
        }
        finally
        {
            _runTest.IsEnabled = true;
        }
    }

    private static StackPanel IconLabel(string icon, string text, Brush foreground) => Ui.Row(
        new LucideIcon { Icon = icon, Width = 15, Height = 15, Foreground = foreground },
        new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
}
