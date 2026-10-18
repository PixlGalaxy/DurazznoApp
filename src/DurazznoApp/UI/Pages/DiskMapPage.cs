using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Durazzno.Core;
using Durazzno.Hardware;
using Durazzno.Hardware.Storage;
using Durazzno.Tools;
using Durazzno.UI.Controls;

namespace Durazzno.UI.Pages;

/// <summary>One row of the DiskMap folder list (public so the XAML data template can bind to it).</summary>
public sealed class DiskMapRow
{
    internal DiskMapRow(MapItem item, long parentSize, bool scanning)
    {
        Item = item;
        var percent = parentSize > 0 ? item.Size * 100.0 / parentSize : 0;
        Name = item.Name;
        SizeText = SystemInfo.FormatBytes(item.Size);
        BarWidth = Math.Max(2, percent * 0.7);

        // Same colour as the item's block on the map.
        var color = new SolidColorBrush(Treemap.ColorFor(item));
        color.Freeze();
        Color = color;

        if (item.Dir is { } dir)
        {
            Icon = "folder";
            Detail = $"{percent:0.0}%  ·  {dir.FileCount:N0} files" + (scanning ? "…" : "") + (dir.Denied ? "  ·  no access" : "");
        }
        else if (item.File is { } file)
        {
            Icon = "file";
            Detail = $"{percent:0.0}%  ·  {FileTypes.Name(file.Category)}";
        }
        else
        {
            Icon = "files";
            Detail = $"{percent:0.0}%  ·  files under {SystemInfo.FormatBytes(SpaceScanner.KeepFileBytes)}";
        }
    }

    internal MapItem Item { get; }
    public string Name { get; }
    public string Detail { get; }
    public string SizeText { get; }
    public string Icon { get; }
    public Brush Color { get; }
    public double BarWidth { get; }
}

/// <summary>
/// DiskMap: what is using the space on a drive or folder, as a sorted list and a treemap. While scanning, the map
/// draws itself live as an animated outline and the list fills in; at the end the colours fade in.
/// </summary>
internal sealed class DiskMapPage : UserControl, IDisposable
{
    private const double MainHeight = 560;
    private static readonly TimeSpan LiveRefresh = TimeSpan.FromMilliseconds(450);
    private const string HoverHint = "Hover the map to see what each block is. Double-click a block to open it.";

    private static readonly DataTemplate RowTemplate = (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:c="clr-namespace:Durazzno.UI.Controls;assembly=DurazznoAppDaemon">
            <Grid Height="40">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="30" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="84" />
                    <ColumnDefinition Width="82" />
                </Grid.ColumnDefinitions>
                <c:LucideIcon Icon="{Binding Icon}" Foreground="{Binding Color}" Width="17" Height="17" HorizontalAlignment="Left" />
                <StackPanel Grid.Column="1" VerticalAlignment="Center">
                    <TextBlock Text="{Binding Name}" Foreground="#F2F2F3" FontSize="13" TextTrimming="CharacterEllipsis" />
                    <TextBlock Text="{Binding Detail}" Foreground="#7A7A84" FontSize="11.5" TextTrimming="CharacterEllipsis" />
                </StackPanel>
                <TextBlock Grid.Column="2" Text="{Binding SizeText}" HorizontalAlignment="Right" VerticalAlignment="Center"
                           Foreground="#F2F2F3" FontSize="13" FontWeight="SemiBold" />
                <Grid Grid.Column="3" Margin="12,0,4,0" VerticalAlignment="Center" Height="5">
                    <Border Background="#24242A" />
                    <Border Background="{Binding Color}" HorizontalAlignment="Left" Width="{Binding BarWidth}" />
                </Grid>
            </Grid>
        </DataTemplate>
        """);

    private readonly AppController _app;
    private readonly ToolDefinition _tool;
    private readonly Brush _accent;
    private readonly TileStrip _drives = new() { Margin = new Thickness(0, 22, 0, 4) };
    private readonly ContentControl _top = new() { Focusable = false };
    private readonly ContentControl _body = new() { Focusable = false };
    private readonly DispatcherTimer _liveTimer = new() { Interval = LiveRefresh };

    // Shared list + map (used both while scanning and after).
    private readonly Treemap _map = new();
    private readonly ListBox _list;
    private readonly TextBlock _hoverInfo = Ui.Text(HoverHint, "CardHint");
    private readonly UIElement _main;

    // Toolbar shown when the scan is done.
    private readonly StackPanel _breadcrumb = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = Ui.Text("", "CardHint");
    private readonly Button _up, _open, _delete, _rescan;
    private readonly UIElement _toolbar;

    // Progress strip shown while scanning (built once and reused).
    private readonly TextBlock _scanSize;
    private readonly TextBlock _scanStats = Ui.Text("", "Body", Ui.B("TextMuted"));
    private readonly TextBlock _scanPath = Ui.Text("", "CardHint", Ui.B("TextDim"));
    private readonly ContentControl _scanBar = new() { Focusable = false, Margin = new Thickness(0, 10, 0, 0) };
    private readonly UIElement _scanStrip;
    private readonly UIElement _idle;
    private readonly Stopwatch _scanWatch = new();

    private SpaceScanner? _scanner;
    private CancellationTokenSource? _scanCts;
    private string? _target;
    private string? _selectedTarget = Path.GetPathRoot(Environment.SystemDirectory);
    private readonly Button _scan;
    private readonly TextBlock _targetLabel = Ui.Text("", "CardHint");
    private long _targetUsedBytes;
    private DirNode? _scanRoot;
    private ScanResult? _result;
    private DirNode? _current;
    private MapItem? _selected;
    private bool _syncing;

    public DiskMapPage(AppController app, ToolDefinition tool)
    {
        _app = app;
        _tool = tool;
        _accent = Imaging.Brush(tool.Accent);

        var title = Ui.Stack(Ui.Text(tool.Name, "PageTitle"), Ui.Text(tool.Tagline, "PageSubtitle"));
        title.Margin = new Thickness(20, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        var choose = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = IconLabel("folder-search", "CHOOSE FOLDER", Ui.B("Text")), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(choose, "CHOOSE FOLDER");
        choose.Click += (_, _) => ChooseFolder();
        _scan = new Button { Style = Ui.Res<Style>("PrimaryButton"), Content = IconLabel("folder-search", "SCAN", Ui.B("AccentText")), Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(_scan, "SCAN");
        _scan.Click += (_, _) => { if (_selectedTarget is not null) StartScan(_selectedTarget); };
        _targetLabel.VerticalAlignment = VerticalAlignment.Center;
        _targetLabel.Margin = new Thickness(0, 0, 14, 0);
        _targetLabel.MaxWidth = 420;
        _targetLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        var headerActions = Ui.Row(_targetLabel, choose, _scan);
        headerActions.HorizontalAlignment = HorizontalAlignment.Right;
        headerActions.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid();
        header.Children.Add(Ui.Row(Ui.IconChip(tool.Icon, _accent, Imaging.Brush(tool.Accent, 0x26), 64, 30), title));
        header.Children.Add(headerActions);

        // List + map ───────────────────────────────────────────────
        _list = new ListBox
        {
            ItemTemplate = RowTemplate,
            ItemContainerStyle = Ui.Res<Style>("FlatListItem"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Height = MainHeight,
            Margin = new Thickness(-10, 10, -10, 0),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        WheelScroll.Attach(_list);
        _list.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _list.SelectedItem is DiskMapRow row)
                Select(row.Item, fromList: true);
        };
        _list.MouseDoubleClick += (_, e) =>
        {
            if (_result is not null && (e.OriginalSource as DependencyObject).FindAncestor<ListBoxItem>() is { DataContext: DiskMapRow { Item.Dir: { } dir } })
                Open(dir);
        };

        _map.Height = MainHeight + 10;
        _map.Margin = new Thickness(0, 12, 0, 0);
        _map.HoverChanged += item => _hoverInfo.Text = item is null
            ? HoverHint
            : $"{item.Path}   ·   {SystemInfo.FormatBytes(item.Size)}   ·   {Percent(item.Size, _scanRoot?.Size ?? 0):0.0}% of scan";
        _map.ItemSelected += item => Select(item, fromList: false);
        _map.FolderOpened += dir => { if (_result is not null) Open(dir); };
        _hoverInfo.Margin = new Thickness(0, 10, 0, 0);
        _hoverInfo.TextTrimming = TextTrimming.CharacterEllipsis;

        var main = Ui.Columns(2, 20);
        main.ColumnDefinitions[0].Width = new GridLength(1.05, GridUnitType.Star);
        main.ColumnDefinitions[2].Width = new GridLength(2, GridUnitType.Star);
        main.Margin = new Thickness(0, 16, 0, 0);
        main.Children.Add(Ui.Card(Ui.Stack(Ui.PanelTitle("CONTENTS"), _list)).At(0));
        main.Children.Add(Ui.Card(Ui.Stack(Ui.PanelTitle("MAP"), _map, _hoverInfo)).At(2));
        _main = main;

        // Done toolbar ─────────────────────────────────────────────
        _up = ToolButton("arrow-up", "UP", (_, _) => { if (_current?.Parent is { } p) Open(p); });
        _open = ToolButton("folder-open", "OPEN IN EXPLORER", (_, _) => OpenInExplorer());
        _delete = ToolButton("trash-2", "DELETE", async (_, _) => await DeleteSelectedAsync());
        _rescan = ToolButton("refresh-cw", "RESCAN", (_, _) => { if (_target is not null) StartScan(_target); });
        var bar = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(_breadcrumb);
        var actions = Ui.Row(_up, _open, _delete, _rescan);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Children.Add(actions);
        _summary.Margin = new Thickness(0, 10, 0, 0);
        _toolbar = Ui.Stack(bar, _summary);

        // Scanning strip ───────────────────────────────────────────
        _scanSize = new TextBlock { FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 40, Foreground = _accent, VerticalAlignment = VerticalAlignment.Center };
        _scanStats.VerticalAlignment = VerticalAlignment.Center;
        _scanStats.Margin = new Thickness(20, 6, 0, 0);
        _scanPath.TextTrimming = TextTrimming.CharacterEllipsis;
        _scanPath.Margin = new Thickness(0, 8, 0, 0);
        var cancel = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = IconLabel("x", "CANCEL", Ui.B("Text")), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(cancel, "CANCEL");
        cancel.Click += (_, _) => CancelScan();
        var stripTop = new Grid();
        stripTop.Children.Add(Ui.Row(_scanSize, _scanStats));
        stripTop.Children.Add(cancel);
        var strip = Ui.Card(Ui.Stack(stripTop, _scanBar, _scanPath), new Thickness(24, 16, 24, 16));
        strip.Margin = new Thickness(0, 6, 0, 0);
        _scanStrip = strip;

        var idleText = Ui.Text("Select a drive above or choose a folder, then press SCAN to see what is using the space.", "Body", Ui.B("TextMuted"));
        idleText.Margin = new Thickness(0, 12, 0, 0);
        var idle = Ui.Card(Ui.Stack(Ui.PanelTitle("NOTHING SCANNED YET"), idleText));
        idle.Margin = new Thickness(0, 6, 0, 0);
        _idle = idle;

        Content = Ui.Stack(header, _drives, _top, _body);

        _liveTimer.Tick += (_, _) => LiveTick();
        RenderDrives();
        ShowIdle();
        UpdateScanButton();
    }

    public void Dispose()
    {
        _scanCts?.Cancel();
        _liveTimer.Stop();
    }

    // ── Drives / target ───────────────────────────────────────────────────────────────────

    private void RenderDrives() => _drives.SetItems(SystemInfo.Drives().Select(DriveTile));

    private UIElement DriveTile(DriveSnapshot d)
    {
        var selected = _selectedTarget is not null && string.Equals(_selectedTarget, d.Letter + "\\", StringComparison.OrdinalIgnoreCase);
        var letter = new TextBlock { Text = d.Letter, FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 26, Foreground = selected ? Ui.B("Accent") : Ui.B("Text") };
        var label = Ui.Text(d.Label, "CardLabel", Ui.B("Text"));
        label.Margin = new Thickness(12, 0, 0, 2);
        label.VerticalAlignment = VerticalAlignment.Bottom;
        label.MaxWidth = 140;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        var size = Ui.Text($"{SystemInfo.FormatBytes(d.Used)} used of {SystemInfo.FormatBytes(d.Total)}", "CardHint");
        size.Margin = new Thickness(0, 6, 0, 8);

        var card = new ChamferBorder
        {
            Width = 240,
            Cut = 12,
            Padding = new Thickness(18, 14, 18, 16),
            Margin = new Thickness(0, 0, 16, 16),
            Background = selected ? Ui.B("SelectedBg") : Ui.B("PanelBg"),
            BorderBrush = selected ? Ui.B("Accent") : Ui.B("PanelBorder"),
            Child = Ui.Stack(Ui.Row(letter, label), size, Ui.UsageBar(d.UsedPercent, _accent, 4)),
        };

        // A real button (keyboard + accessibility) that just shows the card.
        var button = new Button
        {
            Content = card,
            Cursor = Cursors.Hand,
            ToolTip = $"Select {d.Letter}",
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, $"Select {d.Letter}");
        button.Click += (_, _) => SelectTarget(d.Letter + "\\");
        return button;
    }

    /// <summary>Picks what the SCAN button will scan (a drive tile or a chosen folder).</summary>
    private void SelectTarget(string target)
    {
        _selectedTarget = target;
        RenderDrives();
        UpdateScanButton();
    }

    private void UpdateScanButton()
    {
        var isDrive = _selectedTarget is not null && string.Equals(Path.GetPathRoot(_selectedTarget), _selectedTarget, StringComparison.OrdinalIgnoreCase);
        _targetLabel.Text = _selectedTarget is null ? "" : isDrive ? "" : _selectedTarget;
        _scan.IsEnabled = _selectedTarget is not null && !_liveTimer.IsEnabled; // disabled while a scan runs
    }

    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to scan" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            SelectTarget(dialog.FolderName);
    }

    // ── Scanning ──────────────────────────────────────────────────────────────────────────

    private async void StartScan(string target)
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var scanner = _scanner = new SpaceScanner();

        _target = target;
        _targetUsedBytes = 0;
        try
        {
            if (Path.GetPathRoot(target) is { } root && string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
            {
                var drive = new DriveInfo(root);
                _targetUsedBytes = drive.TotalSize - drive.TotalFreeSpace;
            }
        }
        catch
        {
            // Not a drive root or not ready: progress shows counts only.
        }

        // Drop the previous tree before building the new one.
        _result = null;
        _selected = null;
        _map.Root = null;
        _map.Selected = null;
        _list.ItemsSource = null;
        RenderDrives();

        _scanWatch.Restart();
        _top.Content = _scanStrip;
        _body.Content = _main;
        _map.Live = true;
        _scan.IsEnabled = false;

        var scanTask = scanner.ScanAsync(target, cts.Token);
        _scanRoot = scanner.Root;
        _current = _scanRoot;
        _map.Root = _scanRoot;
        UpdateProgress();
        _liveTimer.Start();

        try
        {
            var result = await scanTask;
            if (cts.IsCancellationRequested)
                return;

            _liveTimer.Stop();
            _result = result;
            _scanRoot = result.Root;
            Log.Info($"DiskMap: scanned {target} in {result.Duration.TotalSeconds:0.0}s ({result.Root.FileCount:N0} files)");

            _summary.Text = $"{SystemInfo.FormatBytes(result.Root.Size)} in {result.Root.FileCount:N0} files and {result.Root.DirCount:N0} folders  ·  scanned in {result.Duration.TotalSeconds:0.0} s"
                + (result.DeniedFolders > 0 ? $"  ·  {result.DeniedFolders:N0} folders skipped (no access)" : "");
            _top.Content = _toolbar;
            _map.Live = false; // Full detail; the colours fade in over the outlines.
            _scan.IsEnabled = true;
            Open(result.Root);
        }
        catch (OperationCanceledException)
        {
            if (_scanCts == cts)
                ShowIdle();
        }
        catch (Exception ex)
        {
            Log.Error("DiskMap scan failed", ex);
            _liveTimer.Stop();
            _scan.IsEnabled = true;
            _top.Content = null;
            _body.Content = Ui.Card(Ui.Text($"Could not scan {target}: {ex.Message}", "Body"));
        }
    }

    private void CancelScan()
    {
        _scanCts?.Cancel();
        _liveTimer.Stop();
        _map.Live = false;
        _target = null;
        RenderDrives();
        ShowIdle();
    }

    private void ShowIdle()
    {
        _liveTimer.Stop();
        if (_scan is not null)
            _scan.IsEnabled = _selectedTarget is not null;
        _map.Root = null;
        _list.ItemsSource = null;
        _top.Content = null;
        _body.Content = _idle;
    }

    /// <summary>Every ~0.45 s while scanning: counters, the folder list and a new (animated) map layout.</summary>
    private void LiveTick()
    {
        UpdateProgress();
        if (_scanRoot is null)
            return;

        _map.Relayout();
        RefreshList(scanning: true);
    }

    private void UpdateProgress()
    {
        if (_scanner is not { } s)
            return;

        var bytes = s.Bytes;
        _scanSize.Text = SystemInfo.FormatBytes(bytes);
        _scanStats.Text = $"scanning {_target}   ·   {s.Files:N0} files  ·  {s.Dirs:N0} folders  ·  {_scanWatch.Elapsed.TotalSeconds:0.0} s"
            + (s.DeniedFolders > 0 ? $"  ·  {s.DeniedFolders:N0} without access" : "");
        _scanPath.Text = s.CurrentPath;
        _scanBar.Content = Ui.UsageBar(_targetUsedBytes > 0 ? Math.Min(99, bytes * 100.0 / _targetUsedBytes) : 0, _accent, 5);
    }

    // ── Result ────────────────────────────────────────────────────────────────────────────

    private void Open(DirNode dir)
    {
        _current = dir;
        _selected = null;
        _map.Root = dir;
        _map.Selected = null;
        RefreshList(scanning: false);
        RenderBreadcrumb();
        UpdateButtons();
    }

    private void RefreshList(bool scanning)
    {
        if (_current is not { } current)
            return;

        var size = current.Size;
        var selectedKey = (_list.SelectedItem as DiskMapRow)?.Item.Key;
        _syncing = true;
        var rows = Treemap.Children(current).Select(i => new DiskMapRow(i, size, scanning)).ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = selectedKey is null ? null : rows.FirstOrDefault(r => r.Item.Key.Equals(selectedKey));
        _syncing = false;
    }

    private void Select(MapItem item, bool fromList)
    {
        _selected = item;
        if (fromList)
        {
            _map.Selected = item;
        }
        else
        {
            // A tile deep inside a sub-folder selects the matching row of the current folder when there is one.
            _syncing = true;
            var rows = (IList<DiskMapRow>?)_list.ItemsSource ?? [];
            var row = rows.FirstOrDefault(r => r.Item.Key.Equals(item.Key))
                ?? rows.FirstOrDefault(r => r.Item.Dir is { } d && (d == item.Owner || d.IsAncestorOf(item.Owner)));
            _list.SelectedItem = row;
            if (row is not null)
                _list.ScrollIntoView(row);
            _syncing = false;
        }

        UpdateButtons();
    }

    private void RenderBreadcrumb()
    {
        _breadcrumb.Children.Clear();
        var chain = new List<DirNode>();
        for (var n = _current; n is not null; n = n.Parent)
            chain.Insert(0, n);

        for (var i = 0; i < chain.Count; i++)
        {
            var node = chain[i];
            var last = i == chain.Count - 1;
            if (i > 0)
                _breadcrumb.Children.Add(new LucideIcon { Icon = "chevron-right", Width = 14, Height = 14, Foreground = Ui.B("TextDim"), Margin = new Thickness(2, 0, 2, 0) });

            var text = new TextBlock
            {
                Text = i == 0 ? node.Name.TrimEnd('\\') : node.Name,
                FontFamily = Ui.Res<FontFamily>("HeadingFont"),
                FontWeight = last ? FontWeights.Bold : FontWeights.SemiBold,
                FontSize = 17,
                Foreground = last ? Ui.B("Text") : Ui.B("TextMuted"),
                MaxWidth = 220,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var button = new Button { Style = Ui.Res<Style>("TileButton"), Content = text, Padding = new Thickness(6, 4, 6, 4) };
            button.Click += (_, _) => Open(node);
            _breadcrumb.Children.Add(button);
        }

        if (_current is { } c)
        {
            var size = Ui.Text($"  {SystemInfo.FormatBytes(c.Size)}", "CardLabel", _accent);
            size.VerticalAlignment = VerticalAlignment.Center;
            _breadcrumb.Children.Add(size);
        }
    }

    private void UpdateButtons()
    {
        _up.IsEnabled = _current?.Parent is not null;
        _open.IsEnabled = _current is not null;
        _delete.IsEnabled = _selected is { IsSmallFiles: false } s && !IsProtected(s.Path);
        _rescan.IsEnabled = _target is not null;
    }

    private void OpenInExplorer()
    {
        var path = _selected?.Path ?? _current?.FullPath;
        if (path is null)
            return;

        try
        {
            var args = _selected is { IsSmallFiles: false } ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_selected is not { IsSmallFiles: false } item || _current is null || IsProtected(item.Path))
            return;

        var what = item.Dir is not null ? "folder" : "file";
        if (!DurazznoDialog.Confirm(Window.GetWindow(this), $"Delete this {what}?",
                "It goes to the Recycle Bin, so you can still restore it from there.",
                "Move to Recycle Bin", icon: "trash-2", tone: "Danger", destructive: true,
                bullets: [item.Path, SystemInfo.FormatBytes(item.Size)]))
            return;

        _delete.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                var recycle = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
                var ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
                if (item.Dir is not null)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.Path, ui, recycle);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path, ui, recycle);
            });

            SpaceScanner.Remove(item.Owner, item.Dir, item.File);
            Log.Info($"DiskMap: recycled {item.Path}");
            _selected = null;
            _map.Selected = null;
            _map.Relayout();
            RefreshList(scanning: false);
            RenderBreadcrumb();
        }
        catch (OperationCanceledException)
        {
            // User cancelled the Windows dialog.
        }
        catch (Exception ex)
        {
            Log.Error($"DiskMap delete failed: {item.Path}", ex);
            _app.NotifyTool(_tool, $"Could not delete: {ex.Message}", System.Windows.Forms.ToolTipIcon.Error);
        }
        finally
        {
            UpdateButtons();
        }
    }

    /// <summary>The scan root and Windows' own top-level folders cannot be deleted from here.</summary>
    private bool IsProtected(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        string[] guarded =
        [
            _target?.TrimEnd('\\') ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ];
        return guarded.Any(g => g.Length > 0 && string.Equals(full, g.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            || Path.GetPathRoot(full)?.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase) == true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    private static double Percent(long part, long whole) => whole > 0 ? part * 100.0 / whole : 0;

    private static Button ToolButton(string icon, string text, RoutedEventHandler click)
    {
        var b = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = IconLabel(icon, text, Ui.B("Text")), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(b, text);
        b.Click += click;
        return b;
    }

    private static StackPanel IconLabel(string icon, string text, Brush foreground) => Ui.Row(
        new LucideIcon { Icon = icon, Width = 15, Height = 15, Foreground = foreground },
        new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
}

internal static class VisualTreeExtensions
{
    public static T? FindAncestor<T>(this DependencyObject? node) where T : DependencyObject
    {
        while (node is not null && node is not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }
}
