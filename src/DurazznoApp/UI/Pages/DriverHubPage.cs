using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Durazzno.Core;
using Durazzno.Hardware;
using Durazzno.Hardware.Drivers;
using Durazzno.Tools;
using Durazzno.UI.Controls;

namespace Durazzno.UI.Pages;

/// <summary>Install progress of one update, as shown on its row.</summary>
internal sealed record RowProgress(string Phase, double? Percent, string? Message);

/// <summary>One row of the DriverHub list (public so the XAML data template can bind to it).</summary>
public sealed class DriverRow : INotifyPropertyChanged
{
    /// <summary>A row for something that is not a device (driver package, backup).</summary>
    internal DriverRow(object item, string name, string sub, string icon, string iconColor, string status, string statusColor)
    {
        Item = item;
        Name = name;
        Sub = sub;
        Icon = icon;
        IconBrush = Imaging.Brush(iconColor);
        StatusText = status;
        StatusBrush = Imaging.Brush(statusColor);
        StatusBackground = Imaging.Brush(statusColor, 0x22);
    }

    internal DriverRow(DeviceInfo d, (string Text, string Color)? status = null, string? sub = null)
    {
        Device = d;
        Item = d;
        Name = d.Name;
        var parts = new List<string> { d.ClassName };
        if (!string.IsNullOrWhiteSpace(d.DriverProvider)) parts.Add(d.DriverProvider!);
        if (!string.IsNullOrWhiteSpace(d.DriverVersion)) parts.Add($"v{d.DriverVersion}");
        if (d.DriverDate is { } date) parts.Add(date.ToString("yyyy-MM-dd"));
        Sub = string.Join("  ·  ", parts);
        (Icon, var iconColor) = DriverHubPage.ClassIcon(d.ClassName);
        IconBrush = Imaging.Brush(iconColor);

        if (!d.IsPresent)
            Sub = $"{d.ClassName}  ·  " + (d.LastSeen is { } seen ? $"last connected {seen:yyyy-MM-dd}" : "not connected");

        (StatusText, var color) = !d.IsPresent ? ("UNUSED", "#A78BFA")
            : d.Update is not null ? ("UPDATE", "#38BDF8")
            : d.MissingDriver ? ("NO DRIVER", "#EF4444")
            : d.IsDisabled ? ("DISABLED", "#94A3B8")
            : d.HasProblem ? ($"ERROR {d.ProblemCode}", "#EF4444")
            : d.IsOld ? ("OLD", "#F97316")
            : ("OK", "#22C55E");
        if (status is { } st)
            (StatusText, color) = st;
        if (sub is not null)
            Sub = sub;
        StatusBrush = Imaging.Brush(color);
        StatusBackground = Imaging.Brush(color, 0x22);
    }

    internal DeviceInfo? Device { get; }
    internal object Item { get; }
    public string Name { get; }
    public string Sub { get; }
    public string Icon { get; }
    public Brush IconBrush { get; }
    public string StatusText { get; }
    public Brush StatusBrush { get; }
    public Brush StatusBackground { get; }

    // Install progress bar (left of the status badge): green downloading, blue installing, orange error.
    public Visibility ProgressVisibility { get; private set; } = Visibility.Collapsed;
    public double ProgressValue { get; private set; }
    public string ProgressText { get; private set; } = "";
    public string? ProgressMessage { get; private set; }
    public Brush ProgressBrush { get; private set; } = Brushes.Transparent;
    public Brush ProgressTrack { get; private set; } = Brushes.Transparent;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Apply(RowProgress? p)
    {
        if (p is null)
        {
            ProgressVisibility = Visibility.Collapsed;
        }
        else
        {
            var (color, value, text) = p.Phase switch
            {
                "downloading" => ("#22C55E", p.Percent ?? 0, p.Percent is { } pct ? $"DOWNLOADING {pct:0}%" : "DOWNLOADING…"),
                "downloaded" => ("#22C55E", 100.0, "READY"),
                "installing" => ("#38BDF8", 100.0, "INSTALLING…"),
                "done" => ("#22C55E", 100.0, "INSTALLED"),
                "error" => ("#F97316", 100.0, "ERROR"),
                "rolledback" => ("#F97316", 100.0, "ROLLED BACK"),
                "cancelled" => ("#94A3B8", 100.0, "CANCELLED"),
                _ => ("#94A3B8", 0.0, "QUEUED"),
            };
            ProgressVisibility = Visibility.Visible;
            ProgressValue = value;
            ProgressText = text;
            ProgressMessage = p.Message;
            ProgressBrush = Imaging.Brush(color);
            ProgressTrack = Imaging.Brush(color, 0x33);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); // every property
    }

    public override string ToString() => Name;
}

/// <summary>
/// DriverHub: every device and its driver, problem detection, and driver updates from Windows Update plus the
/// Microsoft Update Catalog (which lists the newer versions Windows Update holds back).
/// </summary>
internal sealed class DriverHubPage : UserControl, IDisposable
{
    private const double MainHeight = 560;
    private enum Filter { All, Updates, Problems, Old, Unused, History, Cleanup, Backups, Hidden }

    private static readonly DataTemplate RowTemplate = (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:c="clr-namespace:Durazzno.UI.Controls;assembly=DurazznoApp">
            <Grid Height="46">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="32" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="104" />
                </Grid.ColumnDefinitions>
                <c:LucideIcon Icon="{Binding Icon}" Foreground="{Binding IconBrush}" Width="17" Height="17" HorizontalAlignment="Left" />
                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,12,0">
                    <TextBlock Text="{Binding Name}" Foreground="#F2F2F3" FontSize="13" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                    <TextBlock Text="{Binding Sub}" Foreground="#7A7A84" FontSize="11.5" TextTrimming="CharacterEllipsis" />
                </StackPanel>
                <Grid Grid.Column="2" Width="168" Height="22" Margin="0,0,10,0" VerticalAlignment="Center"
                      Visibility="{Binding ProgressVisibility}" ToolTip="{Binding ProgressMessage}">
                    <ProgressBar Minimum="0" Maximum="100" Value="{Binding ProgressValue, Mode=OneWay}" Foreground="{Binding ProgressBrush}"
                                 Background="{Binding ProgressTrack}" BorderThickness="0">
                        <ProgressBar.Template>
                            <ControlTemplate TargetType="ProgressBar">
                                <Grid>
                                    <Border x:Name="PART_Track" Background="{TemplateBinding Background}" />
                                    <Border x:Name="PART_Indicator" Background="{TemplateBinding Foreground}" HorizontalAlignment="Left" />
                                </Grid>
                            </ControlTemplate>
                        </ProgressBar.Template>
                    </ProgressBar>
                    <TextBlock Text="{Binding ProgressText}" Foreground="White" FontSize="10.5" FontWeight="Bold"
                               HorizontalAlignment="Center" VerticalAlignment="Center" />
                </Grid>
                <Border Grid.Column="3" Background="{Binding StatusBackground}" Padding="8,3" HorizontalAlignment="Right" VerticalAlignment="Center">
                    <TextBlock Text="{Binding StatusText}" Foreground="{Binding StatusBrush}" FontSize="11" FontWeight="Bold" />
                </Border>
            </Grid>
        </DataTemplate>
        """);

    private readonly AppController _app;
    private readonly ToolDefinition _tool;
    private readonly ListBox _list;
    private readonly StackPanel _filters = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private StackPanel _switches = null!;
    private readonly CheckBox _showSystem = new() { Content = "Show system devices", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = Ui.Text("Scanning devices…", "CardHint");
    private readonly TextBlock _status = Ui.Text("", "CardHint", Ui.B("Info"));
    private readonly ContentControl _detail = new() { Focusable = false };
    private readonly ContentControl _listTitle = Ui.PanelTitle("DEVICES");
    private readonly Button _check, _installAll, _backup, _cancel, _removeAll;
    private string? _jobFile; // the running install, for Cancel
    private string? _installingId; // the update installing right now (the status line follows it)
    private readonly TextBox _search = new() { Width = 240, Tag = "Search devices" };

    private List<DeviceInfo> _devices = [];
    private object? _selectedItem;                                 // package / backup selected in Cleanup / Backups
    private Dictionary<string, List<DriverVersionEntry>>? _history; // device ID → drivers in the store
    private List<DriverPackage>? _packages;
    private List<DriverBackup>? _backups;
    private readonly Dictionary<string, DriverUpdate> _hidden = new(StringComparer.OrdinalIgnoreCase); // device ID → hidden offer
    private readonly HashSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase);                // update IDs skipped until next check
    private readonly Dictionary<string, PendingDriver> _pending = new(StringComparer.OrdinalIgnoreCase); // device ID → installed, waiting for a restart
    private List<PendingDriver> _pendingList = [];
    private readonly Border _restartBanner = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14, 10, 10, 10) };
    private readonly TextBlock _restartText = Ui.Text("", "Body");
    private List<DriverUpdate>? _updates;
    private Filter _filter = Filter.All;
    private DeviceInfo? _selected;
    private bool _busy;
    private bool _checking; // Check for updates is running
    private double _actionsWidth;

    /// <summary>Install progress per update ID (kept until the next check, so errors stay visible).</summary>
    private readonly Dictionary<string, RowProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly CheckBox _notify = new() { Content = "Notify about new drivers", VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _interval = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

    public DriverHubPage(AppController app, ToolDefinition tool)
    {
        _app = app;
        _tool = tool;
        var accent = Imaging.Brush(tool.Accent);

        var title = Ui.Stack(Ui.Text(tool.Name, "PageTitle"), Ui.Text("Your devices, their drivers, and updates from Windows Update, the Microsoft Update Catalog, Intel, NVIDIA and AMD", "PageSubtitle"));
        title.Margin = new Thickness(20, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;

        _search.Style = Ui.Res<Style>("SearchBox");
        _search.VerticalAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => RenderList();
        _check = Action("refresh-cw", "CHECK FOR UPDATES", "PrimaryButton", async () => await CheckUpdatesAsync());
        _installAll = Action("download", "INSTALL ALL", "PrimaryButton", async () => await InstallAsync(_devices.Where(d => d.Update is not null).ToList()));
        _installAll.Visibility = Visibility.Collapsed;
        _cancel = Action("x", "CANCEL", "DangerButton", () =>
        {
            if (_jobFile is not null)
            {
                DriverUpdates.RequestCancel(_jobFile);
                _cancel!.IsEnabled = false;
                _status.Text = "Cancelling… the driver being installed right now finishes first.";
            }
            return Task.CompletedTask;
        });
        _cancel.Visibility = Visibility.Collapsed;
        _removeAll = Action("trash-2", "REMOVE ALL", "DangerButton", async () =>
        {
            if (_filter == Filter.Cleanup)
                await DeletePackagesAsync(_packages ?? []);
            else
                await RemoveUnusedAsync(Visible(Filter.Unused).ToList());
        });
        _removeAll.Visibility = Visibility.Collapsed;
        _backup = Action("archive", "BACKUP DRIVERS", "SecondaryButton", async () => await BackupAsync());
        var devmgmt = Action("cpu", "DEVICE MANAGER", "SecondaryButton", () => { Run("devmgmt.msc"); return Task.CompletedTask; });
        var actions = Ui.Row(_search, devmgmt, _removeAll, _installAll, _cancel, _check);

        // BIOS, firmware and board-specific drivers from the PC maker (no public API: link to its support page).
        _ = Task.Run(PcMaker.Detect).ContinueWith(t =>
        {
            if (t.Result is { } maker)
                actions.Children.Insert(1, Action("external-link", $"{maker.Name.ToUpperInvariant()} SUPPORT", "SecondaryButton", () =>
                {
                    Run(maker.SupportUrl);
                    return Task.CompletedTask;
                }));
        }, TaskScheduler.FromCurrentSynchronizationContext());
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Center;

        // Title and buttons share one line when they fit; in a narrower window the buttons move to a second line
        // under the title instead of covering it.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titleRow = Ui.Row(Ui.IconChip(tool.Icon, accent, Imaging.Brush(tool.Accent, 0x26), 64, 52), title);
        header.Children.Add(titleRow);
        header.Children.Add(actions);
        void LayoutHeader()
        {
            titleRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var wide = header.ActualWidth <= 0 || titleRow.DesiredSize.Width + actions.DesiredSize.Width + 24 <= header.ActualWidth;
            Grid.SetRow(actions, wide ? 0 : 1);
            Grid.SetColumn(actions, wide ? 1 : 0);
            Grid.SetColumnSpan(actions, wide ? 1 : 2);
            actions.HorizontalAlignment = wide ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            actions.Margin = wide ? new Thickness(0) : new Thickness(0, 18, 0, 0);
        }
        header.SizeChanged += (_, _) => LayoutHeader();
        actions.LayoutUpdated += (_, _) =>
        {
            // The PC maker button arrives later and widens the row.
            if (Math.Abs(actions.DesiredSize.Width - _actionsWidth) > 0.5)
            {
                _actionsWidth = actions.DesiredSize.Width;
                LayoutHeader();
            }
        };

        _showSystem.Style = Ui.Res<Style>("ToggleSwitch");
        _showSystem.Checked += (_, _) => Render();
        _showSystem.Unchecked += (_, _) => Render();
        // Background check: notify about new drivers every 6 hours / day / week (off by default).
        _notify.Style = Ui.Res<Style>("ToggleSwitch");
        _notify.IsChecked = _app.Settings.Current.DriverCheckNotify;
        _notify.Checked += (_, _) => { _app.Settings.Update(x => x.DriverCheckNotify = true); _interval.Visibility = Visibility.Visible; ReportMinWidth(); };
        _notify.Unchecked += (_, _) => { _app.Settings.Update(x => x.DriverCheckNotify = false); _interval.Visibility = Visibility.Collapsed; ReportMinWidth(); };
        foreach (var (label, hours) in new[] { ("6 h", 6), ("Daily", 24), ("Weekly", 168) })
        {
            var b = new RadioButton { Content = label, GroupName = "dh-interval", Style = Ui.Res<Style>("Segment"), IsChecked = _app.Settings.Current.DriverCheckHours == hours };
            b.Checked += (_, _) => _app.Settings.Update(x => x.DriverCheckHours = hours);
            _interval.Children.Add(b);
        }
        _interval.Visibility = _notify.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _showSystem.Margin = new Thickness(28, 0, 0, 0);
        var switches = _switches = Ui.Row(_notify, _interval, _showSystem);
        switches.HorizontalAlignment = HorizontalAlignment.Right;
        switches.VerticalAlignment = VerticalAlignment.Center;

        // Filters and switches stay on one line: the window's minimum width follows them (see ReportMinWidth).
        var filterRow = new Grid { Margin = new Thickness(0, 24, 0, 0) };
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        switches.Margin = new Thickness(48, 0, 0, 0);
        switches.VerticalAlignment = VerticalAlignment.Top;
        filterRow.Children.Add(_filters);
        Grid.SetColumn(switches, 1);
        filterRow.Children.Add(switches);
        _summary.Margin = new Thickness(0, 8, 0, 0);
        _status.Margin = new Thickness(0, 4, 0, 0);

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
            if (_list.SelectedItem is DriverRow row)
            {
                _selected = row.Device;
                _selectedItem = row.Device is null ? row.Item : null;
                RenderDetail();
            }
        };
        _list.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is not ListBoxItem { DataContext: DriverRow row } item)
                return;
            item.IsSelected = true;
            CardMenu.Show(_list, MenuFor(row));
            e.Handled = true;
        };

        var main = Ui.Columns(2, 20);
        main.ColumnDefinitions[0].Width = new GridLength(1.6, GridUnitType.Star);
        main.Margin = new Thickness(0, 16, 0, 0);
        main.Children.Add(Ui.Card(Ui.Stack(_listTitle, _list)).At(0));
        main.Children.Add(Ui.Card(_detail).At(2));

        // "Restart Windows to finish N drivers": installs that wait for a restart (PendingRestart).
        _restartBanner.Background = Ui.B("WarningChip");
        _restartText.Foreground = Ui.B("Warning");
        _restartText.TextWrapping = TextWrapping.Wrap;
        _restartText.VerticalAlignment = VerticalAlignment.Center;
        var restartNow = Action("power", "RESTART NOW", "SecondaryButton", () => { RestartWindows(); return Task.CompletedTask; });
        restartNow.Margin = new Thickness(16, 0, 0, 0);
        var bannerGrid = new Grid();
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bannerGrid.Children.Add(_restartText);
        Grid.SetColumn(restartNow, 1);
        bannerGrid.Children.Add(restartNow);
        _restartBanner.Child = bannerGrid;

        Content = Ui.Stack(header, filterRow, _summary, _status, _restartBanner, main);
        Loaded += (_, _) => ReportMinWidth();

        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && _devices.Count == 0 && !_busy)
                await ScanAsync();
        };
        RenderDetail();
    }

    public void Dispose()
    {
    }

    // ── Data ──────────────────────────────────────────────────────────────────────────────

    private async Task ScanAsync()
    {
        _busy = true;
        try
        {
            var watch = Stopwatch.StartNew();
            _devices = await Task.Run(DeviceScanner.Scan);
            if (_updates is not null)
                DriverUpdates.Match(_devices, _updates);
            ApplyHidden();
            _history = null; // driver versions change after installs and rollbacks
            Log.Info($"DriverHub: {_devices.Count} devices in {watch.Elapsed.TotalSeconds:0.0}s");
            _selected = _selected is null ? null : _devices.FirstOrDefault(d => d.DeviceId == _selected.DeviceId);
        }
        catch (Exception ex)
        {
            Log.Error("DriverHub scan failed", ex);
            _status.Text = $"Could not read devices: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }

        Render();
    }

    private async Task CheckUpdatesAsync()
    {
        if (_busy)
            return;

        _busy = true;
        _checking = true;
        _check.IsEnabled = false;
        _progress.Clear();
        _skipped.Clear(); // "skip until next check" ends here
        RenderList();
        _status.Foreground = Ui.B("Info");
        try
        {
            var finished = false; // Progress<T> callbacks are posted, so late ones must not overwrite the final message
            var catalogProgress = (Done: 0, Total: 0, Device: (string?)null);
            var windowsUpdateDone = false;
            void Report()
            {
                if (finished)
                    return;
                // The percentage is the catalog's device-by-device lookup; Windows Update and the makers run alongside.
                var catalogDone = catalogProgress.Total > 0 && catalogProgress.Done >= catalogProgress.Total;
                _status.Text = catalogDone && !windowsUpdateDone ? "Checking for driver updates…  ·  waiting for Windows Update"
                    : catalogProgress.Total == 0 ? "Checking for driver updates…"
                    : $"Checking for driver updates… {catalogProgress.Done * 100 / catalogProgress.Total}%"
                      + (catalogProgress.Device is { } device && !catalogDone ? $"  ·  Microsoft Update Catalog: {device}" : "");
            }
            Report();

            var result = await DriverSearch.RunAsync(_devices, new Progress<(int Done, int Total, string? Device)>(p => { catalogProgress = p; Report(); }),
                () => Dispatcher.BeginInvoke(() => { windowsUpdateDone = true; Report(); }), CancellationToken.None);
            finished = true;
            _updates = result.Offers;
            ApplyHidden();
            var withUpdate = _devices.Where(d => d.Update is not null).ToList();
            _app.DriverWatch.MarkSeen(withUpdate.Select(d => d.Update!));

            _status.Text = (withUpdate.Count == 0
                    ? "Your drivers are up to date."
                    : withUpdate.Count == 1 ? "1 driver with a new update." : $"{withUpdate.Count} drivers with new updates.")
                + (result.WindowsUpdateError is null ? "" : $"  Windows Update failed: {result.WindowsUpdateError}.")
                + (result.CatalogError is null ? "" : $"  Catalog search failed: {result.CatalogError}.")
                + (result.MakersError is null ? "" : $"  Intel / NVIDIA / AMD lookup failed: {result.MakersError}.")
                + $"  Checked at {DateTime.Now:HH:mm}.";
            if (result.HasErrors)
                _status.Foreground = Ui.B("Warning");
            if (withUpdate.Count > 0)
                _filter = Filter.Updates;
        }
        catch (Exception ex)
        {
            Log.Error("DriverHub update check failed", ex);
            _status.Foreground = Ui.B("Danger");
            _status.Text = $"Update check failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            _checking = false;
            _check.IsEnabled = true;
        }

        Render();
    }

    /// <summary>
    /// Install order: chipset and system first, then everything else, graphics, and network last (a network driver
    /// drops the connection while it installs, so nothing may still be downloading by then).
    /// </summary>
    private static int InstallStep(DeviceInfo d) => d.ClassName.ToUpperInvariant() switch
    {
        "SYSTEM" or "FIRMWARE" or "HDC" or "SCSIADAPTER" or "SECURITYDEVICES" or "COMPUTER" => 0,
        "DISPLAY" => 2,
        "NET" => 3,
        _ => 1,
    };

    private async Task InstallAsync(List<DeviceInfo> devices)
    {
        var updates = devices.Where(d => d.Update is not null)
            .OrderBy(InstallStep).ThenByDescending(d => d.HasProblem).ThenBy(d => d.ClassName).ThenBy(d => d.Name) // same order as the list
            .Select(d => d.Update!).DistinctBy(u => u.UpdateId).ToList();
        if (updates.Count == 0 || _busy)
            return;

        var catalog = updates.Where(u => u.Source == DriverSource.Catalog).ToList();
        var vendor = updates.Where(u => u.IsManufacturer).ToList();
        var wuIds = updates.Where(u => u.Source == DriverSource.WindowsUpdate).Select(u => u.UpdateId).ToList();
        var size = catalog.Sum(u => u.DownloadBytes);
        var vendorSize = vendor.Sum(u => u.DownloadBytes);
        var lines = new List<string>();
        if (wuIds.Count > 0) lines.Add($"{wuIds.Count} from Windows Update");
        if (catalog.Count > 0) lines.Add($"{catalog.Count} from the Microsoft Update Catalog  ·  {SystemInfo.FormatBytes(size)} to download");
        if (vendor.Count > 0) lines.Add($"{vendor.Count} from the hardware makers  ·  {SystemInfo.FormatBytes(vendorSize)} to download");
        if (vendor.Any(u => u.Interactive)) lines.Add("Some makers do not allow silent installs: their installer window opens, follow its steps.");
        if (!SystemRestore.IsOn) lines.Add("System Restore is off: the current drivers are backed up instead (Backups tab).");
        if (!DurazznoDialog.Confirm(Window.GetWindow(this), updates.Count == 1 ? "Install 1 driver update?" : $"Install {updates.Count} driver updates?",
                "They are downloaded and installed one after another, after a system restore point (or, with System Restore off, a backup of the current drivers), so you can go back if something goes wrong.",
                "Install", icon: "download", tone: "Accent", bullets: lines))
            return;

        _busy = true;
        _check.IsEnabled = false;
        _installAll.IsEnabled = false;
        _status.Foreground = Ui.B("Info");
        _progress.Clear();
        foreach (var u in updates)
            _progress[u.UpdateId] = new RowProgress("queued", null, null);
        RenderList();
        RenderDetail();

        var id = Guid.NewGuid().ToString("N")[..8];
        var jobFile = Path.Combine(Path.GetTempPath(), $"DurazznoDrivers-{id}.job.json");
        _jobFile = jobFile;
        _installAll.Visibility = Visibility.Collapsed;
        _cancel.Visibility = Visibility.Visible;
        _cancel.IsEnabled = true;
        var progressFile = Path.Combine(Path.GetTempPath(), $"DurazznoDrivers-{id}.progress.jsonl");
        string? restoreNote = null;
        var declined = false;
        try
        {
            using var process = DriverUpdates.StartInstall(new InstallJob(updates, DriverBackups.NewFolder() + " before updates"), jobFile, progressFile);
            if (process is null)
            {
                declined = true;
            }
            else
            {
                // The elevated installer appends one JSON line per event; follow the file until it exits.
                await using var stream = new FileStream(progressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var pending = "";
                void Drain()
                {
                    pending += reader.ReadToEnd();
                    int newline;
                    while ((newline = pending.IndexOf('\n')) >= 0)
                    {
                        var line = pending[..newline].Trim();
                        pending = pending[(newline + 1)..];
                        if (DriverUpdates.ParseProgress(line) is not { } p)
                            continue;
                        if (p.Phase == "restorepoint")
                            restoreNote = p.Message;
                        else if (p.Phase == "backup")
                        {
                            restoreNote = restoreNote is null ? p.Message : $"{restoreNote}. {p.Message}";
                            _backups = null; // the Backups tab reloads with the new one
                        }
                        else if (p.UpdateId.Length > 0)
                            OnProgress(p, updates);
                    }
                }
                while (!process.HasExited)
                {
                    Drain();
                    await Task.Delay(250);
                }
                Drain();
            }
        }
        catch (Exception ex)
        {
            Log.Error("DriverHub install", ex);
            foreach (var u in updates.Where(u => _progress[u.UpdateId].Phase is not ("done" or "error" or "rolledback")))
                _progress[u.UpdateId] = new RowProgress("error", 100, ex.Message);
        }
        finally
        {
            _busy = false;
            _check.IsEnabled = true;
            _installAll.IsEnabled = true;
            _jobFile = null;
            _cancel.Visibility = Visibility.Collapsed;
            try { File.Delete(jobFile); } catch { }
            try { File.Delete(progressFile); } catch { }
        }

        if (declined)
        {
            _progress.Clear();
            _status.Text = "Installation cancelled.";
            Render();
            return;
        }

        // Anything the installer never reported on (it crashed) counts as failed.
        foreach (var u in updates.Where(u => _progress[u.UpdateId].Phase is not ("done" or "error" or "rolledback" or "cancelled")))
            _progress[u.UpdateId] = new RowProgress("error", 100, "The installer stopped before finishing this driver.");

        Summarize(updates, restoreNote);

        // Windows takes a moment to report the new driver versions: rescan shortly after, without the installed offers.
        var installed = updates.Where(u => _progress[u.UpdateId].Phase == "done").Select(u => u.UpdateId).ToHashSet();
        _updates?.RemoveAll(u => installed.Contains(u.UpdateId));
        RenderList();
        await Task.Delay(TimeSpan.FromSeconds(4));
        await ScanAsync();
    }

    private void OnProgress(InstallProgress p, List<DriverUpdate> batch)
    {
        var state = new RowProgress(p.Phase, p.Percent, p.Message);
        _progress[p.UpdateId] = state;
        if (p.RebootRequired)
            _rebootRequested.Add(p.UpdateId);
        if (p is { Phase: "done", RebootRequired: true } && batch.FirstOrDefault(u => u.UpdateId == p.UpdateId) is { } waiting)
        {
            PendingRestart.Add(waiting);
            _pendingList = PendingRestart.List();
            RenderRestartBanner();
        }
        // Installed but broke the device (the installer already put the previous driver back): never offer it again.
        if (p.Phase == "rolledback" && !(_app.Settings.Current.HiddenDriverUpdates ?? []).Contains(p.UpdateId, StringComparer.OrdinalIgnoreCase))
            _app.Settings.Update(s => s.HiddenDriverUpdates = [.. (s.HiddenDriverUpdates ?? []), p.UpdateId]);
        if (_list.ItemsSource is IEnumerable<DriverRow> rows)
            foreach (var row in rows.Where(r => string.Equals(r.Device?.Update?.UpdateId, p.UpdateId, StringComparison.OrdinalIgnoreCase)))
                row.Apply(state);

        var index = batch.FindIndex(u => u.UpdateId == p.UpdateId) + 1;
        var update = batch[Math.Max(0, index - 1)];
        // Downloads run several at a time: while one driver installs, the status line stays on it.
        if (p.Phase == "installing")
            _installingId = p.UpdateId;
        else if (p.UpdateId == _installingId && p.Phase is "done" or "error" or "rolledback" or "cancelled")
            _installingId = null;
        if (p.Phase is "downloading" or "downloaded" && _installingId is not null)
            return;

        var phase = p.Phase switch
        {
            "downloading" => p.Percent is { } pct ? $"downloading {pct:0}%" : "downloading",
            "downloaded" => "downloaded, waiting its turn",
            "installing" => update.Interactive ? "installing, follow the installer window" : "installing",
            "done" => "installed",
            "error" => $"failed: {p.Message}",
            "rolledback" => "rolled back, doesn't work on this Windows",
            _ => "queued",
        };
        // Cancel stays available: it stops the downloads and the queue; a driver already installing always finishes.
        if (p.Phase is not ("queued" or "cancelled") && !_status.Text.StartsWith("Cancelling", StringComparison.Ordinal))
            _status.Text = $"{index} of {batch.Count}  ·  {DeviceName(update)}  ·  {update.Title}  ·  {phase}";
    }

    /// <summary>One notification for the whole batch (from DriverHub), with names instead of "1 of 1".</summary>
    private void Summarize(List<DriverUpdate> updates, string? restoreNote)
    {
        var ok = updates.Where(u => _progress[u.UpdateId].Phase == "done").ToList();
        var cancelled = updates.Where(u => _progress[u.UpdateId].Phase == "cancelled").ToList();
        var failed = updates.Except(ok).Except(cancelled).ToList();
        var reboot = updates.Any(u => _progress[u.UpdateId] is { Phase: "done" } && WasRebootRequested(u));
        var restart = reboot ? " Restart Windows to finish." : "";

        string title, text;
        if (cancelled.Count > 0)
        {
            title = "Installation cancelled";
            text = (ok.Count > 0 ? $"{ok.Count} of {updates.Count} installed before cancelling." : "Nothing was installed.") + restart;
        }
        else if (updates.Count == 1)
        {
            var u = updates[0];
            (title, text) = ok.Count == 1
                ? ($"{DeviceName(u)} updated", $"{u.Title} is installed.{restart}")
                : ($"{DeviceName(u)}: driver not installed", _progress[u.UpdateId].Message ?? "The installation failed.");
        }
        else if (failed.Count == 0)
        {
            title = $"{ok.Count}/{updates.Count} drivers installed successfully";
            text = Names(ok) + "." + restart;
        }
        else
        {
            title = $"{ok.Count}/{updates.Count} drivers installed";
            var rolledBack = failed.Where(u => _progress[u.UpdateId].Phase == "rolledback").ToList();
            var errors = failed.Except(rolledBack).ToList();
            static string List(List<string> names) => string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" and {names.Count - 3} more" : "");
            text = (rolledBack.Count > 0 ? $"Rolled back (doesn't work on this Windows): {List(rolledBack.Select(DeviceName).ToList())}. " : "")
                + (errors.Count > 0 ? $"Failed: {List(errors.Select(DeviceName).ToList())}. Open DriverHub for the reasons." : "")
                + restart;
        }

        _status.Foreground = failed.Count == 0 ? Ui.B("Success") : Ui.B("Warning");
        _status.Text = $"{title}.  {text}" + (restoreNote is null ? "" : $"  ({restoreNote}.)");
        var tone = cancelled.Count > 0 ? Tray.NotifyTone.Cancelled
            : failed.Count == 0 ? Tray.NotifyTone.Success
            : ok.Count == 0 ? Tray.NotifyTone.Error
            : Tray.NotifyTone.Warning;
        _app.Notify(title, text, failed.Count == 0 ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning, Tray.NotifyLogo.DriverHub, tone: tone);
    }

    private readonly HashSet<string> _rebootRequested = new(StringComparer.OrdinalIgnoreCase);

    private bool WasRebootRequested(DriverUpdate u) => _rebootRequested.Contains(u.UpdateId);

    private string DeviceName(DriverUpdate u) => _devices.FirstOrDefault(d => d.Update?.UpdateId == u.UpdateId)?.Name ?? u.Model;

    private string Names(List<DriverUpdate> list) =>
        string.Join(", ", list.Take(3).Select(DeviceName)) + (list.Count > 3 ? $" and {list.Count - 3} more" : "");

    /// <summary>Removes unused (not connected) devices, like DeviceCleanup: one UAC prompt for the batch.</summary>
    private async Task RemoveUnusedAsync(List<DeviceInfo> devices)
    {
        if (devices.Count == 0 || _busy)
            return;
        var title = devices.Count == 1 ? $"Remove {devices[0].Name}?" : $"Remove {devices.Count} unused devices?";
        if (!DurazznoDialog.Confirm(Window.GetWindow(this), title,
                "Their leftover entries and driver bindings are deleted. A device you connect again is simply installed again.",
                "Remove", icon: "trash-2", tone: "Danger", destructive: true,
                bullets: devices.Count == 1 ? null : devices.Take(5).Select(d => d.Name).Append(devices.Count > 5 ? $"and {devices.Count - 5} more" : null).OfType<string>().ToList()))
            return;

        _busy = true;
        _removeAll.IsEnabled = false;
        _status.Foreground = Ui.B("Info");
        _status.Text = $"Removing {devices.Count} unused device(s)…";
        var results = await DeviceCleanup.RemoveAsync(devices.Select(d => d.DeviceId).ToList());
        _busy = false;
        _removeAll.IsEnabled = true;

        if (results is null)
        {
            _status.Text = "Cancelled.";
            return;
        }
        var ok = results.Count(r => r.Success);
        var failed = results.Where(r => !r.Success).ToList();
        _status.Foreground = failed.Count == 0 ? Ui.B("Success") : Ui.B("Warning");
        _status.Text = failed.Count == 0
            ? $"{ok} unused device(s) removed."
            : $"{ok} of {results.Count} removed. Not removed: " + string.Join(", ", failed.Take(3).Select(r => $"{devices.FirstOrDefault(d => d.DeviceId == r.DeviceId)?.Name} ({r.Message})"));
        _app.Notify(failed.Count == 0 ? $"{ok} unused device{(ok == 1 ? "" : "s")} removed" : $"{ok}/{results.Count} unused devices removed",
            failed.Count == 0 ? "Windows installs them again if you connect them." : _status.Text,
            failed.Count == 0 ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning, Tray.NotifyLogo.DriverHub,
            tone: failed.Count == 0 ? Tray.NotifyTone.Success : ok == 0 ? Tray.NotifyTone.Error : Tray.NotifyTone.Warning);
        _selected = null;
        await ScanAsync();
    }

    private async Task BackupAsync()
    {
        var folder = DriverBackups.NewFolder();
        _backup.IsEnabled = false;
        _status.Foreground = Ui.B("Info");
        _status.Text = "Exporting all third-party drivers…";
        var error = await DriverUpdates.BackupAsync(folder);
        _backup.IsEnabled = true;

        if (error is null)
        {
            _status.Foreground = Ui.B("Success");
            _status.Text = $"Drivers saved to {folder}";
            _backups = await Task.Run(DriverBackups.List);
            _filter = Filter.Backups;
            Render();
        }
        else if (error == "Cancelled")
        {
            _status.Text = "Backup cancelled.";
        }
        else
        {
            _status.Foreground = Ui.B("Danger");
            _status.Text = $"Backup failed: {error}";
        }
    }

    // ── Hide / skip ───────────────────────────────────────────────────────────────────────

    /// <summary>Takes hidden and skipped offers off their devices (hidden ones stay listed under Hidden).</summary>
    private void ApplyHidden()
    {
        _hidden.Clear();
        _pending.Clear();
        _pendingList = PendingRestart.List();
        var hidden = (_app.Settings.Current.HiddenDriverUpdates ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _devices.Where(d => d.Update is not null))
        {
            // Installed and waiting for a restart: the device still reports the old driver until then.
            if (PendingRestart.Find(_pendingList, d.Update!) is { } pending)
            {
                _pending[d.DeviceId] = pending;
                d.Update = null;
            }
            else if (hidden.Contains(d.Update!.UpdateId))
            {
                _hidden[d.DeviceId] = d.Update;
                d.Update = null;
            }
            else if (_skipped.Contains(d.Update.UpdateId))
            {
                d.Update = null;
            }
        }
    }

    private void Hide(DeviceInfo d)
    {
        if (d.Update is not { } u)
            return;
        _app.Settings.Update(s => s.HiddenDriverUpdates = [.. (s.HiddenDriverUpdates ?? []), u.UpdateId]);
        ApplyHidden();
        _status.Text = $"Hidden: {u.Title}. It is listed under Hidden; a newer version is offered again.";
        Render();
    }

    private void Unhide(DeviceInfo d)
    {
        if (!_hidden.TryGetValue(d.DeviceId, out var u))
            return;
        _app.Settings.Update(s => s.HiddenDriverUpdates = (s.HiddenDriverUpdates ?? []).Where(id => !id.Equals(u.UpdateId, StringComparison.OrdinalIgnoreCase)).ToList());
        if (_updates is not null)
            DriverUpdates.Match(_devices, _updates);
        ApplyHidden();
        if (_hidden.Count == 0 && _filter == Filter.Hidden)
            _filter = Filter.Updates;
        Render();
    }

    private void Skip(DeviceInfo d)
    {
        if (d.Update is not { } u)
            return;
        _skipped.Add(u.UpdateId);
        ApplyHidden();
        _status.Text = $"Skipped until the next check: {u.Title}.";
        Render();
    }

    private List<CardMenuItem> MenuFor(DriverRow row)
    {
        if (row.Device is { } d)
        {
            if (d.Update is not null)
                return
                [
                    new("download", "Install update", () => _ = InstallAsync([d]), Enabled: !_busy),
                    new("eye-off", "Hide this update", () => Hide(d)),
                    new("skip-forward", "Skip until next check", () => Skip(d)),
                ];
            if (_hidden.ContainsKey(d.DeviceId))
                return [new("eye", "Unhide update", () => Unhide(d))];
            if (!d.IsPresent)
                return [new("trash-2", "Remove device", () => _ = RemoveUnusedAsync([d]), Danger: true, Enabled: !_busy)];
            return [];
        }
        return row.Item switch
        {
            DriverPackage pkg => [new("trash-2", "Delete package", () => _ = DeletePackagesAsync([pkg]), Danger: true, Enabled: !_busy)],
            DriverBackup b =>
            [
                new("rotate-ccw", "Restore drivers", () => _ = RestoreBackupAsync(b), Enabled: !_busy),
                new("folder-open", "Open folder", () => Run("explorer.exe", $"\"{b.Folder}\"")),
                new("trash-2", "Delete backup", () => DeleteBackup(b), Danger: true),
            ],
            _ => [],
        };
    }

    // ── History / rollback, cleanup, backups ──────────────────────────────────────────────

    private async Task LoadViewAsync(Filter filter)
    {
        if (_busy)
            return;
        _busy = true;
        _status.Foreground = Ui.B("Info");
        try
        {
            switch (filter)
            {
                case Filter.History when _history is null:
                    _status.Text = "Reading driver history…";
                    var devices = _devices.Where(d => d.IsPresent && d.DriverVersion is not null).ToList();
                    _history = await Task.Run(() => devices.ToDictionary(d => d.DeviceId, DriverHistory.ForDevice, StringComparer.OrdinalIgnoreCase));
                    _status.Text = "";
                    break;
                case Filter.Cleanup when _packages is null:
                    _status.Text = "Reading the driver store…";
                    var all = _devices.ToList();
                    _packages = await Task.Run(() => DriverStoreCleanup.FindUnused(all));
                    _status.Text = _packages.Count == 0 ? "No unused driver packages." :
                        $"{_packages.Count} unused driver packages  ·  {SystemInfo.FormatBytes(_packages.Sum(x => x.SizeBytes))} can be freed.";
                    break;
                case Filter.Backups when _backups is null:
                    _backups = await Task.Run(DriverBackups.List);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"DriverHub {filter}", ex);
            _status.Foreground = Ui.B("Danger");
            _status.Text = ex.Message;
        }
        finally
        {
            _busy = false;
        }
        Render();
    }

    private List<DriverVersionEntry> PreviousVersions(DeviceInfo d) =>
        _history is not null && _history.TryGetValue(d.DeviceId, out var all) ? DriverHistory.Previous(all, d) : [];

    private async Task RollbackAsync(DeviceInfo d, DriverVersionEntry entry)
    {
        if (_busy || !DurazznoDialog.Confirm(Window.GetWindow(this), $"Roll back {d.Name}?",
                $"Its driver goes from v{d.DriverVersion} to v{entry.Version}{(entry.Date is { } dt ? $" ({dt:yyyy-MM-dd})" : "")}. A restore point is created first.",
                "Roll back", icon: "rotate-ccw", tone: "Warning"))
            return;
        _busy = true;
        _status.Foreground = Ui.B("Info");
        _status.Text = $"Rolling back {d.Name} to v{entry.Version}…";
        var (error, reboot) = await DriverHistory.RollbackAsync(d, entry);
        _busy = false;
        if (error == "Cancelled")
        {
            _status.Text = "Rollback cancelled.";
            return;
        }
        var ok = error is null;
        _status.Foreground = ok ? Ui.B("Success") : Ui.B("Danger");
        _status.Text = ok ? $"{d.Name} is back on v{entry.Version}.{(reboot ? " Restart Windows to finish." : "")}" : $"Rollback failed: {error}";
        _app.Notify(ok ? $"{d.Name} rolled back" : $"{d.Name}: rollback failed", ok ? $"Driver v{entry.Version} is installed.{(reboot ? " Restart Windows to finish." : "")}" : error!,
            ok ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning, Tray.NotifyLogo.DriverHub,
            tone: ok ? Tray.NotifyTone.Success : Tray.NotifyTone.Error);
        await Task.Delay(2000);
        await ScanAsync();
    }

    private async Task DeletePackagesAsync(List<DriverPackage> packages)
    {
        if (packages.Count == 0 || _busy)
            return;
        var size = SystemInfo.FormatBytes(packages.Sum(x => x.SizeBytes));
        var title = packages.Count == 1 ? $"Delete {packages[0].OriginalName}?" : $"Delete {packages.Count} driver packages?";
        if (!DurazznoDialog.Confirm(Window.GetWindow(this), title,
                $"Frees {size}. Older versions you delete can no longer be rolled back to; a device that needs a deleted driver gets it again from Windows Update.",
                "Delete", icon: "trash-2", tone: "Danger", destructive: true))
            return;
        _busy = true;
        _status.Foreground = Ui.B("Info");
        _status.Text = $"Deleting {packages.Count} driver package(s)…";
        var results = await DriverStoreCleanup.DeleteAsync(packages.Select(x => x.PublishedName).ToList());
        _busy = false;
        if (results is null)
        {
            _status.Text = "Cancelled.";
            return;
        }
        var ok = results.Count(r => r.Success);
        var freed = SystemInfo.FormatBytes(packages.Where(x => results.Any(r => r.Success && r.DeviceId == x.PublishedName)).Sum(x => x.SizeBytes));
        _status.Foreground = ok == results.Count ? Ui.B("Success") : Ui.B("Warning");
        _status.Text = $"{ok} of {results.Count} packages deleted  ·  {freed} freed."
                       + string.Concat(results.Where(r => !r.Success).Take(3).Select(r => $"  {r.DeviceId}: {r.Message}."));
        _app.Notify($"{ok} driver package{(ok == 1 ? "" : "s")} deleted", $"{freed} freed.", System.Windows.Forms.ToolTipIcon.Info, Tray.NotifyLogo.DriverHub,
            tone: ok == results.Count ? Tray.NotifyTone.Success : ok == 0 ? Tray.NotifyTone.Error : Tray.NotifyTone.Warning);
        _packages = null;
        _history = null;
        _selectedItem = null;
        await LoadViewAsync(Filter.Cleanup);
    }

    private async Task RestoreBackupAsync(DriverBackup b)
    {
        if (_busy || !DurazznoDialog.Confirm(Window.GetWindow(this), "Restore drivers?",
                $"Installs the {b.Packages} drivers saved on {b.Created:yyyy-MM-dd HH:mm}. Windows keeps whichever version is newer for each device.",
                "Restore", icon: "rotate-ccw", tone: "Accent"))
            return;
        _busy = true;
        _status.Foreground = Ui.B("Info");
        _status.Text = "Restoring drivers from the backup…";
        var (error, reboot) = await DriverBackups.RestoreAsync(b);
        _busy = false;
        if (error == "Cancelled")
        {
            _status.Text = "Restore cancelled.";
            return;
        }
        _status.Foreground = error is null ? Ui.B("Success") : Ui.B("Danger");
        _status.Text = error is null ? $"Drivers restored from {b.Name}.{(reboot ? " Restart Windows to finish." : "")}" : $"Restore failed: {error}";
        _app.Notify(error is null ? "Drivers restored" : "Restore failed", error ?? $"{b.Packages} drivers from {b.Name}.{(reboot ? " Restart Windows to finish." : "")}",
            error is null ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning, Tray.NotifyLogo.DriverHub,
            tone: error is null ? Tray.NotifyTone.Success : Tray.NotifyTone.Error);
        await ScanAsync();
    }

    private void DeleteBackup(DriverBackup b)
    {
        if (!DurazznoDialog.Confirm(Window.GetWindow(this), "Delete this backup?", $"{b.Name}  ·  {b.Packages} drivers  ·  {SystemInfo.FormatBytes(b.SizeBytes)}",
                "Delete", icon: "trash-2", tone: "Danger", destructive: true))
            return;
        try
        {
            Directory.Delete(b.Folder, true);
        }
        catch (Exception ex)
        {
            _status.Foreground = Ui.B("Danger");
            _status.Text = $"Could not delete the backup: {ex.Message}";
        }
        _backups = DriverBackups.List();
        _selectedItem = null;
        Render();
    }

    // ── Rendering ─────────────────────────────────────────────────────────────────────────

    private IEnumerable<DeviceInfo> Visible(Filter filter)
    {
        var showSystem = _showSystem.IsChecked == true;
        var q = _search.Text.Trim();
        return _devices.Where(d =>
            (showSystem || !d.IsSystemPlumbing || d.HasProblem || d.Update is not null)
            && (filter == Filter.Unused) != d.IsPresent // unused devices only appear under Unused
            && filter switch
            {
                Filter.Updates => d.Update is not null,
                Filter.Problems => d.HasProblem,
                Filter.Old => d.IsOld,
                // Windows' own in-box drivers are left out: their "older versions" are just other Windows builds.
                Filter.History => !(d.DriverProvider?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ?? true) && PreviousVersions(d).Count > 0,
                Filter.Hidden => _hidden.ContainsKey(d.DeviceId),
                Filter.Cleanup or Filter.Backups => false,
                _ => true,
            }
            && (q.Length == 0 || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || d.ClassName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (d.DriverProvider?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)));
    }

    /// <summary>Tells the window how wide the filter row needs to be so it never wraps (filters + switches).</summary>
    private void ReportMinWidth()
    {
        if (_switches is null || Window.GetWindow(this) is not MainWindow window)
            return;
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _filters.Measure(unbounded);
        _switches.Measure(unbounded);
        window.SetPageMinWidth(_filters.DesiredSize.Width + _switches.DesiredSize.Width + _switches.Margin.Left);
    }

    private void Render()
    {
        _filters.Children.Clear();
        var segments = new List<(string Label, Filter Filter, int? Count)>
        {
            ("All", Filter.All, Visible(Filter.All).Count()),
            ("Updates", Filter.Updates, Visible(Filter.Updates).Count()),
            ("Problems", Filter.Problems, Visible(Filter.Problems).Count()),
            ("Old drivers", Filter.Old, Visible(Filter.Old).Count()),
            ("Unused", Filter.Unused, Visible(Filter.Unused).Count()),
            ("History", Filter.History, _history is null ? null : Visible(Filter.History).Count()),
            ("Cleanup", Filter.Cleanup, _packages?.Count),
            ("Backups", Filter.Backups, _backups?.Count),
        };
        if (_hidden.Count > 0)
            segments.Add(("Hidden", Filter.Hidden, _hidden.Count));
        foreach (var (label, filter, count) in segments)
        {
            var b = new RadioButton { Content = count is null ? label : $"{label}  {count}", GroupName = "dh-filter", Style = Ui.Res<Style>("Segment"), IsChecked = _filter == filter };
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            b.Checked += async (_, _) =>
            {
                _filter = filter;
                _selectedItem = null;
                Render();
                if (filter is Filter.History or Filter.Cleanup or Filter.Backups)
                    await LoadViewAsync(filter);
            };
            _filters.Children.Add(b);
        }
        ReportMinWidth();

        var unused = Visible(Filter.Unused).Count();
        var removable = _filter == Filter.Cleanup ? _packages?.Count ?? 0 : _filter == Filter.Unused ? unused : 0;
        _removeAll.Visibility = removable > 0 && !_busy ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)((StackPanel)_removeAll.Content).Children[1]).Text = _filter == Filter.Cleanup ? $"DELETE ALL ({removable})" : $"REMOVE ALL ({removable})";
        var updates = _devices.Count(d => d.Update is not null);
        _installAll.Visibility = updates > 0 ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)((StackPanel)_installAll.Content).Children[1]).Text = $"INSTALL ALL ({updates})";
        RenderList();
        RenderDetail();
    }

    private void RenderList()
    {
        _listTitle.Content = _filter switch { Filter.Cleanup => "DRIVER PACKAGES", Filter.Backups => "BACKUPS", _ => "DEVICES" };
        if (_filter is Filter.Cleanup or Filter.Backups)
        {
            var q = _search.Text.Trim();
            var other = _filter == Filter.Cleanup
                ? (_packages ?? []).Where(x => q.Length == 0 || $"{x.Provider} {x.ClassName} {x.OriginalName}".Contains(q, StringComparison.OrdinalIgnoreCase))
                    .Select(x => new DriverRow(x, $"{x.Provider}  ·  {x.ClassName}", $"{x.OriginalName}  ·  v{x.Version}  ·  {x.Date:yyyy-MM-dd}  ·  {SystemInfo.FormatBytes(x.SizeBytes)}",
                        "package", "#94A3B8", x.IsSuperseded ? "OLD VERSION" : "UNUSED", x.IsSuperseded ? "#F97316" : "#A78BFA")).ToList()
                : (_backups ?? []).Select(b => new DriverRow(b, b.Name, $"{b.Packages} drivers  ·  {SystemInfo.FormatBytes(b.SizeBytes)}  ·  {b.Created:yyyy-MM-dd HH:mm}",
                    "archive", "#38BDF8", "BACKUP", "#38BDF8")).ToList();
            _list.ItemsSource = other;
            _list.SelectedItem = other.FirstOrDefault(r => ReferenceEquals(r.Item, _selectedItem));
            return;
        }

        RenderRestartBanner();
        var rows = Visible(_filter)
            .OrderByDescending(d => d.Update is not null)
            .ThenBy(d => d.Update is not null ? InstallStep(d) : 0) // updates in install order
            .ThenByDescending(d => d.HasProblem)
            .ThenBy(d => d.ClassName)
            .ThenBy(d => d.Name)
            .Select(d =>
            {
                var row = _filter switch
                {
                    Filter.History => new DriverRow(d, ($"{PreviousVersions(d).Count} OLDER", "#2DD4BF")),
                    Filter.Hidden => new DriverRow(d, ("HIDDEN", "#94A3B8"), $"Hidden: {_hidden[d.DeviceId].Title}"),
                    _ => new DriverRow(d),
                };
                if (d.Update is { } u && _progress.TryGetValue(u.UpdateId, out var p))
                    row.Apply(p);
                return row;
            })
            .ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = _selected is null ? null : rows.FirstOrDefault(r => r.Device == _selected);
        if (_filter == Filter.History && _history is null)
            _summary.Text = "Reading driver history…";

        var visible = Visible(Filter.All).ToList();
        _summary.Text = $"{visible.Count} devices  ·  {visible.Count(d => d.HasProblem)} with problems  ·  {visible.Count(d => d.IsOld)} drivers older than 4 years  ·  {Visible(Filter.Unused).Count()} unused"
            + $"  ·  {UpdatesSummary()}"
            + (_showSystem.IsChecked == true ? "" : $"  ·  {_devices.Count(d => d.IsPresent) - visible.Count} system devices hidden");
    }

    private void RenderDetail()
    {
        if (_selectedItem is DriverPackage pkg)
        {
            var panel = DetailHeader(pkg.OriginalName, pkg.IsSuperseded ? "OLD VERSION" : "UNUSED", pkg.IsSuperseded ? "#F97316" : "#A78BFA");
            panel.Children.Add(Note(pkg.IsSuperseded
                ? "An older version of a driver this PC uses. Deleting it frees space, but that device can no longer roll back to it."
                : "Not used by any connected device. If a device that needs it is connected later, Windows gets it again from Windows Update.", "Info"));
            Field(panel, "Provider", pkg.Provider);
            Field(panel, "Class", pkg.ClassName);
            Field(panel, "Version", $"v{pkg.Version}  ·  {pkg.Date:yyyy-MM-dd}");
            Field(panel, "Size", SystemInfo.FormatBytes(pkg.SizeBytes));
            Field(panel, "Published as", pkg.PublishedName, mono: true);
            Field(panel, "Store folder", pkg.StorePath, mono: true);
            panel.Children.Add(Buttons(Action("trash-2", "DELETE PACKAGE", "DangerButton", async () => await DeletePackagesAsync([pkg]), !_busy)));
            _detail.Content = panel;
            return;
        }
        if (_selectedItem is DriverBackup backup)
        {
            var panel = DetailHeader(backup.Name, "BACKUP", "#38BDF8");
            Field(panel, "Created", backup.Created.ToString("yyyy-MM-dd HH:mm"));
            Field(panel, "Drivers", backup.Packages.ToString());
            Field(panel, "Size", SystemInfo.FormatBytes(backup.SizeBytes));
            Field(panel, "Folder", backup.Folder, mono: true);
            panel.Children.Add(Buttons(
                Action("rotate-ccw", "RESTORE", "PrimaryButton", async () => await RestoreBackupAsync(backup), !_busy),
                Action("archive", "NEW BACKUP", "SecondaryButton", async () => await BackupAsync(), !_busy),
                Action("folder-open", "OPEN FOLDER", "SecondaryButton", () => { Run("explorer.exe", $"\"{backup.Folder}\""); return Task.CompletedTask; }),
                Action("trash-2", "DELETE", "DangerButton", () => { DeleteBackup(backup); return Task.CompletedTask; })));
            _detail.Content = panel;
            return;
        }
        if (_filter == Filter.Backups && _selectedItem is null)
        {
            var panel = DetailHeader("Driver backups", "BACKUPS", "#38BDF8");
            panel.Children.Add(Note($"Back up saves every third-party driver to {DriverBackups.Root}. Select a backup to restore it, for example after reinstalling Windows.", "Info"));
            panel.Children.Add(Buttons(
                Action("archive", "BACK UP DRIVERS NOW", "PrimaryButton", async () => await BackupAsync(), !_busy),
                Action("folder-open", "OPEN BACKUPS FOLDER", "SecondaryButton", () =>
            {
                Directory.CreateDirectory(DriverBackups.Root);
                Run("explorer.exe", $"\"{DriverBackups.Root}\"");
                return Task.CompletedTask;
            })));
            if (!SystemRestore.IsOn)
            {
                panel.Children.Add(Note("System Restore is off, so driver installs can't make a restore point. DriverHub backs up the drivers it replaces instead; turning System Restore on also protects the rest of Windows.", "Warning"));
                panel.Children.Add(Buttons(Action("shield-check", "TURN ON SYSTEM RESTORE", "SecondaryButton", TurnOnSystemRestoreAsync, !_busy)));
            }
            _detail.Content = panel;
            return;
        }
        if (_filter == Filter.History && _selected is { } hd && _history is not null)
        {
            RenderHistory(hd);
            return;
        }
        if (_filter == Filter.Hidden && _selected is { } hidden && _hidden.TryGetValue(hidden.DeviceId, out var offer))
        {
            var panel = DetailHeader(hidden.Name, "HIDDEN", "#94A3B8");
            panel.Children.Add(Note($"Hidden update: {offer.Title}. A newer version is offered again.", "Info"));
            Field(panel, "Driver", $"{hidden.DriverProvider}  ·  v{hidden.DriverVersion}  ·  {hidden.DriverDate:yyyy-MM-dd}");
            panel.Children.Add(Buttons(Action("eye", "UNHIDE UPDATE", "PrimaryButton", () => { Unhide(hidden); return Task.CompletedTask; })));
            _detail.Content = panel;
            return;
        }

        if (_selected is not { } d)
        {
            var hint = Ui.Text("Select a device to see its driver, hardware IDs and available updates.", "Body", Ui.B("TextMuted"));
            hint.Margin = new Thickness(0, 14, 0, 0);
            _detail.Content = Ui.Stack(Ui.PanelTitle("DETAILS"), hint);
            return;
        }

        var row = new DriverRow(d);
        var name = new TextBlock { Text = d.Name, FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 22, Foreground = Ui.B("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 6) };
        var badge = new Border { Background = row.StatusBackground, Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = row.StatusText, Foreground = row.StatusBrush, FontSize = 11, FontWeight = FontWeights.Bold } };
        var stack = Ui.Stack(Ui.PanelTitle("DETAILS"), name, badge);

        if (!d.IsPresent)
        {
            stack.Children.Add(Note((d.LastSeen is { } seen ? $"Not connected since {seen:yyyy-MM-dd}. " : "Not connected. ")
                + "Removing it deletes the leftover device entry and its driver binding. If you connect the device again, Windows installs it again.", "Info"));
            Field(stack, "Class", d.ClassName);
            if (!string.IsNullOrWhiteSpace(d.Manufacturer)) Field(stack, "Manufacturer", d.Manufacturer);
            if (d.HardwareIds.Length > 0) Field(stack, "Hardware IDs", string.Join("\n", d.HardwareIds.Take(4)), mono: true);
            Field(stack, "Device ID", d.DeviceId, mono: true);
            var remove = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
            remove.Children.Add(Action("trash-2", "REMOVE DEVICE", "DangerButton", async () => await RemoveUnusedAsync([d]), !_busy));
            stack.Children.Add(remove);
            _detail.Content = stack;
            return;
        }

        if (_pending.TryGetValue(d.DeviceId, out var waiting))
            stack.Children.Add(Note($"{waiting.Title} is installed. Restart Windows to finish; until then the old driver is shown.", "Warning"));
        if (d.IsDisabled)
            stack.Children.Add(Note("This device is disabled in Device Manager. Enable it there if you need it.", "Info"));
        else if (d.HasProblem)
            stack.Children.Add(Note(DeviceInfo.Problem(d.ProblemCode) + (d.MissingDriver ? ". Check for updates or search the manufacturer's site." : "."), "Danger"));
        else if (d.IsOld && d.Update is null)
            stack.Children.Add(Note("This driver is more than 4 years old. It may be fine, but a newer one from the manufacturer can fix bugs or improve performance.", "Warning"));

        if (d.Update is { } u)
        {
            var version = u.Source == DriverSource.Amd
                ? $"{u.InstalledPackage ?? "AMD package not installed"}  →  {u.Version}  ·  "
                : u.ParsedVersion is { } v ? $"v{d.DriverVersion ?? "none"}  →  v{v}  ·  " : "";
            var box = Ui.Stack(
                Ui.Text(u.Source switch
                {
                    DriverSource.Catalog => "UPDATE FROM MICROSOFT UPDATE CATALOG",
                    DriverSource.Intel => "UPDATE FROM INTEL",
                    DriverSource.Nvidia => "UPDATE FROM NVIDIA",
                    DriverSource.Amd => "UPDATE FROM AMD",
                    DriverSource.Dell => "UPDATE FROM DELL",
                    DriverSource.HP => "UPDATE FROM HP",
                    DriverSource.Lenovo => "UPDATE FROM LENOVO",
                    _ => "UPDATE FROM WINDOWS UPDATE",
                }, "CardLabel", Ui.B("Info")),
                Ui.Text(u.Title, "Body"),
                Ui.Text($"{version}{u.Provider}  ·  {(u.Date is { } dt ? dt.ToString("yyyy-MM-dd") : "date unknown")}  ·  {SystemInfo.FormatBytes(u.DownloadBytes)}", "CardHint"));
            stack.Children.Add(new Border { Background = Ui.B("InfoChip"), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 14, 0, 0), Child = box });
            if (u.Interactive)
                stack.Children.Add(Note($"{u.Provider}'s installer window opens for this driver. Follow its steps.", "Info"));
            if (u.Source == DriverSource.Catalog && d.ClassName.Equals("MEDIA", StringComparison.OrdinalIgnoreCase))
                stack.Children.Add(Note("Audio drivers often come as a set tuned by your PC maker (speakers, microphones). If sound breaks after updating, roll back from the restore point or Device Manager.", "Warning"));
        }

        Field(stack, "Class", d.ClassName);
        if (!string.IsNullOrWhiteSpace(d.Manufacturer)) Field(stack, "Manufacturer", d.Manufacturer);
        Field(stack, "Driver", d.DriverVersion is null ? "No driver" : $"{d.DriverProvider}  ·  v{d.DriverVersion}  ·  {d.DriverDate:yyyy-MM-dd}");
        if (d.InfName is not null) Field(stack, "INF", d.InfName + (d.Signed == false ? "  (not signed)" : ""));
        Field(stack, "Hardware IDs", string.Join("\n", d.HardwareIds.Take(4)), mono: true);
        Field(stack, "Device ID", d.DeviceId, mono: true);

        var buttons = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        if (d.Update is not null)
            buttons.Children.Add(Action("download", "INSTALL UPDATE", "PrimaryButton", async () => await InstallAsync([d]), !_busy));
        if (d.Update?.DetailsUrl is { } details)
        {
            buttons.Children.Add(Action("external-link", "DRIVER PAGE", "SecondaryButton", () =>
            {
                Run(details);
                return Task.CompletedTask;
            }));
        }
        if (d.HardwareIds.FirstOrDefault() is { } hwid)
        {
            // Microsoft Update Catalog: also lists drivers Windows Update does not offer automatically.
            buttons.Children.Add(Action("package", "UPDATE CATALOG", "SecondaryButton", () =>
            {
                Run($"https://www.catalog.update.microsoft.com/Search.aspx?q={Uri.EscapeDataString(hwid)}");
                return Task.CompletedTask;
            }));
        }
        buttons.Children.Add(Action("globe", "SEARCH ONLINE", "SecondaryButton", () =>
        {
            var query = Uri.EscapeDataString($"{d.Name} driver {d.HardwareIds.FirstOrDefault()}");
            Run($"https://www.bing.com/search?q={query}");
            return Task.CompletedTask;
        }));
        stack.Children.Add(buttons);
        _detail.Content = stack;
    }

    /// <summary>History detail: the installed driver, then every other version in the store with a Roll back button.</summary>
    private void RenderHistory(DeviceInfo d)
    {
        var stack = DetailHeader(d.Name, "HISTORY", "#2DD4BF");
        var entries = _history!.TryGetValue(d.DeviceId, out var all) ? all : [];
        var previous = DriverHistory.Previous(entries, d);
        stack.Children.Add(Note(previous.Count > 0
            ? "Older versions of this driver are still in the driver store. Rolling back installs one of them (a restore point is created first)."
            : "No older version of this driver is left in the driver store.", "Info"));

        UIElement Entry(DriverVersionEntry e, bool current)
        {
            var info = Ui.Stack(
                Ui.Text($"v{e.Version}  ·  {(e.Date is { } dt ? dt.ToString("yyyy-MM-dd") : "date unknown")}", "Body"),
                Ui.Text($"{e.Provider}  ·  {e.InfName}" + (e.AddedToStore is { } added ? $"  ·  added {added:yyyy-MM-dd}" : ""), "CardHint"));
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(info);
            UIElement right = current
                ? new Border { Background = Imaging.Brush("#22C55E", 0x22), Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "INSTALLED", Foreground = Imaging.Brush("#22C55E"), FontSize = 11, FontWeight = FontWeights.Bold } }
                : Action("rotate-ccw", "ROLL BACK", "SecondaryButton", async () => await RollbackAsync(d, e), !_busy);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return new Border { Background = Ui.B("PanelBorder"), Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 8, 0, 0), Child = grid };
        }

        var label = Ui.Text("INSTALLED", "CardLabel", Ui.B("TextDim"));
        label.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(label);
        stack.Children.Add(Entry(entries.FirstOrDefault(e => e.IsCurrent)
            ?? new DriverVersionEntry(d.Name, d.DriverProvider ?? "", d.DriverVersion ?? "", d.DriverDate, d.InfName ?? "", 0, true, null), true));
        if (previous.Count > 0)
        {
            var older = Ui.Text("PREVIOUS VERSIONS", "CardLabel", Ui.B("TextDim"));
            older.Margin = new Thickness(0, 16, 0, 0);
            stack.Children.Add(older);
            foreach (var e in previous)
                stack.Children.Add(Entry(e, false));
        }
        _detail.Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = MainHeight + 40 };
    }

    private StackPanel DetailHeader(string title, string badgeText, string color)
    {
        var name = new TextBlock { Text = title, FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 22, Foreground = Ui.B("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 6) };
        var badge = new Border { Background = Imaging.Brush(color, 0x22), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = badgeText, Foreground = Imaging.Brush(color), FontSize = 11, FontWeight = FontWeights.Bold } };
        return Ui.Stack(Ui.PanelTitle("DETAILS"), name, badge);
    }

    private static WrapPanel Buttons(params Button[] buttons)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        foreach (var b in buttons)
            panel.Children.Add(b);
        return panel;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    public static (string Icon, string Color) ClassIcon(string cls) => cls.ToUpperInvariant() switch
    {
        "DISPLAY" or "MONITOR" => ("monitor", "#22C55E"),
        "NET" => ("wifi", "#38BDF8"),
        "MEDIA" or "AUDIOENDPOINT" => ("volume-2", "#F59E0B"),
        "BLUETOOTH" => ("bluetooth", "#3B82F6"),
        "USB" or "USBDEVICE" => ("usb", "#A78BFA"),
        "MOUSE" or "HIDCLASS" => ("mouse", "#94A3B8"),
        "KEYBOARD" => ("keyboard", "#94A3B8"),
        "PRINTER" or "PRINTQUEUE" => ("printer", "#64748B"),
        "CAMERA" or "IMAGE" => ("camera", "#EC4899"),
        "BATTERY" => ("battery", "#84CC16"),
        "DISKDRIVE" or "SCSIADAPTER" or "HDC" or "SCSI" or "VOLUME" => ("hard-drive", "#3B82F6"),
        "PROCESSOR" or "SYSTEM" or "COMPUTER" => ("cpu", "#06B6D4"),
        _ => ("package", "#94A3B8"),
    };

    private static Button Action(string icon, string text, string style, Func<Task> click, bool enabled = true)
    {
        var fg = style == "PrimaryButton" ? Ui.B("AccentText") : Ui.B("Text");
        var b = new Button
        {
            Style = Ui.Res<Style>(style),
            Content = Ui.Row(new LucideIcon { Icon = icon, Width = 15, Height = 15, Foreground = fg }, new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }),
            Margin = new Thickness(10, 0, 0, 0),
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, text);
        b.Click += async (_, _) => await click();
        return b;
    }

    /// <summary>
    /// Update count for the summary line: this session's check, or until then the last check's result (manual or
    /// background, kept in settings).
    /// </summary>
    private string UpdatesSummary()
    {
        if (_checking)
            return "checking for updates…";
        if (_updates is not null)
            return Available(Visible(Filter.Updates).Count()); // same number as the Updates tab
        var s = _app.Settings.Current;
        if (s.DriverCheckLast is not { } last || s.DriverNotifiedIds is not { } ids)
            return "updates not checked yet";
        return $"{Available(ids.Count)} (last check {(last.Date == DateTime.Today ? last.ToString("HH:mm") : last.ToString("MMM d"))})";

        static string Available(int n) => n == 1 ? "1 update available" : $"{n} updates available";
    }

    private void RenderRestartBanner()
    {
        var count = _pendingList.Count;
        _restartBanner.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count == 0)
            return;
        var names = _pendingList.Select(p => _devices.FirstOrDefault(d => _pending.TryGetValue(d.DeviceId, out var x) && x.UpdateId == p.UpdateId)?.Name ?? p.Title).Distinct().ToList();
        _restartText.Text = (count == 1 ? "Restart Windows to finish 1 driver: " : $"Restart Windows to finish {count} drivers: ")
            + string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" and {names.Count - 3} more" : "") + ".";
    }

    private async Task TurnOnSystemRestoreAsync()
    {
        _busy = true;
        RenderDetail();
        _status.Foreground = Ui.B("Info");
        _status.Text = "Turning on System Restore…";
        var error = await SystemRestore.TurnOnAsync();
        _busy = false;
        _status.Foreground = Ui.B(error is null ? "Success" : error == "Cancelled" ? "Info" : "Warning");
        _status.Text = error switch
        {
            null => "System Restore is on. Driver installs make a restore point first.",
            "Cancelled" => "System Restore was not changed.",
            _ => $"Could not turn on System Restore: {error}",
        };
        RenderDetail();
    }

    private void RestartWindows()
    {
        if (!DurazznoDialog.Confirm(Window.GetWindow(this), "Restart Windows now?",
                "Save your work first. Windows restarts right away to finish installing the drivers.", "Restart", icon: "power", tone: "Warning"))
            return;
        try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { Log.Warn($"Could not restart Windows: {ex.Message}"); }
    }

    private static void Run(string file, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn($"Could not start {file}: {ex.Message}"); }
    }

    private static void Field(StackPanel stack, string label, string value, bool mono = false)
    {
        var l = Ui.Text(label, "CardLabel", Ui.B("TextDim"));
        l.FontSize = 12.5;
        l.Margin = new Thickness(0, 14, 0, 2);
        stack.Children.Add(l);
        stack.Children.Add(new TextBox
        {
            Text = value,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Ui.B("Text"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = mono ? Ui.Res<FontFamily>("MonoFont") : Ui.Res<FontFamily>("BodyFont"),
            FontSize = mono ? 12 : 13,
            Padding = new Thickness(0),
        });
    }

    private static UIElement Note(string text, string tone) =>
        new Border { Background = Ui.B(tone + "Chip"), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 14, 0, 0), Child = Ui.Text(text, "Body", Ui.B(tone)) };
}
