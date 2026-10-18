using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Durazzno.Hardware;
using Durazzno.Hardware.Disks;
using Durazzno.Tools;
using Durazzno.UI.Controls;

namespace Durazzno.UI.Pages;

/// <summary>DiskInfo: health, temperature, endurance and the full S.M.A.R.T. table of every physical disk.</summary>
internal sealed class DiskInfoPage : UserControl, IDisposable
{
    private readonly AppController _app;
    private readonly ToolDefinition _tool;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly TileStrip _driveTiles = new() { Margin = new Thickness(0, 22, 0, 4) };
    private readonly ContentControl _detail = new() { Focusable = false };
    private readonly ContentControl _banner = new() { Focusable = false };
    private readonly TextBlock _updated = Ui.Text("", "CardHint");
    private readonly Button _refresh;

    private List<DiskReport> _disks = [];
    private int _selected = -1;
    private bool _scanning;
    private bool _showSerial;

    public DiskInfoPage(AppController app, ToolDefinition tool)
    {
        _app = app;
        _tool = tool;

        _refresh = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = IconLabel("refresh-cw", "REFRESH", Ui.B("Text")) };
        _refresh.Click += async (_, _) => await RefreshAsync();

        var accent = Imaging.Brush(tool.Accent);
        var titleBlock = Ui.Stack(Ui.Text(tool.Name, "PageTitle"), Ui.Text(tool.Tagline, "PageSubtitle"));
        titleBlock.Margin = new Thickness(20, 0, 0, 0);
        titleBlock.VerticalAlignment = VerticalAlignment.Center;

        var actions = Ui.Stack(_refresh, _updated);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Center;
        _updated.HorizontalAlignment = HorizontalAlignment.Right;
        _updated.Margin = new Thickness(0, 8, 0, 0);

        var header = new Grid();
        header.Children.Add(Ui.Row(Ui.IconChip(tool.Icon, accent, Imaging.Brush(tool.Accent, 0x26), 64, 30), titleBlock));
        header.Children.Add(actions);

        Content = Ui.Stack(header, _banner, _driveTiles, _detail);

        _timer.Tick += async (_, _) => await RefreshAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible)
            {
                await RefreshAsync();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    public void Dispose() => _timer.Stop();

    private async Task RefreshAsync()
    {
        if (_scanning)
            return;

        _scanning = true;
        _refresh.IsEnabled = false;
        try
        {
            var previous = _selected >= 0 && _selected < _disks.Count ? _disks[_selected].Index : (int?)null;
            _disks = (await _app.Disks.ScanAsync()).Where(d => !d.IsVirtual).ToList();
            _selected = Math.Max(0, previous is null ? 0 : _disks.FindIndex(d => d.Index == previous));
            _updated.Text = $"Updated {DateTime.Now:HH:mm:ss}  ·  refreshes every 30 s";
            Render();
        }
        catch (Exception ex)
        {
            Core.Log.Error("DiskInfo scan failed", ex);
            _detail.Content = Ui.Card(Ui.Text($"Could not read disk information: {ex.Message}", "Body"));
        }
        finally
        {
            _scanning = false;
            _refresh.IsEnabled = true;
        }
    }

    private void Render()
    {
        RenderBanner();
        _driveTiles.SetItems(Enumerable.Range(0, _disks.Count).Select(DriveTile));

        _detail.Content = _disks.Count == 0
            ? Ui.Card(Ui.Text("No physical disks found.", "Body"))
            : Detail(_disks[_selected]);
    }

    // ── Admin banner ──────────────────────────────────────────────────────────────────────

    private void RenderBanner()
    {
        if (_app.Disks.HasAdminAccess || !_disks.Any(d => d.SmartNeedsAdmin))
        {
            _banner.Content = null;
            return;
        }

        var grant = new Button { Style = Ui.Res<Style>("PrimaryButton"), Content = IconLabel("shield-check", "GRANT ACCESS", Ui.B("AccentText")), VerticalAlignment = VerticalAlignment.Center };
        grant.Click += async (_, _) =>
        {
            grant.IsEnabled = false;
            if (await _app.Disks.StartAgentAsync())
                await RefreshAsync();
            grant.IsEnabled = true;
        };

        var text = Ui.Stack(
            Ui.Text("Administrator access needed", "CardLabel", Ui.B("Text")),
            Ui.Text("Some drives (SATA / USB) only share their S.M.A.R.T. data with an elevated process. Durazzno will ask once per session.", "Body", Ui.B("TextMuted")));
        text.Margin = new Thickness(16, 0, 20, 0);
        text.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Ui.IconChip("shield-alert", Ui.B("Warning"), Ui.B("WarningChip")));
        grid.Children.Add(text.At(1));
        grid.Children.Add(grant.At(2));

        var card = Ui.Card(grid);
        card.Margin = new Thickness(0, 28, 0, 0);
        card.BorderBrush = Ui.B("Warning");
        _banner.Content = card;
    }

    // ── Drive selector ────────────────────────────────────────────────────────────────────

    private UIElement DriveTile(int i)
    {
        var d = _disks[i];
        var tone = HealthBrush(d.Health);
        var selected = i == _selected;

        var temp = new TextBlock
        {
            Text = d.TemperatureC is { } t ? $"{t}°C" : "—°C",
            FontFamily = Ui.Res<FontFamily>("HeadingFont"),
            FontWeight = FontWeights.Bold,
            FontSize = 22,
            Foreground = TempBrush(d),
        };
        var status = new TextBlock
        {
            Text = HealthText(d.Health),
            FontFamily = Ui.Res<FontFamily>("HeadingFont"),
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = tone,
            Margin = new Thickness(0, 0, 0, 2),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var top = new Grid();
        top.Children.Add(temp);
        status.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(status);

        var model = Ui.Text(d.Model, "CardLabel", Ui.B("Text"));
        model.Margin = new Thickness(0, 8, 0, 0);
        model.TextTrimming = TextTrimming.CharacterEllipsis;
        var letters = d.DriveLetters.Count > 0 ? string.Join(" ", d.DriveLetters) : "no volume";
        var meta = Ui.Text($"{letters}  ·  {SystemInfo.FormatBytes(d.SizeBytes)}  ·  {d.Kind}", "CardHint");

        var body = Ui.Stack(new Border { Height = 3, Background = tone, Margin = new Thickness(-18, -16, -18, 14) }, top, model, meta);
        var card = new ChamferBorder
        {
            Width = 260,
            Cut = 12,
            Padding = new Thickness(18, 16, 18, 16),
            Margin = new Thickness(0, 0, 16, 16),
            Background = selected ? Ui.B("SelectedBg") : Ui.B("PanelBg"),
            BorderBrush = selected ? Ui.B("Accent") : Ui.B("PanelBorder"),
            Child = body,
            Cursor = Cursors.Hand,
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            _selected = i;
            Render();
        };
        return card;
    }

    // ── Detail ────────────────────────────────────────────────────────────────────────────

    private UIElement Detail(DiskReport d)
    {
        var top = Ui.Columns(3, 20);
        top.Children.Add(HealthCard(d).At(0));
        top.Children.Add(TemperatureCard(d).At(2));
        top.Children.Add(UsageCard(d).At(4));

        var bottom = Ui.Columns(3, 20);
        bottom.Margin = new Thickness(0, 20, 0, 0);
        bottom.Children.Add(InfoCard(d).At(0));
        bottom.Children.Add(AttributesCard(d).At(2, columnSpan: 3));

        return Ui.Stack(top, bottom);
    }

    private UIElement HealthCard(DiskReport d)
    {
        var tone = HealthBrush(d.Health);
        var status = new TextBlock
        {
            Text = HealthText(d.Health),
            FontFamily = Ui.Res<FontFamily>("HeadingFont"),
            FontWeight = FontWeights.Bold,
            FontSize = 40,
            Foreground = tone,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var reason = Ui.Text(d.HealthReason, "Body", Ui.B("TextMuted"));
        reason.Margin = new Thickness(0, 6, 0, 0);

        var left = Ui.Stack(Ui.Text("HEALTH STATUS", "CardLabel"), status, reason);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);

        if (d.LifePercent is { } life)
        {
            var ring = new RingGauge { Width = 112, Height = 112, Value = life, RingBrush = tone, TrackBrush = Ui.B("HoverBg"), Thickness = 9 };
            var center = Ui.Stack(
                new TextBlock { Text = $"{life}%", FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 26, Foreground = Ui.B("Text"), HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "life left", Style = Ui.Res<Style>("CardHint"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -4, 0, 0) });
            center.VerticalAlignment = VerticalAlignment.Center;
            var gauge = new Grid { Margin = new Thickness(16, 0, 0, 0) };
            gauge.Children.Add(ring);
            gauge.Children.Add(center);
            grid.Children.Add(gauge.At(1));
        }

        return Ui.Card(grid);
    }

    private UIElement TemperatureCard(DiskReport d)
    {
        var tone = TempBrush(d);
        var value = new TextBlock
        {
            Text = d.TemperatureC is { } t ? $"{t}°C" : "—",
            FontFamily = Ui.Res<FontFamily>("HeadingFont"),
            FontWeight = FontWeights.Bold,
            FontSize = 40,
            Foreground = tone,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var stack = Ui.Stack(Ui.Text("TEMPERATURE", "CardLabel"), value);
        var critical = d.CriticalTemperatureC ?? 80;
        if (d.TemperatureC is { } temp)
        {
            var bar = Ui.UsageBar(temp * 100.0 / critical, tone, 8);
            bar.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(bar);
        }

        var limits = d.WarningTemperatureC is { } w ? $"Warning {w}°C  ·  Critical {critical}°C" : "No thresholds reported";
        var hint = Ui.Text(limits, "CardHint");
        hint.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(hint);

        if (d.Nvme is { SensorTemperaturesC.Length: > 0 } nvme)
        {
            var sensors = Ui.Text(string.Join("   ", nvme.SensorTemperaturesC.Select((s, i) => $"Sensor {i + 1}: {s}°C")), "CardHint", Ui.B("TextDim"));
            sensors.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(sensors);
        }

        return Ui.Card(stack);
    }

    private UIElement UsageCard(DiskReport d)
    {
        var rows = Ui.Stack(Ui.Text("USAGE", "CardLabel"));
        rows.Children.Add(Metric("Power-on time", d.PowerOnHours is { } h ? $"{h:N0} h" : "—", d.PowerOnHours is { } h2 && h2 >= 48 ? $"{h2 / 24.0:N0} days" : null));
        rows.Children.Add(Metric("Power cycles", d.PowerCycles is { } c ? $"{c:N0}" : "—", null));
        rows.Children.Add(Metric("Host writes", d.HostWritesBytes is { } wr ? SystemInfo.FormatBytes(wr) : "—", null));
        rows.Children.Add(Metric("Host reads", d.HostReadsBytes is { } rd ? SystemInfo.FormatBytes(rd) : "—", null));
        if (d.Nvme is { } n)
            rows.Children.Add(Metric("Unsafe shutdowns", $"{n.UnsafeShutdowns:N0}", null));
        return Ui.Card(rows);
    }

    private static UIElement Metric(string label, string value, string? suffix)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.Children.Add(Ui.Text(label, "CardHint"));
        var text = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        text.Inlines.Add(new System.Windows.Documents.Run(value)
        {
            FontFamily = Ui.Res<FontFamily>("HeadingFont"),
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            Foreground = Ui.B("Text"),
        });
        if (suffix is not null)
            text.Inlines.Add(new System.Windows.Documents.Run($"  ({suffix})") { Foreground = Ui.B("TextMuted"), FontSize = 12.5 });
        grid.Children.Add(text);
        return grid;
    }

    private UIElement InfoCard(DiskReport d)
    {
        var rows = Ui.Stack(Ui.PanelTitle("DRIVE"));
        rows.Children.Add(InfoRow("Model", d.Model));
        rows.Children.Add(InfoRow("Firmware", string.IsNullOrEmpty(d.Firmware) ? "—" : d.Firmware));

        var serial = InfoRow("Serial", string.IsNullOrEmpty(d.Serial) ? "—" : _showSerial ? d.Serial : Mask(d.Serial));
        serial.Cursor = Cursors.Hand;
        serial.ToolTip = "Click to show / hide";
        serial.MouseLeftButtonUp += (_, _) =>
        {
            _showSerial = !_showSerial;
            Render();
        };
        rows.Children.Add(serial);

        rows.Children.Add(InfoRow("Interface", d.BusType));
        rows.Children.Add(InfoRow("Type", d.Kind));
        rows.Children.Add(InfoRow("Capacity", SystemInfo.FormatBytes(d.SizeBytes)));
        rows.Children.Add(InfoRow("Volumes", d.DriveLetters.Count > 0 ? string.Join("  ", d.DriveLetters) : "—"));
        if (d.TrimEnabled is { } trim)
            rows.Children.Add(InfoRow("TRIM", trim ? "Enabled" : "Disabled"));
        rows.Children.Add(InfoRow("Read via", TransportName(d.Transport)));
        rows.Children.Add(InfoRow("Disk number", $"{d.Index}"));
        return Ui.Card(rows);
    }

    private static Grid InfoRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 9, 0, 0), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(Ui.Text(label, "CardHint"));
        var v = Ui.Text(value, "Body");
        v.TextWrapping = TextWrapping.Wrap;
        grid.Children.Add(v.At(1));
        return grid;
    }

    private UIElement AttributesCard(DiskReport d)
    {
        var stack = Ui.Stack(Ui.PanelTitle("S.M.A.R.T."));

        if (!d.HasSmart)
        {
            var msg = Ui.Text(d.SmartError ?? "This drive does not report S.M.A.R.T. data.", "Body", Ui.B("TextMuted"));
            msg.Margin = new Thickness(0, 16, 0, 0);
            stack.Children.Add(msg);
            return Ui.Card(stack);
        }

        var table = d.Nvme is { } nvme ? NvmeTable(nvme) : AtaTable(d.Attributes);
        table.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(table);
        return Ui.Card(stack);
    }

    private static Grid NvmeTable(NvmeHealthLog n)
    {
        var rows = new (string Id, string Name, string Value, ulong Raw, bool Warn)[]
        {
            ("01", "Critical Warning", n.CriticalWarning == 0 ? "None" : $"0x{n.CriticalWarning:X2}", n.CriticalWarning, n.CriticalWarning != 0),
            ("02", "Composite Temperature", $"{n.TemperatureC}°C", (ulong)(n.TemperatureC + 273), false),
            ("03", "Available Spare", $"{n.AvailableSpare} %", n.AvailableSpare, n.AvailableSpare < n.AvailableSpareThreshold),
            ("04", "Available Spare Threshold", $"{n.AvailableSpareThreshold} %", n.AvailableSpareThreshold, false),
            ("05", "Percentage Used", $"{n.PercentageUsed} %", n.PercentageUsed, n.PercentageUsed >= 90),
            ("06", "Data Units Read", SystemInfo.FormatBytes(NvmeHealthLog.UnitsToBytes(n.DataUnitsRead)), (ulong)n.DataUnitsRead, false),
            ("07", "Data Units Written", SystemInfo.FormatBytes(NvmeHealthLog.UnitsToBytes(n.DataUnitsWritten)), (ulong)n.DataUnitsWritten, false),
            ("08", "Host Read Commands", $"{n.HostReadCommands:N0}", (ulong)n.HostReadCommands, false),
            ("09", "Host Write Commands", $"{n.HostWriteCommands:N0}", (ulong)n.HostWriteCommands, false),
            ("0A", "Controller Busy Time", $"{n.ControllerBusyMinutes:N0} min", (ulong)n.ControllerBusyMinutes, false),
            ("0B", "Power Cycles", $"{n.PowerCycles:N0}", (ulong)n.PowerCycles, false),
            ("0C", "Power On Hours", $"{n.PowerOnHours:N0} h", (ulong)n.PowerOnHours, false),
            ("0D", "Unsafe Shutdowns", $"{n.UnsafeShutdowns:N0}", (ulong)n.UnsafeShutdowns, false),
            ("0E", "Media and Data Integrity Errors", $"{n.MediaErrors:N0}", (ulong)n.MediaErrors, n.MediaErrors > 0),
            ("0F", "Error Information Log Entries", $"{n.ErrorLogEntries:N0}", (ulong)n.ErrorLogEntries, false),
            ("10", "Warning Temperature Time", $"{n.WarningTempMinutes:N0} min", n.WarningTempMinutes, n.WarningTempMinutes > 0),
            ("11", "Critical Temperature Time", $"{n.CriticalTempMinutes:N0} min", n.CriticalTempMinutes, n.CriticalTempMinutes > 0),
        };

        var grid = Table([24, 46, 0, 160, 150], "", "ID", "ATTRIBUTE", "VALUE", "RAW");
        foreach (var r in rows)
            AddRow(grid, r.Warn ? Ui.B("Warning") : Ui.B("Success"), r.Id, r.Name, r.Value, $"{r.Raw:X12}");
        return grid;
    }

    private static Grid AtaTable(List<SmartAttribute> attributes)
    {
        var grid = Table([24, 46, 0, 76, 70, 90, 130], "", "ID", "ATTRIBUTE", "CURRENT", "WORST", "THRESHOLD", "RAW");
        foreach (var a in attributes)
        {
            var tone = a.IsFailing ? Ui.B("Danger")
                : SmartAnalysis.IsDamageCounter(a.Id) && a.Raw > 0 ? Ui.B("Warning")
                : Ui.B("Success");
            AddRow(grid, tone, $"{a.Id:X2}", a.Name, $"{a.Current}", $"{a.Worst}", $"{a.Threshold}", $"{a.Raw:X12}");
        }
        return grid;
    }

    /// <summary>Header row; widths of 0 mean "take the remaining space".</summary>
    private static Grid Table(double[] widths, params string[] headers)
    {
        var grid = new Grid();
        foreach (var w in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });

        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        for (var c = 0; c < headers.Length; c++)
        {
            var h = Ui.Text(headers[c], "CardLabel", Ui.B("TextDim"));
            h.FontSize = 13;
            h.VerticalAlignment = VerticalAlignment.Center;
            if (c >= 3)
                h.HorizontalAlignment = HorizontalAlignment.Right;
            grid.Children.Add(h.At(c));
        }

        grid.Children.Add(new Border { Height = 1, Background = Ui.B("PanelBorder"), VerticalAlignment = VerticalAlignment.Bottom }.At(0, 0, widths.Length));
        return grid;
    }

    private static void AddRow(Grid grid, Brush tone, params string[] cells)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) });

        if (row % 2 == 0)
            grid.Children.Add(new Border { Background = Ui.B("HoverBg") }.At(0, row, grid.ColumnDefinitions.Count));

        grid.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = tone, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }.At(0, row));

        for (var c = 0; c < cells.Length; c++)
        {
            var mono = c == 0 || c == cells.Length - 1;
            var text = new TextBlock
            {
                Text = cells[c],
                FontFamily = mono ? Ui.Res<FontFamily>("MonoFont") : Ui.Res<FontFamily>("BodyFont"),
                FontSize = mono ? 12.5 : 13,
                Foreground = c == 0 || mono ? Ui.B("TextMuted") : Ui.B("Text"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = c >= 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, c == cells.Length - 1 ? 10 : 0, 0),
            };
            if (c == 1)
                text.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(text.At(c + 1, row));
        }
    }

    // ── Formatting ────────────────────────────────────────────────────────────────────────

    private static string TransportName(SmartTransport t) => t switch
    {
        SmartTransport.NvmeStorageQuery => "NVMe (Windows storage query)",
        SmartTransport.AtaSmartIoctl => "ATA SMART (driver IOCTL)",
        SmartTransport.AtaPassThrough => "ATA pass-through",
        SmartTransport.Sat => "SCSI-ATA translation (USB / SAS)",
        SmartTransport.NvmeJMicron => "USB-NVMe bridge (JMicron)",
        SmartTransport.NvmeASMedia => "USB-NVMe bridge (ASMedia)",
        SmartTransport.NvmeRealtek => "USB-NVMe bridge (Realtek)",
        SmartTransport.NvmeIntelRst => "Intel RST NVMe pass-through",
        _ => "—",
    };

    private static Brush HealthBrush(DiskHealth h) => h switch
    {
        DiskHealth.Good => Ui.B("Success"),
        DiskHealth.Caution => Ui.B("Warning"),
        DiskHealth.Bad => Ui.B("Danger"),
        _ => Ui.B("TextMuted"),
    };

    private static string HealthText(DiskHealth h) => h switch
    {
        DiskHealth.Good => "GOOD",
        DiskHealth.Caution => "CAUTION",
        DiskHealth.Bad => "BAD",
        _ => "UNKNOWN",
    };

    private static Brush TempBrush(DiskReport d)
    {
        if (d.TemperatureC is not { } t)
            return Ui.B("TextMuted");
        var warning = d.WarningTemperatureC ?? 60;
        var critical = d.CriticalTemperatureC ?? 70;
        return t >= critical ? Ui.B("Danger") : t >= warning ? Ui.B("Warning") : t >= warning - 15 ? Ui.B("Accent") : Ui.B("Success");
    }

    private static string Mask(string serial) =>
        serial.Length <= 4 ? new string('•', serial.Length) : serial[..4] + new string('•', Math.Min(12, serial.Length - 4));

    private static StackPanel IconLabel(string icon, string text, Brush foreground) => Ui.Row(
        new LucideIcon { Icon = icon, Width = 15, Height = 15, Foreground = foreground },
        new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
}
