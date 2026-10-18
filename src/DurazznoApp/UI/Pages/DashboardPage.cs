using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Durazzno.Hardware;
using Durazzno.Network;
using Durazzno.Tools;
using Durazzno.UI.Controls;

namespace Durazzno.UI.Pages;

/// <summary>Live overview of this PC: KPIs, CPU history, hardware, drives and quick access to the tools.</summary>
internal sealed class DashboardPage : UserControl, IDisposable
{
    private const int HistorySeconds = 60;

    private readonly AppController _app;
    private readonly Action<string> _navigate;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CpuSampler _cpu = new();
    private readonly Queue<double> _cpuHistory = new();

    private readonly StatCard _cpuCard;
    private readonly StatCard _memoryCard;
    private readonly StatCard _storageCard;
    private readonly StatCard _uptimeCard;
    private readonly BarChart _chart = new() { Height = 210, Capacity = HistorySeconds };
    private readonly TextBlock _chartNow;
    private readonly StackPanel _drives = new();
    private readonly TextBlock _connectionValue;
    private int _ticks;

    public DashboardPage(AppController app, Action<string> navigate)
    {
        _app = app;
        _navigate = navigate;

        _cpuCard = new StatCard("CPU USAGE", "cpu", Ui.B("Accent"), Ui.B("AccentChip"));
        _memoryCard = new StatCard("MEMORY", "memory-stick", Ui.B("Accent"), Ui.B("AccentChip"));
        _storageCard = new StatCard("STORAGE", "hard-drive", Ui.B("Accent"), Ui.B("AccentChip"));
        _uptimeCard = new StatCard("UPTIME", "clock", Ui.B("Text"), Ui.B("SelectedBg"));
        _chartNow = Ui.Text("", "CardLabel", Ui.B("Accent"));
        _chart.BarBrush = Ui.B("Accent");
        _chart.GridBrush = Ui.B("PanelBorder");
        _connectionValue = Ui.Text("", "CardHint");

        var kpis = Ui.Columns(4, 20);
        kpis.Margin = new Thickness(0, 28, 0, 0);
        kpis.Children.Add(_cpuCard.At(0));
        kpis.Children.Add(_memoryCard.At(2));
        kpis.Children.Add(_storageCard.At(4));
        kpis.Children.Add(_uptimeCard.At(6));

        var main = Ui.Columns(3, 20);
        main.Margin = new Thickness(0, 20, 0, 0);
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.Children.Add(BuildChartPanel().At(0, 0, columnSpan: 3));
        main.Children.Add(BuildSystemPanel().At(4, 0, rowSpan: 3));
        main.Children.Add(BuildDrivesPanel().At(0, 2));
        main.Children.Add(BuildToolsPanel().At(2, 2));

        Content = Ui.Stack(
            Ui.Text("Dashboard", "PageTitle"),
            Ui.Text($"{SystemInfo.MachineName}  ·  {SystemInfo.OsName}", "PageSubtitle"),
            kpis,
            main);

        _timer.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Tick();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        };
        _app.StatusChanged += OnStatusChanged;
        RefreshDrives();
        RefreshConnection();
    }

    public void Dispose()
    {
        _timer.Stop();
        _app.StatusChanged -= OnStatusChanged;
    }

    private void Tick()
    {
        var cpu = _cpu.Sample();
        _cpuHistory.Enqueue(cpu);
        while (_cpuHistory.Count > HistorySeconds)
            _cpuHistory.Dequeue();
        _chart.SetSamples(_cpuHistory.ToArray());
        _chartNow.Text = $"{cpu:0} %";
        _cpuCard.Set($"{cpu:0}%", $"{SystemInfo.LogicalProcessors} threads");

        var (total, available) = Memory.Read();
        var used = total - available;
        _memoryCard.Set(SystemInfo.FormatBytes(used), $"of {SystemInfo.FormatBytes(total)}  ·  {(total > 0 ? used * 100.0 / total : 0):0}% in use");

        var uptime = SystemInfo.Uptime;
        _uptimeCard.Set(SystemInfo.FormatUptime(uptime), $"Since {DateTime.Now - uptime:MMM d, HH:mm}");

        if (++_ticks % 15 == 0)
            RefreshDrives();
    }

    private UIElement BuildChartPanel()
    {
        var header = new Grid();
        header.Children.Add(Ui.PanelTitle("CPU USAGE  ·  LAST 60 S"));
        _chartNow.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(_chartNow);

        var axis = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        axis.Children.Add(Ui.Text("60 s ago", "CardHint"));
        axis.Children.Add(new TextBlock { Text = "30 s", Style = Ui.Res<Style>("CardHint"), HorizontalAlignment = HorizontalAlignment.Center });
        axis.Children.Add(new TextBlock { Text = "now", Style = Ui.Res<Style>("CardHint"), HorizontalAlignment = HorizontalAlignment.Right });

        _chart.Margin = new Thickness(0, 26, 0, 0);
        return Ui.Card(Ui.Stack(header, _chart, axis));
    }

    private UIElement BuildSystemPanel()
    {
        var header = new Grid();
        header.Children.Add(Ui.PanelTitle("SYSTEM"));
        var live = Ui.Row(
            new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Ui.B("Danger"), VerticalAlignment = VerticalAlignment.Center },
            Ui.Text("LIVE", "CardLabel"));
        ((FrameworkElement)live.Children[1]).Margin = new Thickness(8, 0, 0, 0);
        live.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(live);

        var rows = Ui.Stack(header);
        rows.Children.Add(SystemRow("cpu", "Processor", SystemInfo.Processor, Ui.B("Accent")));
        rows.Children.Add(SystemRow("monitor", "Graphics", SystemInfo.Graphics, Ui.B("Accent")));
        rows.Children.Add(SystemRow("memory-stick", "Memory", $"{SystemInfo.FormatBytes(SystemInfo.TotalMemory)} installed", Ui.B("Accent")));
        rows.Children.Add(SystemRow("server", "Motherboard", SystemInfo.Motherboard, Ui.B("Accent")));
        rows.Children.Add(SystemRow("app-window", "Operating system", SystemInfo.OsName, Ui.B("Accent")));
        rows.Children.Add(SystemRow("activity", "Durazzno connection", _connectionValue, Ui.B("Info")));
        return Ui.Card(rows);
    }

    private static UIElement SystemRow(string icon, string title, object value, Brush accent)
    {
        var valueElement = value as TextBlock ?? Ui.Text((string)value, "CardHint");
        valueElement.TextWrapping = TextWrapping.Wrap;
        valueElement.TextTrimming = TextTrimming.None;

        var grid = new Grid { Margin = new Thickness(-22, 22, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Border { Background = accent });
        grid.Children.Add(new LucideIcon { Icon = icon, Width = 20, Height = 20, Foreground = accent, VerticalAlignment = VerticalAlignment.Center }.At(1));

        var title_ = new TextBlock
        {
            Text = title,
            FontFamily = Ui.Res<FontFamily>("HeadingFont"),
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            Foreground = Ui.B("Text"),
        };
        var text = Ui.Stack(title_, valueElement);
        text.Margin = new Thickness(0, 2, 0, 2);
        grid.Children.Add(text.At(2));
        return grid;
    }

    private UIElement BuildDrivesPanel()
    {
        _drives.Margin = new Thickness(0, 6, 0, 0);
        return Ui.Card(Ui.Stack(Ui.PanelTitle("DRIVES"), _drives));
    }

    private void RefreshDrives()
    {
        var drives = SystemInfo.Drives();
        _drives.Children.Clear();

        long total = drives.Sum(d => d.Total), free = drives.Sum(d => d.Free);
        _storageCard.Set(total > 0 ? $"{(total - free) * 100.0 / total:0}%" : "—",
            $"{SystemInfo.FormatBytes(free)} free of {SystemInfo.FormatBytes(total)}");

        if (drives.Count == 0)
            _drives.Children.Add(Ui.Text("No drives found", "CardHint"));

        foreach (var d in drives)
        {
            var top = new Grid { Margin = new Thickness(0, 18, 0, 8) };
            top.Children.Add(Ui.Row(
                new LucideIcon { Icon = "hard-drive", Width = 17, Height = 17, Foreground = Ui.B("TextMuted") },
                new TextBlock
                {
                    Text = $"{d.Letter}  {d.Label}",
                    Margin = new Thickness(10, 0, 0, 0),
                    FontFamily = Ui.Res<FontFamily>("HeadingFont"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 16,
                    Foreground = Ui.B("Text"),
                    VerticalAlignment = VerticalAlignment.Center,
                }));
            var size = Ui.Text($"{SystemInfo.FormatBytes(d.Free)} free of {SystemInfo.FormatBytes(d.Total)}", "CardHint");
            size.HorizontalAlignment = HorizontalAlignment.Right;
            size.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(size);

            var tone = d.UsedPercent >= 90 ? Ui.B("Danger") : d.UsedPercent >= 75 ? Ui.B("Warning") : Ui.B("Accent");
            var meta = Ui.Text($"{d.Format}  ·  {d.Kind}  ·  {d.UsedPercent:0}% used", "CardHint", Ui.B("TextDim"));
            meta.Margin = new Thickness(0, 6, 0, 0);

            _drives.Children.Add(Ui.Stack(top, Ui.UsageBar(d.UsedPercent, tone), meta));
        }
    }

    private UIElement BuildToolsPanel()
    {
        var list = Ui.Stack(Ui.PanelTitle("TOOLS"));
        var items = new StackPanel { Margin = new Thickness(-10, 10, -10, 0) };
        foreach (var tool in ToolCatalog.All.Where(t => !t.External))
        {
            var accent = Imaging.Brush(tool.Accent);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Ui.IconChip(tool.Icon, accent, Imaging.Brush(tool.Accent, 0x26), 36, 18));

            var name = new TextBlock
            {
                Text = tool.Name,
                FontFamily = Ui.Res<FontFamily>("HeadingFont"),
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Foreground = Ui.B("Text"),
            };
            var text = Ui.Stack(name, Ui.Text(tool.Tagline, "CardHint"));
            text.Margin = new Thickness(14, 0, 8, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text.At(1));
            row.Children.Add(new LucideIcon { Icon = "chevron-right", Width = 16, Height = 16, Foreground = Ui.B("TextDim"), VerticalAlignment = VerticalAlignment.Center }.At(2));

            var button = new Button { Style = Ui.Res<Style>("TileButton"), Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => _navigate(tool.Id);
            items.Children.Add(button);
        }

        list.Children.Add(items);
        return Ui.Card(list);
    }

    private void OnStatusChanged(object? sender, EventArgs e) => RefreshConnection();

    private void RefreshConnection()
    {
        var c = _app.Connection;
        _connectionValue.Text = c.State switch
        {
            ConnectionState.Online when c.Manifest is { HasUpdate: true } => $"Online  ·  update {c.Manifest.App.Version} available",
            ConnectionState.Online => "Online  ·  up to date",
            ConnectionState.ServerUnreachable => "Webin Cloud Server unreachable",
            ConnectionState.NoInternet => "No internet connection",
            _ => "Connecting…",
        };
    }
}
