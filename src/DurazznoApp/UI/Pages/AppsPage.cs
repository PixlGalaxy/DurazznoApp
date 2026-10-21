using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Durazzno.Apps;
using Durazzno.Core;
using Durazzno.Hardware;
using Durazzno.Tools;
using Durazzno.UI.Controls;

namespace Durazzno.UI.Pages;

/// <summary>
/// Apps: what Durazzno downloaded (Installed) and what Webin Cloud Server offers (Online), as tiles. Clicking an online
/// app shows its message (requirements) if it has one, then downloads it into apps\ next to Durazzno; clicking an
/// installed app opens it. Right-click an installed app to open its folder, update it or remove it.
/// </summary>
internal sealed class AppsPage : UserControl
{
    public const string Accent = "#F472B6";
    private const double TileWidth = 330;

    private readonly AppController _app;
    private readonly WrapPanel _tiles = new() { Margin = new Thickness(-10, 16, -10, 0) };
    private readonly ContentControl _body = new() { Focusable = false };
    private (string Icon, string Title, string Detail)? _error;
    private readonly WrapPanel _tabs = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _refresh;
    private readonly TextBox _search = new() { Width = 280, Tag = "Search apps" };
    private readonly Dictionary<string, (TextBlock Label, Grid Bar)> _progress = [];
    private readonly HashSet<string> _downloading = [];

    private List<AppEntry>? _online;
    private List<InstalledApp> _installed = AppCatalog.Installed();
    private bool _showOnline;
    private string? _details; // key of the Online app shown large (null: the tiles)
    private bool _loading;

    public AppsPage(AppController app)
    {
        _app = app;
        var accent = Imaging.Brush(Accent);
        var title = Ui.Stack(Ui.Text("Apps", "PageTitle"), Ui.Text("Apps from Webin Cloud Server, one click away", "PageSubtitle"));
        title.Margin = new Thickness(20, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        _refresh = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = IconLabel("refresh-cw", "REFRESH"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        System.Windows.Automation.AutomationProperties.SetName(_refresh, "REFRESH");
        _refresh.Click += async (_, _) => await LoadOnlineAsync();

        _search.Style = Ui.Res<Style>("SearchBox");
        _search.VerticalAlignment = VerticalAlignment.Center;
        _search.Margin = new Thickness(0, 0, 10, 0);
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search apps");
        _search.TextChanged += (_, _) => { _details = null; Render(); };
        var actions = Ui.Row(_search, _refresh);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Center;

        var header = new Grid();
        header.Children.Add(Ui.Row(Ui.IconChip("package", accent, Imaging.Brush(Accent, 0x26), 64, 52), title));
        header.Children.Add(actions);

        var tabsRow = new Grid { Margin = new Thickness(0, 24, 0, 0) };
        tabsRow.Children.Add(_tabs);

        Content = Ui.Stack(header, tabsRow, _body);

        // The catalog is never cached: it is fetched when the page opens, when the Online tab opens, on REFRESH and every
        // 5 minutes while the page is shown, so when the server goes away the online apps disappear (and cannot be
        // installed) until it is back.
        _poll.Tick += async (_, _) => await LoadOnlineAsync(quiet: true);
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible)
            {
                _poll.Stop();
                return;
            }
            _installed = AppCatalog.Installed();
            _showOnline = false; // the page always opens on Installed
            _details = null;
            Render();
            _poll.Start();
            await LoadOnlineAsync();
        };
        Render();
    }

    private readonly System.Windows.Threading.DispatcherTimer _poll = new() { Interval = TimeSpan.FromMinutes(5) };
    private string? _catalogStamp;

    /// <summary>Fetches the catalog. quiet: the 10-second refresh (no "Loading…", re-renders only when something changed).</summary>
    private async Task LoadOnlineAsync(bool quiet = false)
    {
        if (_loading)
            return;
        _loading = true;
        if (!quiet)
            _refresh.IsEnabled = false; // the background refresh leaves the button alone (no blink every few seconds)
        if (!quiet && _online is null)
            Render(); // Online shows "Loading apps" until the list arrives
        var before = (_catalogStamp, _error);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(quiet ? 8 : 20));
            _online = await AppCatalog.GetOnlineAsync(cts.Token);
            _catalogStamp = string.Join("|", _online.Select(a => $"{a.Key}:{a.Version}:{a.Sha256}:{a.Name}:{a.Description}:{a.Icon}"));
            _error = null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Apps: catalog: {ex.Message}");
            _online = null; // nothing from a server that is not answering: no stale list, nothing to install
            _catalogStamp = null;
            _error = ex switch
            {
                // The server answered, but with an error (404 = apps.json not uploaded, 5xx = server problem).
                HttpRequestException { StatusCode: { } code } => ("triangle-alert", "Server error",
                    $"Webin Cloud Server answered with error {(int)code} and did not send the apps list. Installed apps still open."),
                // Reached the server, but the catalog is unsigned or was altered.
                InvalidDataException => ("shield-alert", "Server error", ex.Message),
                // Never reached the server (no network, DNS, refused, timeout).
                _ => ("wifi-off", "Offline", "Can't reach Webin Cloud Server. Check your connection; installed apps still open."),
            };
        }
        finally
        {
            _loading = false;
            _refresh.IsEnabled = true;
            // The 10-second refresh only redraws when the catalog or the connection changed (no flicker, no lost hover).
            if (!quiet || before != (_catalogStamp, _error))
                Render();
        }
    }

    private void Render()
    {
        _tabs.Children.Clear();
        void Tab(string label, bool online, int? count)
        {
            var b = new RadioButton { Content = count is null ? label : $"{label}  {count}", GroupName = "apps-tab", Style = Ui.Res<Style>("Segment"), IsChecked = _showOnline == online };
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            b.Checked += (_, _) =>
            {
                _showOnline = online;
                _details = null;
                Render();
                if (online)
                    _ = LoadOnlineAsync(quiet: _online is not null); // fresh list each time Online opens
            };
            _tabs.Children.Add(b);
        }
        Tab("Installed", false, _installed.Count);
        Tab("Online", true, _online?.Count);

        // An Online app opened large; it closes when the app leaves the catalog (server gone, unpublished).
        if (_showOnline && _online?.FirstOrDefault(a => a.Key == _details) is { } shown)
        {
            _body.Content = DetailView(shown);
            return;
        }
        _details = null;

        _tiles.Children.Clear();
        _progress.Clear();
        _body.Content = _tiles;
        if (_showOnline)
        {
            if (_online is null && _loading)
                _body.Content = State("loader-circle", "Loading apps", "Getting the list from Webin Cloud Server.", "TextMuted", retry: false);
            else if (_error is { } error && _online is null)
                _body.Content = State(error.Icon, error.Title, error.Detail, error.Title == "Offline" ? "Warning" : "Danger", retry: true);
            else if (_online is { Count: 0 })
                _body.Content = State("package", "No apps yet", "Nothing is published on Webin Cloud Server right now.", "TextMuted", retry: true);
            var matches = (_online ?? []).Where(a => Matches(a.Name, a.Description)).ToList();
            if (_online is { Count: > 0 } && matches.Count == 0)
                _body.Content = NoMatches();
            foreach (var entry in matches)
                _tiles.Children.Add(OnlineTile(entry));
        }
        else
        {
            if (_installed.Count == 0)
                _body.Content = State("package", "No apps installed", "Open Online and click an app to download it.", "TextMuted", retry: false);
            var matches = _installed.Where(a => Matches(a.Name, _online?.FirstOrDefault(o => o.Key == a.Key)?.Description))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (_installed.Count > 0 && matches.Count == 0)
                _body.Content = NoMatches();
            foreach (var installed in matches)
                _tiles.Children.Add(InstalledTile(installed));
        }
    }

    /// <summary>Search: every word must appear in the name or the description.</summary>
    private bool Matches(string name, string? description)
    {
        var words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = $"{name} {description}";
        return words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private UIElement NoMatches() =>
        State("search", "No matches", $"Nothing in {(_showOnline ? "Online" : "Installed")} matches “{_search.Text.Trim()}”.", "TextMuted", retry: false);

    // ── Tiles ─────────────────────────────────────────────────────────────────────────────

    private FrameworkElement OnlineTile(AppEntry entry)
    {
        var installed = _installed.FirstOrDefault(a => a.Key == entry.Key);
        var hasUpdate = installed is not null && entry.Version is not null && entry.Version != installed.Version;
        var (text, tone) = _downloading.Contains(entry.Key) ? ("DOWNLOADING…", "Success")
            : installed is null ? ("GET", "Accent")
            : hasUpdate ? ($"UPDATE TO {entry.Version}", "Info")
            : ("OPEN", "Success");

        var tile = Tile(entry.Name, entry.Version, entry.Description, null, text, tone, out var iconHost, out var label, out var bar);
        _progress[entry.Key] = (label, bar);
        _ = LoadIconAsync(entry, iconHost);
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (_downloading.Contains(entry.Key))
                return;
            _details = entry.Key; // open it large: description, details, BACK / INSTALL
            Render();
        };
        return tile;
    }

    /// <summary>An Online app shown large: icon, full description, details, and BACK plus INSTALL / UPDATE / OPEN.</summary>
    private FrameworkElement DetailView(AppEntry entry)
    {
        var installed = _installed.FirstOrDefault(a => a.Key == entry.Key);
        var hasUpdate = installed is not null && entry.Version is not null && entry.Version != installed.Version;

        var iconHost = new ContentControl { Width = 104, Height = 104, Focusable = false, Content = Icon(null, 104) };
        _ = Task.Run(async () =>
        {
            var path = await AppCatalog.DownloadIconAsync(entry, CancellationToken.None);
            if (path is not null)
                await Dispatcher.InvokeAsync(() => iconHost.Content = Icon(path, 104));
        });

        var name = new TextBlock { Text = entry.Name, FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 34,
            Foreground = Ui.B("Text"), TextWrapping = TextWrapping.Wrap };
        var sub = Ui.Text((entry.Version is null ? "" : $"v{entry.Version}  ·  ") + "Webin Cloud Server", "CardHint", Ui.B("TextDim"));
        var names = Ui.Stack(name, sub);
        if (installed is not null)
        {
            var (text, tone) = hasUpdate ? ($"INSTALLED v{installed.Version}  ·  UPDATE AVAILABLE", "Info") : ($"INSTALLED v{installed.Version}", "Success");
            names.Children.Add(new Border
            {
                Background = Ui.B(tone + "Chip"), Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = text, Foreground = Ui.B(tone), FontSize = 11.5, FontWeight = FontWeights.Bold },
            });
        }
        names.Margin = new Thickness(24, 0, 0, 0);
        names.VerticalAlignment = VerticalAlignment.Center;
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(iconHost);
        head.Children.Add(names.At(1));

        var description = Ui.Text(string.IsNullOrWhiteSpace(entry.Description) ? "No description." : entry.Description.Trim(), "Body", Ui.B("TextMuted"));
        description.TextWrapping = TextWrapping.Wrap;
        description.FontSize = 15;
        description.MaxWidth = 820;
        description.HorizontalAlignment = HorizontalAlignment.Left;
        description.Margin = new Thickness(0, 26, 0, 0);
        var stack = Ui.Stack(head, description);

        // The catalog's message (requirements, warnings): shown here instead of a popup before downloading.
        if (!string.IsNullOrWhiteSpace(entry.Message))
        {
            var message = Ui.Text(entry.Message.Trim(), "Body", Ui.B("Warning"));
            message.TextWrapping = TextWrapping.Wrap;
            message.Margin = new Thickness(12, 0, 0, 0);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new LucideIcon { Icon = "triangle-alert", Width = 18, Height = 18, Foreground = Ui.B("Warning"), VerticalAlignment = VerticalAlignment.Top });
            row.Children.Add(message.At(1));
            stack.Children.Add(new Border { Background = Ui.B("WarningChip"), Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 20, 0, 0), MaxWidth = 820,
                HorizontalAlignment = HorizontalAlignment.Left, Child = row });
        }

        var details = Ui.Stack(Ui.PanelTitle("DETAILS"));
        details.Margin = new Thickness(0, 28, 0, 0);
        void Field(string label, string value, bool mono = false)
        {
            var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(Ui.Text(label, "CardLabel"));
            var text = Ui.Text(value, "Body", Ui.B("Text"));
            text.TextWrapping = TextWrapping.Wrap;
            if (mono)
                text.FontFamily = Ui.Res<FontFamily>("MonoFont");
            row.Children.Add(text.At(1));
            details.Children.Add(row);
        }
        Field("VERSION", entry.Version ?? "Not stated");
        if (entry.Size is > 0)
            Field("DOWNLOAD SIZE", SystemInfo.FormatBytes(entry.Size.Value));
        Field("FROM", "Webin Cloud Server");
        Field("CHECK", string.IsNullOrWhiteSpace(entry.Sha256) ? "Not checked (the app updates itself)" : "SHA-256 checked before it's saved");
        var shortcuts = new[] { entry.DesktopShortcut ? "Desktop" : null, entry.StartMenuShortcut ? "Start Menu" : null }.OfType<string>().ToList();
        Field("SHORTCUTS", shortcuts.Count == 0 ? "None" : string.Join(" and ", shortcuts));
        Field("INSTALLS TO", Path.Combine(AppCatalog.AppsDir, entry.Key), mono: true);
        stack.Children.Add(details);

        var back = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = IconLabel("chevron-left", "BACK") };
        System.Windows.Automation.AutomationProperties.SetName(back, "BACK");
        back.Click += (_, _) => { _details = null; Render(); };
        var (icon, action) = installed is null ? ("download", "INSTALL") : hasUpdate ? ("download", $"UPDATE TO {entry.Version}") : ("external-link", "OPEN");
        var primary = new Button { Style = Ui.Res<Style>("PrimaryButton"), Margin = new Thickness(10, 0, 0, 0),
            Content = Ui.Row(new LucideIcon { Icon = icon, Width = 15, Height = 15, Foreground = Ui.B("AccentText") },
                new TextBlock { Text = action, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }) };
        System.Windows.Automation.AutomationProperties.SetName(primary, action);
        primary.Click += (_, _) =>
        {
            _details = null;
            Render(); // back to the tiles, where this app's card shows the download progress
            if (installed is not null && !hasUpdate)
                OpenApp(installed);
            else
                _ = DownloadAsync(entry, confirmed: true);
        };
        var buttons = Ui.Row(back, primary);
        buttons.Margin = new Thickness(0, 32, 0, 0);
        stack.Children.Add(buttons);

        var card = Ui.Card(stack, new Thickness(36, 32, 36, 32));
        card.Margin = new Thickness(0, 16, 0, 0);
        return card;
    }

    private FrameworkElement InstalledTile(InstalledApp installed)
    {
        var online = _online?.FirstOrDefault(a => a.Key == installed.Key);
        var hasUpdate = online?.Version is { } v && v != installed.Version;
        var tile = Tile(installed.Name, installed.Version, online?.Description, installed.IconPath,
            hasUpdate ? $"UPDATE TO {online!.Version}" : "OPEN", hasUpdate ? "Info" : "Success", out _, out var label, out var bar);
        _progress[installed.Key] = (label, bar);
        tile.MouseLeftButtonUp += (_, _) => OpenApp(installed);
        tile.MouseRightButtonUp += (_, e) =>
        {
            var items = new List<CardMenuItem>
            {
                new("external-link", "Open", () => OpenApp(installed)),
                new("folder-open", "Show in folder", () => Launcher.OpenFolder(Path.GetDirectoryName(installed.FilePath)!)),
            };
            if (hasUpdate)
                items.Add(new CardMenuItem("download", $"Update to {online!.Version}", () => _ = DownloadAsync(online)));
            items.Add(new CardMenuItem("trash-2", "Remove", () => RemoveApp(installed), Danger: true));
            CardMenu.Show(tile, items);
            e.Handled = true;
        };
        return tile;
    }

    private static FrameworkElement Tile(string name, string? version, string? description, string? iconPath, string action, string tone,
        out ContentControl iconHost, out TextBlock label, out Grid bar)
    {
        iconHost = new ContentControl { Width = 52, Height = 52, Focusable = false, Content = Icon(iconPath) };
        var title = new TextBlock { Text = name, FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 18, Foreground = Ui.B("Text"), TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = Ui.Text(version is null ? "" : $"v{version}", "CardHint", Ui.B("TextDim"));
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(iconHost);
        var names = Ui.Stack(title, sub);
        names.Margin = new Thickness(14, 0, 0, 0);
        names.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(names.At(1));

        var desc = Ui.Text(description ?? "", "CardHint");
        desc.TextWrapping = TextWrapping.Wrap;
        desc.TextTrimming = TextTrimming.CharacterEllipsis;
        desc.Height = 40;
        desc.Margin = new Thickness(0, 12, 0, 0);

        label = new TextBlock { Text = action, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Ui.B(tone), Margin = new Thickness(0, 12, 0, 6) };
        bar = new Grid { Height = 4, Visibility = Visibility.Collapsed };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) });
        bar.Children.Add(new Border { Background = Ui.B("Success") });
        bar.Children.Add(new Border { Background = Ui.B("PanelBorder") }.At(1));

        var card = Ui.Card(Ui.Stack(head, desc, label, bar));
        card.Width = TileWidth;
        card.Margin = new Thickness(10);
        card.Cursor = Cursors.Hand;
        card.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetName(card, name);
        return card;
    }

    private static UIElement Icon(string? path, double size = 52)
    {
        if (path is not null && File.Exists(path))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = (int)(size * 2);
                image.EndInit();
                image.Freeze();
                return new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
            }
            catch
            {
                // not an image: fall back to the generic icon
            }
        }
        return Ui.IconChip("package", Imaging.Brush(Accent), Imaging.Brush(Accent, 0x26), size, size * 0.46);
    }

    private static async Task LoadIconAsync(AppEntry entry, ContentControl host)
    {
        if (await AppCatalog.DownloadIconAsync(entry, CancellationToken.None) is { } path)
            host.Content = Icon(path);
    }

    /// <summary>A centred message in place of the tiles: offline, server error or nothing to show.</summary>
    private UIElement State(string icon, string title, string detail, string tone, bool retry)
    {
        var brush = Ui.B(tone);
        FrameworkElement chip;
        if (tone == "TextMuted")
        {
            chip = Ui.IconChip(icon, brush, Ui.B("SelectedBg"), 84, 40);
        }
        else
        {
            // Connection problems: the Webin Cloud Server logo with a coloured icon (error / offline) on its corner.
            chip = WebinCloudArt.Create(icon, brush, 96);
        }
        chip.HorizontalAlignment = HorizontalAlignment.Center;
        var heading = new TextBlock { Text = title, FontFamily = Ui.Res<FontFamily>("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 26,
            Foreground = tone == "TextMuted" ? Ui.B("Text") : brush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        var text = Ui.Text(detail, "Body", Ui.B("TextMuted"));
        text.TextAlignment = TextAlignment.Center;
        text.TextWrapping = TextWrapping.Wrap;
        text.MaxWidth = 520;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.Margin = new Thickness(0, 8, 0, 0);
        var panel = Ui.Stack(chip, heading, text);
        if (retry)
        {
            var again = new Button { Style = Ui.Res<Style>("SecondaryButton"), Content = IconLabel("refresh-cw", "RETRY"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0), IsEnabled = !_loading };
            System.Windows.Automation.AutomationProperties.SetName(again, "RETRY");
            again.Click += async (_, _) => await LoadOnlineAsync();
            panel.Children.Add(again);
        }
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Margin = new Thickness(0, 110, 0, 0);
        return panel;
    }

    // ── Actions ───────────────────────────────────────────────────────────────────────────

    private async Task DownloadAsync(AppEntry entry, bool confirmed = false)
    {
        // Only while the server is answering: the catalog is dropped as soon as a refresh fails.
        if (_online?.Any(a => a.Key == entry.Key) != true)
            return;
        if (!confirmed && !string.IsNullOrWhiteSpace(entry.Message)
            && !DurazznoDialog.Confirm(Window.GetWindow(this), entry.Name, entry.Message.Trim(), "Download", icon: "triangle-alert", tone: "Warning"))
            return;

        _downloading.Add(entry.Key);
        void Show(double? percent)
        {
            if (!_progress.TryGetValue(entry.Key, out var p))
                return;
            p.Label.Text = percent is { } pct ? $"DOWNLOADING {pct:0}%" : "DOWNLOADING…";
            p.Label.Foreground = Ui.B("Success");
            p.Bar.Visibility = Visibility.Visible;
            p.Bar.ColumnDefinitions[0].Width = new GridLength(percent ?? 0, GridUnitType.Star);
            p.Bar.ColumnDefinitions[1].Width = new GridLength(100 - (percent ?? 0), GridUnitType.Star);
        }
        Show(0);
        try
        {
            var update = _installed.Any(a => a.Key == entry.Key);
            await AppCatalog.DownloadAsync(entry, new Progress<AppDownloadProgress>(p => Show(p.Percent)), CancellationToken.None);
            _app.Notify("Durazzno Apps", update ? $"{entry.Name} just got updated." : $"{entry.Name} just got installed.",
                System.Windows.Forms.ToolTipIcon.Info, Tray.NotifyLogo.Durazzno, () => _app.ShowMainWindow("apps"), Tray.NotifyTone.Success);
        }
        catch (Exception ex)
        {
            Log.Warn($"Apps: download of {entry.Name} failed: {ex.Message}");
            _downloading.Remove(entry.Key);
            _installed = AppCatalog.Installed();
            Render();
            ShowDownloadError(entry, ex);
            return;
        }
        finally
        {
            _downloading.Remove(entry.Key);
        }
        _installed = AppCatalog.Installed();
        Render();
    }

    /// <summary>A failed download: one popup with the Webin Cloud logo and what went wrong.</summary>
    private void ShowDownloadError(AppEntry entry, Exception ex)
    {
        if (ex is AppInUseException)
        {
            DurazznoDialog.Show(Window.GetWindow(this), $"{entry.Name} is running", $"Close {entry.Name} first, then download it again.",
                "triangle-alert", "Warning", [new DialogButton("Close", "PrimaryButton")]);
            return;
        }
        var (icon, tone, title, message) = ex switch
        {
            HttpRequestException { StatusCode: { } code } => ("triangle-alert", "Danger", "Server error",
                $"The server responded with code: {(int)code}.\nCould not download {entry.Name}."),
            InvalidDataException => ("shield-alert", "Danger", "Download damaged",
                $"{entry.Name} did not match the file published on Webin Cloud Server, so it was deleted. Try again later."),
            _ => ("wifi-off", "Warning", "Offline", $"Can't reach Webin Cloud Server to download {entry.Name}. Check your connection."),
        };
        DurazznoDialog.Show(Window.GetWindow(this), title, message, icon, tone, [new DialogButton("Close", "PrimaryButton")],
            art: WebinCloudArt.Create(icon, Ui.B(tone), 56));
    }

    private void OpenApp(InstalledApp installed)
    {
        if (AppCatalog.Open(installed) is { } error)
            DurazznoDialog.Show(Window.GetWindow(this), $"Could not open {installed.Name}", error, "triangle-alert", "Warning",
                [new DialogButton("Close", "PrimaryButton")]);
    }

    private void RemoveApp(InstalledApp installed)
    {
        if (!DurazznoDialog.Confirm(Window.GetWindow(this), $"Remove {installed.Name}?", $"Deletes {Path.GetDirectoryName(installed.FilePath)} and everything in it.",
                "Remove", icon: "trash-2", tone: "Danger", destructive: true))
            return;
        try
        {
            AppCatalog.Remove(installed);
        }
        catch (AppInUseException)
        {
            DurazznoDialog.Show(Window.GetWindow(this), $"{installed.Name} is running", $"Close {installed.Name} first, then remove it.",
                "triangle-alert", "Warning", [new DialogButton("Close", "PrimaryButton")]);
            return;
        }
        _installed = AppCatalog.Installed();
        Render();
    }

    private static StackPanel IconLabel(string icon, string text) => Ui.Row(
        new LucideIcon { Icon = icon, Width = 16, Height = 16, Foreground = Ui.B("Text") },
        new TextBlock { Text = text, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
}
