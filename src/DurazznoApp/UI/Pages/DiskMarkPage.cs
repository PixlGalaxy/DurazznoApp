using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Durazzno.Hardware;
using Durazzno.Hardware.Disks;
using Durazzno.Tools;
using Durazzno.UI.Controls;

namespace Durazzno.UI.Pages;

/// <summary>DiskMark: CrystalDiskMark-style sequential / random benchmark of any drive.</summary>
internal sealed class DiskMarkPage : UserControl, IDisposable
{
    private static readonly int[] Sizes = [512, 1024, 4096, 16384];
    private static readonly int[] PassOptions = [1, 3, 5];

    private readonly AppController _app;
    private readonly DiskMarkService _bench;
    private readonly ToolDefinition _tool;
    private readonly Brush _readBrush = Imaging.Brush("#10B981");
    private readonly Brush _writeBrush = Imaging.Brush("#38BDF8");

    private readonly TileStrip _drives = new() { Margin = new Thickness(0, 22, 0, 4) };
    private readonly ContentControl _results = new() { Focusable = false };
    private readonly TextBlock _status = Ui.Text("", "Body", Ui.B("TextMuted"));
    private readonly ContentControl _progress = new() { Focusable = false, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _start;
    private readonly Button _stop;
    private readonly StackPanel _options;

    private string _drive;
    private BenchProfile _profile = BenchProfile.Default;
    private int _sizeMiB = 1024;
    private int _passes = 3;
    private BenchMode _mode = BenchMode.ReadWrite;

    public DiskMarkPage(AppController app, ToolDefinition tool)
    {
        _app = app;
        _bench = app.DiskMark;
        _tool = tool;
        _drive = _bench.Current?.Drive ?? Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');

        var accent = Imaging.Brush(tool.Accent);
        var title = Ui.Stack(Ui.Text(tool.Name, "PageTitle"), Ui.Text(tool.Tagline, "PageSubtitle"));
        title.Margin = new Thickness(20, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;

        var copy = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = IconLabel("copy", "COPY RESULTS", Ui.B("Text")), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        copy.Click += (_, _) => CopyResults();

        var header = new Grid();
        header.Children.Add(Ui.Row(Ui.IconChip(tool.Icon, accent, Imaging.Brush(tool.Accent, 0x26), 64, 30), title));
        header.Children.Add(copy);

        _start = new Button { Style = Ui.Res<Style>("PrimaryButton"), Content = IconLabel("zap", "START BENCHMARK", Ui.B("AccentText")), Padding = new Thickness(22, 10, 22, 10), HorizontalAlignment = HorizontalAlignment.Stretch };
        _start.Click += (_, _) => _bench.Start(_drive, _profile, _sizeMiB, _passes, _mode);
        _stop = new Button { Style = Ui.Res<Style>("DangerButton"), Content = IconLabel("x", "STOP", Brushes.White), Padding = new Thickness(22, 10, 22, 10), HorizontalAlignment = HorizontalAlignment.Stretch };
        _stop.Click += (_, _) => _bench.Stop();

        _options = Ui.Stack(
            Label("PROFILE"), Segments("dm-profile", ["Default", "NVMe SSD"], (int)_profile, i => _profile = (BenchProfile)i),
            Label("TEST SIZE"), Segments("dm-size", Sizes.Select(DiskMarkService.FormatSize).ToArray(), Array.IndexOf(Sizes, _sizeMiB), i => _sizeMiB = Sizes[i]),
            Label("PASSES (BEST OF)"), Segments("dm-passes", PassOptions.Select(p => p.ToString()).ToArray(), Array.IndexOf(PassOptions, _passes), i => _passes = PassOptions[i]),
            Label("MODE"), Segments("dm-mode", ["Read & Write", "Read", "Write"], (int)_mode, i => _mode = (BenchMode)i));

        var buttonArea = new Grid { Margin = new Thickness(0, 26, 0, 0) };
        buttonArea.Children.Add(_start);
        buttonArea.Children.Add(_stop);
        _status.Margin = new Thickness(0, 14, 0, 0);

        var note = Ui.Text("Each pass runs for 5 s. Writes go to a temporary file that is deleted afterwards.", "CardHint", Ui.B("TextDim"));
        note.TextWrapping = TextWrapping.Wrap;
        note.TextTrimming = TextTrimming.None;
        note.Margin = new Thickness(0, 14, 0, 0);

        var settings = Ui.Card(Ui.Stack(Ui.PanelTitle("TEST"), _options, buttonArea, _status, _progress, note));

        var grid = Ui.Columns(2, 20);
        grid.ColumnDefinitions[0].Width = new GridLength(0.75, GridUnitType.Star);
        grid.ColumnDefinitions[2].Width = new GridLength(2, GridUnitType.Star);
        grid.Children.Add(settings.At(0));
        grid.Children.Add(Ui.Card(_results).At(2));

        Content = Ui.Stack(header, _drives, grid);

        _bench.Changed += OnChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Render();
        };
        Render();
    }

    public void Dispose() => _bench.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => Render();

    private BenchRun? Shown => _bench.Current is { } current && (current.Drive == _drive || _bench.IsRunning)
        ? current
        : _bench.LastRun(_drive);

    private void Render()
    {
        var running = _bench.IsRunning;
        _start.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _stop.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _start.IsEnabled = _bench.IsAvailable;
        _options.IsEnabled = !running;
        _drives.IsEnabled = !running;

        _status.Text = !_bench.IsAvailable ? "DiskSpd could not be prepared (see the log)."
            : running || _bench.Current?.Drive == _drive ? _bench.Status
            : _bench.LastRun(_drive) is { } last ? $"Last run {last.Finished:MMM d, HH:mm}" : "Ready";
        _progress.Content = running ? Ui.UsageBar(_bench.Progress * 100, Ui.B("Accent"), 6) : null;

        _drives.SetItems(SystemInfo.Drives().Select(DriveTile));
        _results.Content = Results(Shown);
    }

    // ── Drive tiles ───────────────────────────────────────────────────────────────────────

    private UIElement DriveTile(DriveSnapshot d)
    {
        var selected = d.Letter == _drive;
        var letter = new TextBlock { Text = d.Letter, FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 26, Foreground = selected ? Ui.B("Accent") : Ui.B("Text") };
        var label = Ui.Text(d.Label, "CardLabel", Ui.B("Text"));
        label.Margin = new Thickness(12, 0, 0, 2);
        label.VerticalAlignment = VerticalAlignment.Bottom;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.MaxWidth = 140;

        var size = Ui.Text($"{SystemInfo.FormatBytes(d.Free)} free of {SystemInfo.FormatBytes(d.Total)}", "CardHint");
        size.Margin = new Thickness(0, 6, 0, 8);
        var bar = Ui.UsageBar(d.UsedPercent, d.UsedPercent >= 90 ? Ui.B("Danger") : Ui.B("Accent"), 4);

        var card = new ChamferBorder
        {
            Width = 240,
            Cut = 12,
            Padding = new Thickness(18, 14, 18, 16),
            Margin = new Thickness(0, 0, 16, 16),
            Background = selected ? Ui.B("SelectedBg") : Ui.B("PanelBg"),
            BorderBrush = selected ? Ui.B("Accent") : Ui.B("PanelBorder"),
            Child = Ui.Stack(Ui.Row(letter, label), size, bar),
            Cursor = Cursors.Hand,
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (_bench.IsRunning)
                return;
            _drive = d.Letter;
            Render();
        };
        return card;
    }

    // ── Results ───────────────────────────────────────────────────────────────────────────

    private UIElement Results(BenchRun? run)
    {
        var profile = run?.Profile ?? _profile;
        var tests = DiskMarkService.Profiles[profile];

        var header = new Grid();
        header.Children.Add(Ui.PanelTitle($"RESULTS  ·  {_drive}"));
        if (run?.Read.GetValueOrDefault(tests[0].Key) is { } seq)
        {
            var (grade, tone) = Grade(seq.MBs);
            var badge = new Border
            {
                Background = Imaging.Brush(tone, 0x26),
                Padding = new Thickness(10, 3, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = grade, FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 13, Foreground = Imaging.Brush(tone) },
            };
            header.Children.Add(badge);
        }

        var table = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.Children.Add(ColumnHeader("TEST", null).At(0));
        table.Children.Add(ColumnHeader("READ  (MB/s)", _readBrush).At(2));
        table.Children.Add(ColumnHeader("WRITE  (MB/s)", _writeBrush).At(4));

        for (var i = 0; i < tests.Length; i++)
        {
            var test = tests[i];
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = i + 1;

            var name = new TextBlock { FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 22, Foreground = Ui.B("Text") };
            name.Inlines.Add(test.Name + " ");
            name.Inlines.Add(new System.Windows.Documents.Run(test.Detail) { Foreground = Ui.B("TextMuted") });
            var desc = Ui.Text(test.Description, "CardHint");
            var label = Ui.Stack(name, desc);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 14, 0, 14);

            table.Children.Add(new Border { Height = 1, Background = Ui.B("Divider"), VerticalAlignment = VerticalAlignment.Top }.At(0, row, 5));
            table.Children.Add(label.At(0, row));
            table.Children.Add(Cell(run?.Read.GetValueOrDefault(test.Key), test, _readBrush).At(2, row));
            table.Children.Add(Cell(run?.Write.GetValueOrDefault(test.Key), test, _writeBrush).At(4, row));
        }

        var meta = Ui.Text(run is null
            ? "No results for this drive yet. Choose the settings on the left and press Start."
            : $"{run.Drive}  ·  {DiskMarkService.FormatSize(run.SizeMiB)} test file  ·  best of {run.Passes}  ·  {(run.Profile == BenchProfile.Nvme ? "NVMe SSD" : "Default")} profile"
              + (run.Finished == default ? "" : $"  ·  {run.Finished:MMM d yyyy, HH:mm}"), "CardHint");
        meta.Margin = new Thickness(0, 14, 0, 0);

        return Ui.Stack(header, table, meta);
    }

    private static UIElement ColumnHeader(string text, Brush? tone)
    {
        var t = Ui.Text(text, "CardLabel", tone ?? Ui.B("TextDim"));
        t.Margin = new Thickness(0, 0, 0, 8);
        return t;
    }

    private static UIElement Cell(BenchResult? r, BenchTest test, Brush tone)
    {
        var value = new TextBlock
        {
            Text = r is null ? "—" : r.MBs.ToString("N2", CultureInfo.InvariantCulture),
            FontFamily = Ui.Res<FontFamily>("HeadingFont"),
            FontWeight = FontWeights.Bold,
            FontSize = 32,
            Foreground = r is null ? Ui.B("TextDim") : Ui.B("Text"),
        };
        var bar = Ui.UsageBar(r is null ? 0 : r.MBs / test.ReferenceMBs * 100, tone, 5);
        bar.Margin = new Thickness(0, 4, 0, 6);
        var detail = Ui.Text(r is null ? " " : $"{r.Iops:N0} IOPS  ·  {r.LatencyUs:N1} µs", "CardHint");

        var stack = Ui.Stack(value, bar, detail);
        stack.VerticalAlignment = VerticalAlignment.Center;
        stack.Margin = new Thickness(0, 12, 0, 12);
        return stack;
    }

    private static (string Text, string Color) Grade(double seqReadMBs) => seqReadMBs switch
    {
        >= 5000 => ("PCIe 4.0+ NVMe CLASS", "#22C55E"),
        >= 2000 => ("PCIe 3.0 NVMe CLASS", "#22C55E"),
        >= 400 => ("SATA SSD CLASS", "#38BDF8"),
        >= 120 => ("HDD / USB 3 CLASS", "#F59E0B"),
        _ => ("SLOW / USB 2 CLASS", "#EF4444"),
    };

    private void CopyResults()
    {
        if (Shown is not { } run)
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"Durazzno App DiskMark  ·  {run.Drive}  ·  {DiskMarkService.FormatSize(run.SizeMiB)}  ·  best of {run.Passes}  ·  {run.Finished:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"{"",-16}{"Read MB/s",14}{"Write MB/s",14}");
        foreach (var t in DiskMarkService.Profiles[run.Profile])
        {
            string F(BenchResult? r) => r is null ? "-" : r.MBs.ToString("F2", CultureInfo.InvariantCulture);
            sb.AppendLine($"{t.Key,-16}{F(run.Read.GetValueOrDefault(t.Key)),14}{F(run.Write.GetValueOrDefault(t.Key)),14}");
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            _app.NotifyTool(_tool, "Results copied to the clipboard.");
        }
        catch
        {
            // Clipboard busy; ignore.
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    private static TextBlock Label(string text)
    {
        var t = Ui.Text(text, "CardLabel", Ui.B("TextDim"));
        t.Margin = new Thickness(0, 18, 0, 8);
        return t;
    }

    private static WrapPanel Segments(string group, string[] labels, int selected, Action<int> apply)
    {
        var panel = new WrapPanel();
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var b = new RadioButton { Content = labels[i], GroupName = group, Style = Ui.Res<Style>("Segment"), Margin = new Thickness(0, 0, 0, 6), IsChecked = i == selected };
            b.Checked += (_, _) => apply(index);
            panel.Children.Add(b);
        }

        return panel;
    }

    private static StackPanel IconLabel(string icon, string text, Brush foreground) => Ui.Row(
        new LucideIcon { Icon = icon, Width = 16, Height = 16, Foreground = foreground },
        new TextBlock { Text = text, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
}
