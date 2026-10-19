using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using Durazzno.Core;
using Durazzno.Hardware;
using Durazzno.Network;
using Durazzno.Tools;
using Durazzno.Tray;
using Durazzno.UI.Pages;

namespace Durazzno.UI;

/// <summary>
/// The main Durazzno App window: sidebar with Monitoring / Tools / System, page area on the right.
/// Closing it only hides it; the app keeps running in the tray.
/// </summary>
internal sealed partial class MainWindow : Window
{
    public const string DashboardPage = "dashboard";
    public const string SettingsPage = "settings";
    public const string PowerCutsPage = "powercuts";
    public const string AppsPage = "apps";

    private readonly AppController _app;
    private readonly Dictionary<string, RadioButton> _navItems = new();
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private string? _currentPage;
    private bool _exitRequested;

    public MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        Controls.WheelScroll.AttachPage(PageScroller); // proportional wheel / trackpad scrolling for every page

        var logo = Imaging.ToBitmapSource(IconFactory.Logo(app.Settings.Current.Logo));
        BrandLogo.Source = logo;
        Icon = logo;
        VersionBadge.Text = $"v{AppInfo.VersionText}";
        FooterVersion.Text = $"Version: {AppInfo.VersionText}";
        MachineText.Text = SystemInfo.MachineName;

        BuildNavigation();
        Navigate(DashboardPage);
        ApplyScale(fitToScreen: true);

        _app.StatusChanged += OnStatusChanged;
        StateChanged += (_, _) => OnWindowStateChanged();
        RefreshStatus();
    }

    /// <summary>Show, restore and focus the window, even when another app has the foreground.</summary>
    public void BringToFront()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void Navigate(string page)
    {
        if (!_pages.TryGetValue(page, out var view))
        {
            try
            {
                view = CreatePage(page);
            }
            catch (Exception ex)
            {
                // Show the failure instead of silently ignoring the click.
                Log.Error($"Could not open page '{page}'", ex);
                view = Ui.Card(Ui.Text($"This page could not be opened: {ex.Message}", "Body"));
            }

            if (view is null)
                return;
            _pages[page] = view;
        }

        _currentPage = page;
        if (_navItems.TryGetValue(page, out var item))
        {
            if (item.IsChecked != true)
                item.IsChecked = true;
        }
        else
        {
            // Settings lives in the title bar, not the sidebar: nothing is selected there.
            foreach (var other in _navItems.Values)
                other.IsChecked = false;
        }
        SettingsButton.Foreground = page == SettingsPage ? (Brush)FindResource("Accent") : (Brush)FindResource("TextMuted");

        _pageMinWidth = 0;
        UpdateMinWidth();
        PageHost.Content = view;
        PageScroller.ScrollToTop();
    }

    /// <summary>Really close (used when the app exits from the tray).</summary>
    public void CloseForExit()
    {
        _exitRequested = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            // The X sends the app back to the tray.
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _app.StatusChanged -= OnStatusChanged;
        foreach (var page in _pages.Values.OfType<IDisposable>())
            page.Dispose();
        base.OnClosed(e);
    }

    private FrameworkElement? CreatePage(string page) => page switch
    {
        DashboardPage => new DashboardPage(_app, Navigate),
        SettingsPage => new SettingsPage(_app),
        "diskinfo" => new DiskInfoPage(_app, ToolCatalog.Find("diskinfo")!),
        "diskmark" => new DiskMarkPage(_app, ToolCatalog.Find("diskmark")!),
        "diskmap" => new DiskMapPage(_app, ToolCatalog.Find("diskmap")!),
        "startuphub" => new StartupHubPage(_app, ToolCatalog.Find("startuphub")!),
        "timerresolution" => new TimerResolutionPage(_app, ToolCatalog.Find("timerresolution")!),
        "driverhub" => new DriverHubPage(_app, ToolCatalog.Find("driverhub")!),
        "wintweaks" => new WinTweaksPage(_app, ToolCatalog.Find("wintweaks")!),
        PowerCutsPage => new PowerCutsPage(_app),
        AppsPage => new Pages.AppsPage(_app),
        _ => null,
    };

    private void BuildNavigation()
    {
        AddSection("MONITORING");
        AddNavItem(DashboardPage, "Dashboard", "layout-dashboard", "#F5C400");

        AddSection("TOOLS");
        foreach (var tool in ToolCatalog.All)
            AddNavItem(tool.Id, tool.Name, tool.Icon, tool.Accent);
        AddNavItem(PowerCutsPage, Plugins.PowerCutsService.Name, "img:Plugins.powercuts", Plugins.PowerCutsService.Accent);

        AddSection("APPS");
        AddNavItem(AppsPage, "Apps", "package", Pages.AppsPage.Accent);

    }

    private void AddSection(string title) =>
        NavPanel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SectionLabel") });

    private void AddNavItem(string page, string text, string icon, string accent)
    {
        // Foreground colours the icon (each tool keeps its own colour); the label colour is set by the style.
        var item = new RadioButton { Content = text, Tag = icon, Foreground = Imaging.Brush(accent), Style = (Style)FindResource("NavItem") };
        item.Checked += (_, _) =>
        {
            if (_currentPage != page)
                Navigate(page);
        };
        _navItems[page] = item;
        NavPanel.Children.Add(item);
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        RefreshStatus();
        if (Math.Abs(_scale - Scale(_app.Settings.Current.UiSize)) > 0.001)
            ApplyScale(fitToScreen: false);
    }

    private double _scale = 1;

    internal static double Scale(UiSize size) => size switch
    {
        UiSize.Large => 1.0,
        UiSize.Normal => 0.9,
        _ => 0.8,
    };

    /// <summary>
    /// Scales the whole UI (vector, so it stays sharp). Designed at 100 %; the default 80 % fits a full page
    /// without scrolling on high-DPI laptop / 4K screens.
    /// </summary>
    private void ApplyScale(bool fitToScreen)
    {
        var previous = _scale;
        _scale = Scale(_app.Settings.Current.UiSize);
        RootGrid.LayoutTransform = _scale == 1 ? Transform.Identity : new ScaleTransform(_scale, _scale);
        DurazznoDialog.Scale = _scale;
        WindowChrome.GetWindowChrome(this).CaptionHeight = 56 * _scale;
        UpdateMinWidth();
        // The tallest page (Dashboard, ~985 tall at 100 %) plus title bar and page margins must fit without its cards
        // being cut off; never taller than the screen.
        MinHeight = Math.Min(1125 * _scale, SystemParameters.WorkArea.Height);

        if (fitToScreen)
        {
            var work = SystemParameters.WorkArea;
            Width = Math.Min(1560 * _scale, work.Width - 40);
            Height = Math.Min(1125 * _scale, work.Height - 40);
        }
        else if (WindowState == WindowState.Normal)
        {
            Width *= _scale / previous;
            Height *= _scale / previous;
        }
    }

    private double _pageMinWidth;

    /// <summary>
    /// A page whose rows must stay on one line (DriverHub's filters + switches) reports how wide its content needs to
    /// be; the window then cannot be made narrower than that. Reset when another page is shown.
    /// </summary>
    public void SetPageMinWidth(double contentWidth)
    {
        _pageMinWidth = contentWidth;
        UpdateMinWidth();
    }

    private void UpdateMinWidth()
    {
        // 1540 fits DriverHub's button row; sidebar 262 + page margins 96 + scrollbar/border slack 24 around a page's
        // own minimum. Never wider than the screen itself.
        var needed = Math.Max(1540, 262 + 96 + 24 + _pageMinWidth);
        MinWidth = Math.Min(needed * _scale, SystemParameters.WorkArea.Width);
    }

    private void RefreshStatus()
    {
        var connection = _app.Connection;
        var manifest = connection.Manifest;

        var (text, tone) = connection.State switch
        {
            ConnectionState.Online => ("ONLINE", "Success"),
            ConnectionState.ServerUnreachable => ("SERVER UNREACHABLE", "Warning"),
            ConnectionState.NoInternet => ("OFFLINE", "Warning"),
            _ => ("CONNECTING", "Info"),
        };
        StatusText.Text = text;
        StatusText.Foreground = StatusDot.Fill = (Brush)FindResource(tone);
        StatusPill.Background = (Brush)FindResource(tone + "Chip");

        if (_app.IsInstalling)
        {
            UpdatePill.Visibility = Visibility.Visible;
            UpdatePill.IsEnabled = false;
            UpdatePillText.Text = (_app.UpdateStatus ?? "UPDATING").ToUpperInvariant();
        }
        else if (manifest is { HasUpdate: true })
        {
            UpdatePill.Visibility = Visibility.Visible;
            UpdatePill.IsEnabled = true;
            UpdatePillText.Text = $"APPLY UPDATE {manifest.App.Version}";
        }
        else
        {
            UpdatePill.Visibility = Visibility.Collapsed;
        }
    }

    private void OnWindowStateChanged()
    {
        // With a custom chrome a maximized window overhangs the screen by the resize border; pad it back in.
        var maximized = WindowState == WindowState.Maximized;

        // Minimized counts as not on screen: pages stop their refresh timers (DiskInfo, Dashboard...) and rendering.
        PageHost.Visibility = WindowState == WindowState.Minimized ? Visibility.Collapsed : Visibility.Visible;
        var frame = SystemParameters.WindowResizeBorderThickness;
        RootBorder.Margin = maximized ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4) : new Thickness(0);
        RootBorder.BorderThickness = new Thickness(maximized ? 0 : 1);
        MaximizeButton.Tag = maximized ? "copy" : "square";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => Navigate(SettingsPage);

    private void OnWebsiteClick(object sender, RoutedEventArgs e) => Launcher.OpenUrl(AppInfo.WebsiteUrl);

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnUpdatePillClick(object sender, RoutedEventArgs e) => _ = _app.InstallUpdateAsync();
}
