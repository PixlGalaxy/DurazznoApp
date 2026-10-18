using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Durazzno.Core;
using Durazzno.Network;
using Durazzno.Tools;

namespace Durazzno.UI.Pages;

/// <summary>Settings and update status. Every change is saved and applied immediately.</summary>
internal sealed class SettingsPage : UserControl, IDisposable
{
    private readonly AppController _app;
    private readonly List<Action> _reload = [];
    private readonly TextBlock _connection = Value();
    private readonly TextBlock _latest = Value();
    private readonly TextBlock _whatsNew = Value();
    private readonly TextBlock _whatsNewLabel = Ui.Text("What's new", "CardLabel");
    private readonly Grid _whatsNewRow;
    private readonly TextBlock _checkResult = new() { FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _check;
    private readonly Button _install;
    private bool _loading;

    public SettingsPage(AppController app)
    {
        _app = app;
        var s = () => _app.Settings.Current;

        var tray = Ui.Stack(Ui.PanelTitle("TRAY MENU"),
            SettingRow("Menu icons", Segments("icons", () => (int)s().IconStyle,
                i => _app.Settings.Update(x => x.IconStyle = (IconStyle)i), "Colorful", "Monochrome")),
            SettingRow("Menu theme", Segments("theme", () => (int)s().Theme,
                i => _app.Settings.Update(x => x.Theme = (MenuTheme)i), "Follow Windows", "Light", "Dark")),
            SettingRow("Tray logo", Segments("logo", () => (int)s().Logo,
                i => _app.Settings.Update(x => x.Logo = (LogoKind)i), "Durazzno", "Galaxy")));

        var window = Ui.Stack(Ui.PanelTitle("WINDOW"),
            SettingRow("Interface size", Segments("uisize", () => (int)s().UiSize,
                i => _app.Settings.Update(x => x.UiSize = (UiSize)i), "Compact", "Normal", "Large")));

        var startup = Toggle("Start Durazzno App with Windows", () => StartupManager.IsEnabled, StartupManager.SetEnabled);
        var autoUpdate = Toggle("Download and install updates automatically", () => s().AutoUpdate, v => _app.Settings.Update(x => x.AutoUpdate = v));
        var general = Ui.Stack(Ui.PanelTitle("GENERAL"), startup, autoUpdate);
        window.Margin = new Thickness(0, 0, 0, 0);

        _check = new Button { Content = "CHECK FOR UPDATES", Style = Ui.Res<Style>("SecondaryButton") };
        _check.Click += async (_, _) => await CheckAsync();
        _install = new Button { Content = "APPLY UPDATE", Style = Ui.Res<Style>("PrimaryButton"), Margin = new Thickness(10, 0, 0, 0) };
        _install.Click += async (_, _) => await _app.InstallUpdateAsync();
        var buttons = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        var buttonRow = Ui.Row(_check, _install);
        DockPanel.SetDock(buttonRow, Dock.Left);
        buttons.Children.Add(buttonRow);
        buttons.Children.Add(_checkResult);

        var updates = Ui.Stack(Ui.PanelTitle("UPDATES"),
            SettingRow("Installed", Value($"Durazzno App {AppInfo.VersionText}")),
            SettingRow("Latest", _latest),
            SettingRow("Connection", ServerStatus()),
            _whatsNewRow = NotesRow(),
            buttons);

        var openData = new Button { Content = "OPEN DATA FOLDER", Style = Ui.Res<Style>("SecondaryButton") };
        openData.Click += (_, _) => Launcher.OpenFolder(AppInfo.DataDir);
        var reset = new Button { Content = "RESET TO DEFAULTS", Style = Ui.Res<Style>("SecondaryButton"), Margin = new Thickness(10, 0, 0, 0) };
        reset.Click += (_, _) => ResetToDefaults();
        var dataButtons = Ui.Row(openData, reset);

        // Recovery console shipped next to the app (repair, rollback, reset... even when the app won't start).
        var terminalExe = Path.Combine(AppInfo.BaseDir, "DurazznoTerminal.exe");
        if (File.Exists(terminalExe))
        {
            var terminal = new Button { Content = "OPEN TERMINAL", Style = Ui.Res<Style>("SecondaryButton"), Margin = new Thickness(10, 0, 0, 0) };
            terminal.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(terminalExe) { UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) { Log.Warn($"Could not open Durazzno Terminal: {ex.Message}"); }
            };
            dataButtons.Children.Add(terminal);
        }
        dataButtons.Margin = new Thickness(0, 20, 0, 0);
        var dataText = Ui.Text($"Settings and logs are stored in {AppInfo.DataDir}", "Body", Ui.B("TextMuted"));
        dataText.Margin = new Thickness(0, 16, 0, 0);
        var data = Ui.Stack(Ui.PanelTitle("DATA"), dataText, dataButtons);

        var grid = Ui.Columns(2, 20);
        grid.Margin = new Thickness(0, 28, 0, 0);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Ui.Card(tray).At(0, 0));
        grid.Children.Add(Ui.Card(updates).At(2, 0));
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Ui.Card(general).At(0, 2));
        grid.Children.Add(Ui.Card(data).At(2, 2));
        grid.Children.Add(Ui.Card(window).At(0, 4));

        Content = Ui.Stack(
            Ui.Text("Settings", "PageTitle"),
            Ui.Text("Changes are saved and applied right away.", "PageSubtitle"),
            grid);

        Reload();
        _app.StatusChanged += OnStatusChanged;
        RefreshStatus();
    }

    public void Dispose() => _app.StatusChanged -= OnStatusChanged;

    private void OnStatusChanged(object? sender, EventArgs e) => RefreshStatus();

    /// <summary>CHECK FOR UPDATES: the result is shown next to the button, not as a notification.</summary>
    private async Task CheckAsync()
    {
        _check.IsEnabled = false;
        ShowResult("Checking…", "TextMuted");
        try
        {
            await _app.CheckNowAsync(notify: false);
        }
        finally
        {
            RefreshStatus();
        }

        var c = _app.Connection;
        var m = c.Manifest;
        if (m is null)
            ShowResult(_app.ConnectionDescription(), "Danger");
        else if (m.IsAppNewer)
            ShowResult(_app.IsUpdateReady || _app.Settings.Current.AutoUpdate
                ? $"Version {m.App.Version} found, it installs on the next start."
                : $"Version {m.App.Version} is available.", "Accent");
        else
            ShowResult("You're on the latest version.", "Success");
    }

    private void ShowResult(string text, string brush)
    {
        _checkResult.Text = text;
        _checkResult.Foreground = Ui.B(brush);
    }

    private void RefreshStatus()
    {
        var c = _app.Connection;
        var m = c.Manifest;
        var checkedAt = c.LastChecked is { } t ? $"  ·  checked {t:HH:mm}" : "";

        var (status, brush) = c.State switch
        {
            ConnectionState.Online => ("Online (Webin Cloud Server)", "Success"),
            ConnectionState.ServerUnreachable => ("Webin Cloud Server unreachable", "Danger"),
            ConnectionState.NoInternet => ("No internet connection", "Danger"),
            _ => ("Connecting…", "Warning"),
        };
        _connection.Text = status + checkedAt;
        _connection.Foreground = Ui.B(brush);

        _latest.Text = _app.IsInstalling ? _app.UpdateStatus ?? "Updating…"
            : m is null ? "Unknown while offline"
            : m.IsAppNewer && _app.IsUpdateReady ? $"{m.App.Version}  (downloaded, installs on next start)"
            : m.IsAppNewer ? $"{m.App.Version}  (update available)"
            : m.IsCurrentVersionSupported ? $"{m.App.Version}  (up to date)"
            : $"{m.App.Version}  (this version is no longer supported)";

        // What's new: the next version's notes when one is available, otherwise the installed version's.
        var notes = m is { IsAppNewer: true } ? m.App.Notes
            : Updates.UpdateService.NotesFor(AppInfo.VersionText) ?? (m?.App.Version == AppInfo.VersionText ? m.App.Notes : null);
        _whatsNew.Text = notes?.Trim() ?? "";
        _whatsNewLabel.Text = m is { IsAppNewer: true } ? $"New in {m.App.Version}" : "What's new";
        _whatsNewRow.Visibility = string.IsNullOrWhiteSpace(notes) ? Visibility.Collapsed : Visibility.Visible;

        _check.IsEnabled = !_app.IsInstalling;
        _install.IsEnabled = !_app.IsInstalling && m is { HasUpdate: true };
    }

    private void Reload()
    {
        _loading = true;
        foreach (var reload in _reload)
            reload();
        _loading = false;
    }

    private void ResetToDefaults()
    {
        if (!DurazznoDialog.Confirm(Window.GetWindow(this), "Reset settings?",
                "Every Durazzno App setting goes back to its default, and Durazzno starts with Windows again.",
                "Reset", icon: "rotate-ccw", tone: "Warning", destructive: true))
            return;

        _app.Settings.Reset();
        StartupManager.SetEnabled(true);
        Reload();
    }

    private UIElement Segments(string group, Func<int> current, Action<int> apply, params string[] labels)
    {
        var row = new WrapPanel();
        var buttons = labels.Select((label, i) =>
        {
            var b = new RadioButton { Content = label, GroupName = $"{group}-{GetHashCode()}", Style = Ui.Res<Style>("Segment"), Margin = new Thickness(0, 0, 0, 6) };
            b.Checked += (_, _) =>
            {
                if (!_loading)
                    apply(i);
            };
            row.Children.Add(b);
            return b;
        }).ToList();

        _reload.Add(() => buttons[Math.Clamp(current(), 0, buttons.Count - 1)].IsChecked = true);
        return row;
    }

    private CheckBox Toggle(string text, Func<bool> current, Action<bool> apply)
    {
        var toggle = new CheckBox { Content = text, Style = Ui.Res<Style>("ToggleSwitch"), Margin = new Thickness(0, 20, 0, 0) };
        toggle.Checked += (_, _) => { if (!_loading) apply(true); };
        toggle.Unchecked += (_, _) => { if (!_loading) apply(false); };
        _reload.Add(() => toggle.IsChecked = current());
        return toggle;
    }

    private static UIElement SettingRow(string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var text = Ui.Text(label, "CardLabel");
        text.Margin = new Thickness(0, 4, 0, 0);
        grid.Children.Add(text);
        grid.Children.Add(control.At(1));
        return grid;
    }

    /// <summary>The connection text with the Webin Cloud Server logo on its left.</summary>
    private UIElement ServerStatus()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Image
        {
            Source = Imaging.Embedded("Plugins.webincloud"),
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 3, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Webin Cloud Server",
        });
        row.Children.Add(_connection);
        return row;
    }

    private Grid NotesRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        _whatsNewLabel.Margin = new Thickness(0, 4, 0, 0);
        _whatsNew.TextWrapping = TextWrapping.Wrap;
        grid.Children.Add(_whatsNewLabel);
        grid.Children.Add(_whatsNew.At(1));
        return grid;
    }

    private static TextBlock Value(string text = "")
    {
        var block = Ui.Text(text, "Body");
        block.Margin = new Thickness(0, 4, 0, 0);
        return block;
    }
}
