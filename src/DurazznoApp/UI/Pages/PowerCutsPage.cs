using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Durazzno.Plugins;
using Durazzno.UI.Controls;

namespace Durazzno.UI.Pages;

/// <summary>PowerCuts plugin: pick a delay and an action; Durazzno counts down, warns, and hands the last 30 seconds to Windows.</summary>
internal sealed class PowerCutsPage : UserControl, IDisposable
{
    private static readonly (string Label, int Minutes)[] Presets =
        [("15 MIN", 15), ("30 MIN", 30), ("1 H", 60), ("2 H", 120), ("4 H", 240), ("8 H", 480)];

    private readonly AppController _app;
    private readonly PowerCutsService _cuts;
    private readonly Brush _accent = Imaging.Brush(PowerCutsService.Accent);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly TextBlock _state = Ui.Text("", "CardLabel");
    private readonly TextBlock _clock;
    private readonly TextBlock _when = Ui.Text("", "CardHint");
    private readonly ContentControl _progress = new() { Focusable = false, Margin = new Thickness(0, 18, 0, 0) };
    private readonly RadioButton _shutdown;
    private readonly RadioButton _restart;
    private readonly Stepper _hours = new("HOURS", 60);
    private readonly Stepper _minutes = new("MINUTES", 59);
    private readonly Stepper _seconds = new("SECONDS", 59);
    private readonly Button _start;
    private readonly Button _cancel;
    private readonly StackPanel _setup;

    public PowerCutsPage(AppController app)
    {
        _app = app;
        _cuts = app.PowerCuts;

        _clock = new TextBlock
        {
            FontFamily = Ui.Res<FontFamily>("HeadingFont"),
            FontWeight = FontWeights.Bold,
            FontSize = 84,
            Margin = new Thickness(0, -4, 0, -8),
        };

        _shutdown = new RadioButton { Content = "Shutdown", GroupName = "pc-mode", Style = Ui.Res<Style>("Segment"), IsChecked = true };
        _restart = new RadioButton { Content = "Restart", GroupName = "pc-mode", Style = Ui.Res<Style>("Segment") };
        _shutdown.Checked += (_, _) => Refresh();
        _restart.Checked += (_, _) => Refresh();

        var presets = new WrapPanel();
        foreach (var (label, minutes) in Presets)
        {
            var b = new Button { Content = label, Style = Ui.Res<Style>("SecondaryButton"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) => SetDuration(TimeSpan.FromMinutes(minutes));
            presets.Children.Add(b);
        }

        _minutes.Value = 30;
        foreach (var s in new[] { _hours, _minutes, _seconds })
            s.Changed += (_, _) => Refresh();

        var steppers = Ui.Row(_hours, _minutes, _seconds);

        _start = new Button { Style = Ui.Res<Style>("DangerButton"), Content = IconLabel("power", "START", Brushes.White), Padding = new Thickness(26, 10, 26, 10) };
        _start.Click += (_, _) => Start();
        _cancel = new Button { Style = Ui.Res<Style>("SuccessButton"), Content = IconLabel("x", "CANCEL / ABORT", Ui.B("AccentText")), Padding = new Thickness(26, 10, 26, 10), Margin = new Thickness(10, 0, 0, 0) };
        _cancel.Click += (_, _) => _cuts.Cancel();
        var buttons = Ui.Row(_start, _cancel);
        buttons.Margin = new Thickness(0, 24, 0, 0);

        _setup = Ui.Stack(
            Label("MODE"), Ui.Row(_shutdown, _restart),
            Label("QUICK PICK"), presets,
            Label("CUSTOM"), steppers);

        var timerCard = Ui.Card(Ui.Stack(_state, _clock, _when, _progress, _setup, buttons), new Thickness(28, 24, 28, 26));

        var info = Ui.Stack(Ui.PanelTitle("HOW IT WORKS"));
        foreach (var line in new[]
        {
            ("calendar-clock", "Durazzno App counts down, also from the tray. In the last 30 seconds Windows takes over and finishes it."),
            ("triangle-alert", "PowerCuts warns you 5 minutes and 1 minute before. Save your work before the time runs out."),
            ("x", "Cancel any time here or from the tray menu."),
            ("refresh-cw", "Starting a new timer replaces the previous one."),
        })
        {
            var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new LucideIcon { Icon = line.Item1, Width = 18, Height = 18, Foreground = _accent, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left });
            row.Children.Add(Ui.Text(line.Item2, "Body", Ui.B("TextMuted")).At(1));
            info.Children.Add(row);
        }

        var grid = Ui.Columns(2, 20);
        grid.ColumnDefinitions[0].Width = new GridLength(1.35, GridUnitType.Star);
        grid.Margin = new Thickness(0, 28, 0, 0);
        grid.Children.Add(timerCard.At(0));
        grid.Children.Add(Ui.Card(info).At(2));

        var logo = new LucideIcon { Icon = "img:Plugins.powercuts", Width = 52, Height = 52 };
        var chip = new Border { Width = 64, Height = 64, Background = Imaging.Brush(PowerCutsService.Accent, 0x1F), Child = logo };
        var title = Ui.Stack(Ui.Text(PowerCutsService.Name, "PageTitle"), Ui.Text("Schedule a shutdown or restart", "PageSubtitle"));
        title.Margin = new Thickness(20, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;

        Content = Ui.Stack(Ui.Row(chip, title), grid);

        _cuts.Changed += OnChanged;
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Refresh(); _timer.Start(); }
            else _timer.Stop();
        };
        Refresh();
    }

    public void Dispose()
    {
        _timer.Stop();
        _cuts.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private TimeSpan Selected => new(_hours.Value, _minutes.Value, _seconds.Value);

    private PowerAction SelectedAction => _restart.IsChecked == true ? PowerAction.Restart : PowerAction.Shutdown;

    private void SetDuration(TimeSpan t)
    {
        _hours.Value = (int)t.TotalHours;
        _minutes.Value = t.Minutes;
        _seconds.Value = t.Seconds;
    }

    private void Start()
    {
        if (_cuts.Start(Selected, SelectedAction) is { } error)
            _app.Notify(PowerCutsService.Name, error, System.Windows.Forms.ToolTipIcon.Error);
    }

    private void Refresh()
    {
        if (_cuts.IsActive)
        {
            var verb = _cuts.Action == PowerAction.Restart ? "RESTART" : "SHUTDOWN";
            _state.Text = $"{verb} IN";
            _state.Foreground = Ui.B("Danger");
            _clock.Text = PowerCutsService.Format(_cuts.Remaining);
            _clock.Foreground = Ui.B("Danger");
            _when.Text = $"{(_cuts.Action == PowerAction.Restart ? "Restarts" : "Shuts down")} at {_cuts.Deadline:dddd HH:mm:ss}";
            _progress.Content = Ui.UsageBar(_cuts.Progress * 100, Ui.B("Danger"), 8);
            _setup.Visibility = Visibility.Collapsed;
            _start.IsEnabled = false;
            _cancel.IsEnabled = true;
        }
        else
        {
            var selected = Selected;
            _state.Text = "SCHEDULED TIME";
            _state.Foreground = Ui.B("TextMuted");
            _clock.Text = PowerCutsService.Format(selected);
            _clock.Foreground = _accent;
            _when.Text = selected > TimeSpan.Zero
                ? $"Would {(SelectedAction == PowerAction.Restart ? "restart" : "shut down")} at {DateTime.Now + selected:dddd HH:mm}"
                : "Pick a duration below";
            _progress.Content = Ui.UsageBar(0, _accent, 8);
            _setup.Visibility = Visibility.Visible;
            _start.IsEnabled = selected > TimeSpan.Zero;
            _cancel.IsEnabled = false;
        }
    }

    private static TextBlock Label(string text)
    {
        var t = Ui.Text(text, "CardLabel", Ui.B("TextDim"));
        t.Margin = new Thickness(0, 20, 0, 8);
        return t;
    }

    private static StackPanel IconLabel(string icon, string text, Brush foreground) => Ui.Row(
        new LucideIcon { Icon = icon, Width = 16, Height = 16, Foreground = foreground },
        new TextBlock { Text = text, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

    /// <summary>
    /// Label + value with − / + buttons. The value can also be typed (digits only, kept within 0..max), changed with the
    /// mouse wheel or the ↑ / ↓ keys.
    /// </summary>
    private sealed class Stepper : StackPanel
    {
        private readonly int _max;
        private readonly TextBox _value;
        private int _current;

        public Stepper(string label, int max)
        {
            _max = max;
            Margin = new Thickness(0, 0, 14, 0);

            _value = new TextBox
            {
                Style = new Style(typeof(TextBox)), // not the app's search box style
                Width = 64,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontFamily = Ui.Res<FontFamily>("HeadingFont"),
                FontWeight = FontWeights.Bold,
                FontSize = 24,
                Foreground = Ui.B("Text"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CaretBrush = Ui.B("Accent"),
                SelectionBrush = Ui.B("Accent"),
                MaxLength = max.ToString().Length,
                Text = "0",
            };
            System.Windows.Input.InputMethod.SetIsInputMethodEnabled(_value, false);
            _value.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
            System.Windows.DataObject.AddPastingHandler(_value, (_, e) =>
            {
                if (e.DataObject.GetData(typeof(string)) is not string text || !text.All(char.IsDigit))
                    e.CancelCommand();
            });
            _value.TextChanged += (_, _) =>
            {
                if (_value.Text.Length == 0)
                    return; // being typed; becomes 0 when focus leaves
                if (int.TryParse(_value.Text, out var typed))
                    Value = typed;
            };
            _value.LostKeyboardFocus += (_, _) => _value.Text = _current.ToString();
            _value.GotKeyboardFocus += (_, _) => _value.SelectAll();
            _value.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (_value.IsKeyboardFocusWithin)
                    return;
                e.Handled = true; // first click selects the whole value, ready to type over
                _value.Focus();
            };
            _value.PreviewKeyDown += (_, e) =>
            {
                switch (e.Key)
                {
                    case System.Windows.Input.Key.Up: Value++; _value.SelectAll(); e.Handled = true; break;
                    case System.Windows.Input.Key.Down: Value--; _value.SelectAll(); e.Handled = true; break;
                    case System.Windows.Input.Key.Enter: System.Windows.Input.Keyboard.ClearFocus(); e.Handled = true; break;
                }
            };

            var minus = Small("minus", -1);
            var plus = Small("plus", +1);
            var box = new Border
            {
                Background = Ui.B("HoverBg"),
                BorderBrush = Ui.B("PanelBorder"),
                BorderThickness = new Thickness(1),
                Child = Ui.Row(minus, _value, plus),
            };
            box.MouseWheel += (_, e) => Value += e.Delta > 0 ? 1 : -1;

            var caption = Ui.Text(label, "CardHint");
            caption.Margin = new Thickness(0, 0, 0, 6);
            Children.Add(caption);
            Children.Add(box);
        }

        public event EventHandler? Changed;

        public int Value
        {
            get => _current;
            set
            {
                var v = Math.Clamp(value, 0, _max);
                if (v == _current && _value.Text == v.ToString())
                    return;
                _current = v;
                if (_value.Text != v.ToString())
                {
                    _value.Text = v.ToString(); // typed past the max (or a leading 0): show what counts
                    _value.CaretIndex = _value.Text.Length;
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        private Button Small(string icon, int delta)
        {
            var b = new Button
            {
                Style = Ui.Res<Style>("TileButton"),
                Width = 40,
                Height = 44,
                Content = new LucideIcon { Icon = icon, Width = 16, Height = 16, Foreground = Ui.B("TextMuted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            b.Click += (_, _) => Value += delta;
            return b;
        }
    }
}
