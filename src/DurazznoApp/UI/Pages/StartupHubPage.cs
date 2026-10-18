using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using Durazzno.Core;
using Durazzno.Startup;
using Durazzno.Tools;
using Durazzno.UI.Controls;
using Microsoft.Win32;

namespace Durazzno.UI.Pages;

/// <summary>One row of the StartupHub list (public so the XAML data template can bind to it).</summary>
public sealed class StartupRow
{
    internal StartupRow(StartupEntry e, StartupImpact impact = StartupImpact.NotMeasured)
    {
        (ImpactText, var impactColor) = impact switch
        {
            StartupImpact.High => ("HIGH", "#EF4444"),
            StartupImpact.Medium => ("MEDIUM", "#F97316"),
            StartupImpact.Low => ("LOW", "#22C55E"),
            StartupImpact.None => ("NOT RUN", "#64748B"),
            _ => ("", "#64748B"),
        };
        ImpactBrush = Imaging.Brush(impactColor);
        ImpactBackground = Imaging.Brush(impactColor, impact == StartupImpact.NotMeasured ? (byte)0 : (byte)0x22);
        Entry = e;
        Name = e.Name;
        Enabled = e.Enabled;
        CanToggle = e.CanToggle;
        Command = e.Command;
        Opacity = e.Enabled ? 1 : 0.5;

        var publisher = e.IsThisApp ? AppInfo.Name : string.IsNullOrWhiteSpace(e.Signature.Publisher) ? "Unknown publisher" : e.Signature.Publisher;
        Sub = $"{publisher}  ·  {e.Trigger}";

        (Icon, IconBrush) = e.Category switch
        {
            StartupCategory.Logon => ("log-in", Imaging.Brush("#F5C400")),
            StartupCategory.Tasks => ("calendar-clock", Imaging.Brush("#38BDF8")),
            StartupCategory.Services => ("cog", Imaging.Brush("#A78BFA")),
            _ => ("shield", Imaging.Brush(e.Unusual ? "#EF4444" : "#64748B")),
        };

        (StatusText, var color) = e.IsThisApp ? ("THIS APP", "#38BDF8")
            : e.Category == StartupCategory.Advanced
            ? (e.Unusual ? "UNUSUAL" : "DEFAULT", e.Unusual ? "#EF4444" : "#22C55E")
            : e.Signature.State switch
            {
                SignatureState.Verified => ("VERIFIED", "#22C55E"),
                SignatureState.Unsigned => ("UNSIGNED", "#F97316"),
                SignatureState.Missing => ("FILE MISSING", "#EF4444"),
                _ => ("UNKNOWN", "#94A3B8"),
            };
        StatusBrush = Imaging.Brush(color);
        StatusBackground = Imaging.Brush(color, 0x22);
    }

    internal StartupEntry Entry { get; }
    public string Name { get; }
    public string Sub { get; }

    /// <summary>Used as the accessible name of the list row.</summary>
    public override string ToString() => Name;
    public string Command { get; }
    public bool Enabled { get; }
    public bool CanToggle { get; }
    public double Opacity { get; }
    public string Icon { get; }
    public Brush IconBrush { get; }
    public string StatusText { get; }
    public string ImpactText { get; }
    public Brush ImpactBrush { get; }
    public Brush ImpactBackground { get; }
    public Brush StatusBrush { get; }
    public Brush StatusBackground { get; }
}

/// <summary>StartupHub: everything that runs automatically, with signature checks and one-click on/off.</summary>
internal sealed class StartupHubPage : UserControl, IDisposable
{
    private const double MainHeight = 560;

    private static readonly DataTemplate RowTemplate = (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:c="clr-namespace:Durazzno.UI.Controls;assembly=DurazznoApp">
            <Grid Height="46">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="58" />
                    <ColumnDefinition Width="28" />
                    <ColumnDefinition Width="1.1*" />
                    <ColumnDefinition Width="1.6*" />
                    <ColumnDefinition Width="86" />
                    <ColumnDefinition Width="104" />
                </Grid.ColumnDefinitions>
                <CheckBox Style="{DynamicResource ToggleSwitch}" IsChecked="{Binding Enabled, Mode=OneWay}" IsEnabled="{Binding CanToggle}"
                          HorizontalAlignment="Left" VerticalAlignment="Center" Margin="-16,0,0,0" />
                <c:LucideIcon Grid.Column="1" Icon="{Binding Icon}" Foreground="{Binding IconBrush}" Width="16" Height="16" HorizontalAlignment="Left" Opacity="{Binding Opacity}" />
                <StackPanel Grid.Column="2" VerticalAlignment="Center" Opacity="{Binding Opacity}" Margin="0,0,12,0">
                    <TextBlock Text="{Binding Name}" Foreground="#F2F2F3" FontSize="13" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                    <TextBlock Text="{Binding Sub}" Foreground="#7A7A84" FontSize="11.5" TextTrimming="CharacterEllipsis" />
                </StackPanel>
                <TextBlock Grid.Column="3" Text="{Binding Command}" Foreground="#9A9AA3" FontFamily="Cascadia Mono, Consolas" FontSize="11.5"
                           VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Opacity="{Binding Opacity}" Margin="0,0,12,0" />
                <Border Grid.Column="4" Background="{Binding ImpactBackground}" Padding="8,3" HorizontalAlignment="Left" VerticalAlignment="Center">
                    <TextBlock Text="{Binding ImpactText}" Foreground="{Binding ImpactBrush}" FontSize="11" FontWeight="Bold" />
                </Border>
                <Border Grid.Column="5" Background="{Binding StatusBackground}" Padding="8,3" HorizontalAlignment="Right" VerticalAlignment="Center">
                    <TextBlock Text="{Binding StatusText}" Foreground="{Binding StatusBrush}" FontSize="11" FontWeight="Bold" />
                </Border>
            </Grid>
        </DataTemplate>
        """);

    private readonly AppController _app;
    private readonly ToolDefinition _tool;
    private readonly ListBox _list;
    private readonly TextBox _search = new() { Width = 280, Tag = "Search name, publisher or path" };
    private readonly WrapPanel _categories = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _hideMicrosoft = new() { Content = "Hide Microsoft entries", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = Ui.Text("Scanning…", "CardHint");
    private readonly ContentControl _detail = new() { Focusable = false };
    private readonly Button _refresh;
    private readonly Button _measure;
    private readonly TextBlock _bootInfo = Ui.Text("", "CardHint");
    private BootImpactData? _impact = BootImpact.LoadCached();
    private HashSet<string> _entryImages = new(StringComparer.OrdinalIgnoreCase);

    private List<StartupEntry> _entries = [];
    private StartupCategory? _category;
    private StartupEntry? _selected;
    private bool _loading;
    private bool _busy;

    public StartupHubPage(AppController app, ToolDefinition tool)
    {
        _app = app;
        _tool = tool;
        var accent = Imaging.Brush(tool.Accent);

        var title = Ui.Stack(Ui.Text(tool.Name, "PageTitle"), Ui.Text(tool.Tagline, "PageSubtitle"));
        title.Margin = new Thickness(20, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;

        _search.Style = Ui.Res<Style>("SearchBox");
        _search.VerticalAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => RenderList();
        _refresh = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = IconLabel("refresh-cw", "REFRESH", Ui.B("Text")), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(_refresh, "REFRESH");
        _refresh.Click += async (_, _) => await LoadAsync();
        _measure = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = IconLabel("timer", "BOOT IMPACT", Ui.B("Text")), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Reads how long Windows took to boot and how much CPU and disk each startup entry used at your last logon (asks for admin once)." };
        System.Windows.Automation.AutomationProperties.SetName(_measure, "BOOT IMPACT");
        _measure.Click += async (_, _) => await MeasureImpactAsync();
        var actions = Ui.Row(_search, _measure, _refresh);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Center;

        var header = new Grid();
        header.Children.Add(Ui.Row(Ui.IconChip(tool.Icon, accent, Imaging.Brush(tool.Accent, 0x26), 64, 52), title));
        header.Children.Add(actions);

        _hideMicrosoft.Style = Ui.Res<Style>("ToggleSwitch");
        _hideMicrosoft.Checked += (_, _) => Render();
        _hideMicrosoft.Unchecked += (_, _) => Render();
        var filters = new Grid { Margin = new Thickness(0, 24, 0, 0) };
        filters.Children.Add(_categories);
        _hideMicrosoft.HorizontalAlignment = HorizontalAlignment.Right;
        filters.Children.Add(_hideMicrosoft);
        _summary.Margin = new Thickness(0, 8, 0, 0);

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
            if (_list.SelectedItem is StartupRow row)
            {
                _selected = row.Entry;
                RenderDetail();
            }
        };
        // Switch clicks inside the rows.
        _list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(async (_, e) =>
        {
            if (e.OriginalSource is CheckBox { DataContext: StartupRow row } box)
            {
                e.Handled = true;
                await ToggleAsync(row.Entry, box.IsChecked == true);
            }
        }));

        var headerRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (text, col) in new[] { ("ON", 0), ("ENTRY", 2), ("COMMAND", 3), ("IMPACT", 4), ("STATUS", 5) })
        {
            var h = Ui.Text(text, "CardLabel", Ui.B("TextDim"));
            h.FontSize = 12.5;
            if (col == 5) h.HorizontalAlignment = HorizontalAlignment.Right;
            headerRow.Children.Add(h.At(col));
        }
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });

        var main = Ui.Columns(2, 20);
        main.ColumnDefinitions[0].Width = new GridLength(2.15, GridUnitType.Star);
        main.Margin = new Thickness(0, 16, 0, 0);
        main.Children.Add(Ui.Card(Ui.Stack(Ui.PanelTitle("AUTOSTART ENTRIES"), headerRow, _list)).At(0));
        main.Children.Add(Ui.Card(_detail).At(2));

        _bootInfo.Margin = new Thickness(0, 4, 0, 0);
        Content = Ui.Stack(header, filters, _summary, _bootInfo, main);
        RenderBootInfo();

        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && _entries.Count == 0 && !_loading)
                await LoadAsync();
        };
        RenderDetail();
    }

    public void Dispose()
    {
    }

    private async Task LoadAsync()
    {
        if (_loading)
            return;

        _loading = true;
        _refresh.IsEnabled = false;
        _summary.Text = "Scanning startup locations and checking signatures…";
        try
        {
            var watch = Stopwatch.StartNew();
            _entries = await Task.Run(StartupScanner.Scan);
            Log.Info($"StartupHub: {_entries.Count} entries in {watch.Elapsed.TotalSeconds:0.0}s");
            if (_selected is not null)
                _selected = _entries.FirstOrDefault(e => SameEntry(e, _selected));
            Render();
        }
        catch (Exception ex)
        {
            Log.Error("StartupHub scan failed", ex);
            _summary.Text = $"Could not read startup entries: {ex.Message}";
        }
        finally
        {
            _loading = false;
            _refresh.IsEnabled = true;
        }
    }

    private async Task MeasureImpactAsync()
    {
        _measure.IsEnabled = false;
        _bootInfo.Foreground = Ui.B("TextMuted");
        _bootInfo.Text = "Reading boot and logon data…";
        try
        {
            var error = await BootImpact.MeasureAsync();
            _impact = BootImpact.LoadCached();
            if (error is not null)
            {
                _bootInfo.Foreground = Ui.B("Warning");
                _bootInfo.Text = error == "Cancelled" ? "Boot impact needs admin rights to read Windows' startup records." : error;
                return;
            }
            RenderList();
            RenderDetail();
            RenderBootInfo();
        }
        finally
        {
            _measure.IsEnabled = true;
        }
    }

    /// <summary>Last boot time, the average of the recorded boots, and when the impact was measured.</summary>
    private void RenderBootInfo()
    {
        _bootInfo.Foreground = Ui.B("TextMuted");
        if (_impact is null)
        {
            _bootInfo.Text = "Press BOOT IMPACT to see how long Windows takes to start and what slows it down.";
            return;
        }
        static string S(int ms) => $"{ms / 1000.0:0.#} s";
        var parts = new List<string>();
        if (_impact.Boots.FirstOrDefault() is { } last)
        {
            parts.Add($"Last boot {S(last.TotalMs)} ({S(last.MainPathMs)} to the desktop + {S(last.PostBootMs)} after it), {last.Time:MMM d}");
            if (_impact.Boots.Count > 1)
                parts.Add($"average of last {_impact.Boots.Count}: {S((int)_impact.Boots.Average(b => b.TotalMs))}");
        }
        parts.Add(_impact.LogonTime is { } logon ? $"impact from the logon of {logon:MMM d, HH:mm}" : "no logon data yet");
        _bootInfo.Text = string.Join("  ·  ", parts);
    }

    private StartupImpact ImpactOf(StartupEntry e) =>
        e.Category is StartupCategory.Logon or StartupCategory.Tasks ? BootImpact.For(e, _impact, _entryImages).Impact : StartupImpact.NotMeasured;

    private static bool SameEntry(StartupEntry a, StartupEntry b) =>
        a.Source == b.Source && a.Name == b.Name && a.Location == b.Location && a.TaskPath == b.TaskPath && a.ServiceName == b.ServiceName;

    private IEnumerable<StartupEntry> Visible(StartupCategory? category)
    {
        var hideMs = _hideMicrosoft.IsChecked == true;
        var query = _search.Text.Trim();
        return _entries.Where(e =>
            (category is null || e.Category == category)
            && !(hideMs && e.IsMicrosoft && !e.Unusual)
            && (query.Length == 0
                || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.Command.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.Signature.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    private void Render()
    {
        RenderCategories();
        RenderList();
        RenderDetail();
    }

    private void RenderCategories()
    {
        _categories.Children.Clear();
        void Add(string label, StartupCategory? category)
        {
            var count = Visible(category).Count();
            var b = new RadioButton { Content = $"{label}  {count}", GroupName = "sh-cat", Style = Ui.Res<Style>("Segment"), IsChecked = _category == category };
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            b.Checked += (_, _) =>
            {
                _category = category;
                RenderList();
            };
            _categories.Children.Add(b);
        }

        Add("All", null);
        Add("Logon", StartupCategory.Logon);
        Add("Tasks", StartupCategory.Tasks);
        Add("Services", StartupCategory.Services);
        Add("Advanced", StartupCategory.Advanced);
    }

    private void RenderList()
    {
        var visible = Visible(_category)
            .OrderBy(e => e.Category)
            .ThenByDescending(e => e.Unusual)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _entryImages = _entries.Where(e => e.ImagePath is { Length: > 0 }).Select(e => e.ImagePath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = visible.Select(e => new StartupRow(e, ImpactOf(e))).ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = _selected is null ? null : rows.FirstOrDefault(r => r.Entry == _selected);

        var all = Visible(null).ToList();
        _summary.Text = $"{all.Count} entries  ·  {all.Count(e => e.Enabled && e.Category != StartupCategory.Advanced)} enabled  ·  "
            + $"{all.Count(e => e.Signature.State == SignatureState.Unsigned && !e.IsThisApp)} unsigned  ·  {all.Count(e => e.Signature.State == SignatureState.Missing)} missing files"
            + (all.Any(e => e.Unusual) ? $"  ·  {all.Count(e => e.Unusual)} unusual system settings" : "")
            + (_hideMicrosoft.IsChecked == true ? $"  ·  {_entries.Count - all.Count} Microsoft entries hidden" : "");
    }

    private void RenderDetail()
    {
        if (_selected is not { } e)
        {
            var hint = Ui.Text("Select an entry to see where it comes from, who signed it and what it runs.", "Body", Ui.B("TextMuted"));
            hint.Margin = new Thickness(0, 14, 0, 0);
            _detail.Content = Ui.Stack(Ui.PanelTitle("DETAILS"), hint);
            return;
        }

        var row = new StartupRow(e, ImpactOf(e));
        var name = new TextBlock { Text = e.Name, FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 22, Foreground = Ui.B("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 6) };
        var badge = new Border { Background = row.StatusBackground, Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = row.StatusText, Foreground = row.StatusBrush, FontSize = 11, FontWeight = FontWeights.Bold } };
        var stack = Ui.Stack(Ui.PanelTitle("DETAILS"), name, badge);

        if (e.Category is StartupCategory.Logon or StartupCategory.Tasks && _impact is not null)
        {
            var (impact, cpu, disk) = BootImpact.For(e, _impact, _entryImages);
            if (impact is StartupImpact.High or StartupImpact.Medium or StartupImpact.Low)
                stack.Children.Add(Note($"At the last logon it used {cpu / 1000:0.##} s of CPU and {Hardware.SystemInfo.FormatBytes(disk)} of disk (including what it started). "
                    + (impact == StartupImpact.High ? "Disabling it makes startup noticeably faster." : impact == StartupImpact.Medium ? "It adds a little to startup." : "It barely affects startup."),
                    impact == StartupImpact.High ? "Warning" : "Info"));
            else if (impact == StartupImpact.None && e.Enabled)
                stack.Children.Add(Note("It did not run at the last logon.", "Info"));
        }

        if (e.Unusual)
            stack.Children.Add(Note("This is not the Windows default. Malware often hides here; check it is something you recognise.", "Danger"));
        else if (e.Signature.State == SignatureState.Missing && e.Category != StartupCategory.Advanced)
            stack.Children.Add(Note("The file this entry points to does not exist. It is probably left over from an uninstalled program and safe to remove.", "Danger"));
        else if (e.IsThisApp)
            stack.Children.Add(Note("This is Durazzno App itself. Turn it off here or in Settings if you don't want it to start with Windows.", "Info"));
        else if (e.Signature.State == SignatureState.Unsigned)
            stack.Children.Add(Note("Not digitally signed. Fine for many small tools, but make sure you know what it is.", "Warning"));

        Field(stack, "Publisher", e.IsThisApp ? $"{AppInfo.Name} (Galaxy Apps)" : string.IsNullOrWhiteSpace(e.Signature.Publisher) ? "Unknown" : e.Signature.Publisher);
        Field(stack, "Runs", e.Trigger);
        Field(stack, "File", e.ImagePath ?? "—", mono: true);
        Field(stack, "Command", e.Command, mono: true);
        Field(stack, "Location", e.Location, mono: true);
        if (!string.IsNullOrWhiteSpace(e.Description))
            Field(stack, "Description", e.Description);

        var buttons = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        if (e.CanToggle)
        {
            var toggle = new Button { Style = Ui.Res<Style>(e.Enabled ? "SecondaryButton" : "PrimaryButton"), Content = IconLabel("power", e.Enabled ? "DISABLE" : "ENABLE", e.Enabled ? Ui.B("Text") : Ui.B("AccentText")), Margin = new Thickness(0, 0, 8, 8), IsEnabled = !_busy };
            toggle.Click += async (_, _) => await ToggleAsync(e, !e.Enabled);
            buttons.Children.Add(toggle);
        }

        if (e.ImagePath is { } image && File.Exists(image))
        {
            var open = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = IconLabel("folder-open", "FILE LOCATION", Ui.B("Text")), Margin = new Thickness(0, 0, 8, 8) };
            open.Click += (_, _) => Explorer($"/select,\"{image}\"");
            buttons.Children.Add(open);
        }

        if (RegistryPath(e) is { } regPath)
        {
            var reg = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = IconLabel("external-link", "REGEDIT", Ui.B("Text")), Margin = new Thickness(0, 0, 8, 8) };
            reg.Click += (_, _) => OpenRegedit(regPath);
            buttons.Children.Add(reg);
        }

        if (e.CanDelete)
        {
            var delete = new Button { Style = Ui.Res<Style>("DangerButton"), Content = IconLabel("trash-2", "DELETE", Brushes.White), Margin = new Thickness(0, 0, 8, 8), IsEnabled = !_busy };
            delete.Click += async (_, _) => await DeleteAsync(e);
            buttons.Children.Add(delete);
        }

        stack.Children.Add(buttons);
        if (e.Machine && (e.CanToggle || e.CanDelete))
        {
            var admin = Ui.Text("Machine-wide entry: Windows asks for administrator permission when you change it.", "CardHint", Ui.B("TextDim"));
            admin.TextWrapping = TextWrapping.Wrap;
            admin.TextTrimming = TextTrimming.None;
            stack.Children.Add(admin);
        }

        _detail.Content = stack;
    }

    private async Task ToggleAsync(StartupEntry e, bool enabled)
    {
        if (_busy || !e.CanToggle)
            return;

        _busy = true;
        RenderDetail();
        var error = await StartupActions.SetEnabledAsync(e, enabled);
        _busy = false;

        if (error is null)
        {
            e.Enabled = enabled;
            Log.Info($"StartupHub: {(enabled ? "enabled" : "disabled")} {e.Name}");
        }
        else if (error != "Cancelled")
        {
            _app.NotifyTool(_tool, $"Could not change {e.Name}: {error}", System.Windows.Forms.ToolTipIcon.Error);
        }

        _selected = e;
        Render();
    }

    private async Task DeleteAsync(StartupEntry e)
    {
        if (!DurazznoDialog.Confirm(Window.GetWindow(this), "Remove this startup entry?",
                "The program itself is not uninstalled; it just stops starting with Windows.",
                "Remove", icon: "trash-2", tone: "Danger", destructive: true, bullets: [e.Name, e.Location]))
            return;

        _busy = true;
        RenderDetail();
        var error = await StartupActions.DeleteAsync(e);
        _busy = false;

        if (error is null)
        {
            _entries.Remove(e);
            _selected = null;
        }
        else if (error != "Cancelled")
        {
            _app.NotifyTool(_tool, $"Could not delete {e.Name}: {error}", System.Windows.Forms.ToolTipIcon.Error);
        }

        Render();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>Full regedit path for registry-based entries (handles the 32-bit WOW6432Node view).</summary>
    private static string? RegistryPath(StartupEntry e)
    {
        if (e.Location.StartsWith("HKCU", StringComparison.Ordinal))
            return "HKEY_CURRENT_USER" + e.Location[4..];
        if (!e.Location.StartsWith("HKLM", StringComparison.Ordinal))
            return null;

        const string wow = @"HKLM\WOW6432Node\Software\";
        return e.Location.StartsWith(wow, StringComparison.OrdinalIgnoreCase)
            ? @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\" + e.Location[wow.Length..]
            : "HKEY_LOCAL_MACHINE" + e.Location[4..];
    }

    private static void OpenRegedit(string path)
    {
        try
        {
            // Regedit opens at the last key it remembers.
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                key.SetValue("LastKey", @"Computer\" + path);
            Process.Start(new ProcessStartInfo("regedit.exe", "/m") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open regedit: {ex.Message}");
        }
    }

    private static void Explorer(string args)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn($"Could not open explorer: {ex.Message}"); }
    }

    private static void Field(StackPanel stack, string label, string value, bool mono = false)
    {
        var l = Ui.Text(label, "CardLabel", Ui.B("TextDim"));
        l.FontSize = 12.5;
        l.Margin = new Thickness(0, 14, 0, 2);
        var v = new TextBox
        {
            Text = value,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Ui.B("Text"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = mono ? Ui.Res<FontFamily>("MonoFont") : Ui.Res<FontFamily>("BodyFont"),
            FontSize = mono ? 12 : 13,
            CaretBrush = Ui.B("Accent"),
            Padding = new Thickness(0),
        };
        stack.Children.Add(l);
        stack.Children.Add(v);
    }

    private static UIElement Note(string text, string tone)
    {
        var t = Ui.Text(text, "Body", Ui.B(tone));
        return new Border { Background = Ui.B(tone + "Chip"), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 14, 0, 0), Child = t };
    }

    private static StackPanel IconLabel(string icon, string text, Brush foreground) => Ui.Row(
        new LucideIcon { Icon = icon, Width = 15, Height = 15, Foreground = foreground },
        new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
}
