using System.Drawing;
using System.Windows.Forms;
using Durazzno.Core;
using Durazzno.Hardware;
using Durazzno.Hardware.Disks;
using Durazzno.Hardware.Drivers;
using Durazzno.Network;
using Durazzno.Plugins;
using Durazzno.Tools;
using Durazzno.Tray;
using Durazzno.UI;
using Durazzno.Updates;
using TrayTheme = Durazzno.Tray.Theme;

namespace Durazzno;

/// <summary>
/// Owns the app's lifetime: tray icon + menu, connection and update logic, and the main window.
/// The main window only hides when closed; the app exits from the tray menu.
/// </summary>
internal sealed class AppController : IDisposable
{
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _tray;
    private Icon? _trayIcon;
    private MainWindow? _window;
    private bool _exiting;

    private bool _userCheckPending;
    private bool _quietCheck;
    private string? _announcedUpdate;
    private Task? _staging;
    private Action? _balloonClick;

    private readonly SynchronizationContext _ui = SynchronizationContext.Current!;

    public AppController(SettingsStore settings, bool justUpdated, bool updateFailed = false)
    {
        Settings = settings;
        TimerResolution = new TimerResolutionService(settings);
        DurazznoDialog.Scale = MainWindow.Scale(settings.Current.UiSize);
        Connection = new ConnectivityMonitor(() => ServerClient.ResolveManifestUri(Settings.Current.ManifestUrl));

        _menu.Font = new Font("Segoe UI", 9f);
        _menu.Opening += (_, _) => BuildMenu();

        _tray = new NotifyIcon { ContextMenuStrip = _menu, Text = AppInfo.Name };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
        _tray.BalloonTipClicked += (_, _) => { var click = _balloonClick; _balloonClick = null; click?.Invoke(); };
        _tray.BalloonTipClosed += (_, _) => _balloonClick = null;

        DriverWatch = new DriverWatch(settings, (title, text) => Notify(title, text, ToolTipIcon.Info, NotifyLogo.DriverHub));

        PowerCuts.Warning += (title, text) => Notify(title, text, ToolTipIcon.Warning, NotifyLogo.PowerCuts,
            () => ShowMainWindow(MainWindow.PowerCutsPage));

        Connection.Changed += (_, _) => OnConnectionChanged();
        Settings.Changed += (_, _) => OnStatusChanged();

        RefreshTray();
        _tray.Visible = true;

        // Back on this version after the new one crashed on start (UpdateService.RunFinish). The update is still offered.
        if (updateFailed)
            Notify("Durazzno App Update", "We couldn't complete the Durazzno App update.", ToolTipIcon.Warning,
                onClick: () => ShowMainWindow(MainWindow.SettingsPage), tone: NotifyTone.Warning);
        else if (justUpdated)
            Notify($"Durazzno App updated to {AppInfo.VersionText}", WhatsNewSummary(UpdateService.NotesFor(AppInfo.VersionText)) ?? "The new version is installed.",
                tone: NotifyTone.Success);

        _ = Connection.RefreshAsync();
        _ = CheckDisksAtStartupAsync();
    }

    /// <summary>
    /// One S.M.A.R.T. check per start (normally when Windows boots): DiskInfo does not poll in the background, it only
    /// refreshes while its page is on screen. A disk in Caution or Bad health gets a DiskInfo notification. Runs
    /// unelevated, so it covers NVMe drives and anything readable without admin rights (most SATA SSDs/HDDs on
    /// standard controllers need the elevated agent DiskInfo starts on demand).
    /// </summary>
    private async Task CheckDisksAtStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45)); // let Windows finish starting first
            var disks = await Disks.ScanAsync();
            foreach (var change in SmartHistory.Record(disks))
                NotifyDiskChange(change);
            foreach (var d in disks.Where(d => !d.IsVirtual && d.Health is DiskHealth.Caution or DiskHealth.Bad))
            {
                var state = d.Health == DiskHealth.Bad ? "BAD" : "CAUTION";
                Log.Warn($"Startup SMART check: {d.Model} {state} ({d.HealthReason})");
                Notify($"{d.Model}: {state}",
                    (string.IsNullOrWhiteSpace(d.HealthReason) ? "S.M.A.R.T. reports a problem with this disk." : d.HealthReason)
                    + (d.Health == DiskHealth.Bad ? " Back up your files now." : " Open DiskInfo for details."),
                    ToolTipIcon.Warning, NotifyLogo.DiskInfo, () => ShowMainWindow("diskinfo"),
                    d.Health == DiskHealth.Bad ? NotifyTone.Error : NotifyTone.Warning);
                await Task.Delay(TimeSpan.FromSeconds(6)); // one notification at a time
            }
            Log.Info($"Startup SMART check: {disks.Count(d => !d.IsVirtual)} disks, {disks.Count(d => d.Health is DiskHealth.Caution or DiskHealth.Bad)} with problems");
        }
        catch (Exception ex)
        {
            Log.Warn($"Startup SMART check failed: {ex.Message}");
        }
    }

    /// <summary>A disk got worse since its previous reading (SmartHistory): one DiskInfo notification.</summary>
    public void NotifyDiskChange(SmartChange change)
    {
        Log.Warn($"SMART change: {change.Disk.Model}: {change.Text}");
        Notify($"{change.Disk.Model} is getting worse", $"{change.Text}. Open DiskInfo for details and keep your backups up to date.",
            ToolTipIcon.Warning, NotifyLogo.DiskInfo, () => ShowMainWindow("diskinfo"), change.Serious ? NotifyTone.Error : NotifyTone.Warning);
    }

    public SettingsStore Settings { get; }

    public ConnectivityMonitor Connection { get; }

    public DiskService Disks { get; } = new();

    public DiskMarkService DiskMark { get; } = new();

    public PowerCutsService PowerCuts { get; } = new();

    public TimerResolutionService TimerResolution { get; private set; } = null!;

    public DriverWatch DriverWatch { get; private set; } = null!;

    public bool IsInstalling { get; private set; }

    /// <summary>The newer version is downloaded and verified, waiting for the next start (or Apply update).</summary>
    public bool IsUpdateReady => Connection.Manifest is { } m && UpdateService.IsStaged(m);

    /// <summary>Progress text while an update is being installed.</summary>
    public string? UpdateStatus { get; private set; }

    /// <summary>Raised on the UI thread whenever connection, update or settings state changes.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>
    /// A check the user asked for. From the tray (notify) the result comes as a notification; the Settings page passes
    /// notify: false and shows the result next to its button instead.
    /// </summary>
    public Task CheckNowAsync(bool notify = true)
    {
        _userCheckPending = true;
        _quietCheck = !notify;
        return Connection.RefreshAsync();
    }

    /// <summary>
    /// Apply update (title bar button, Settings, tray): shows the progress card, downloads the update if it is not
    /// staged yet, installs it and restarts Durazzno with its window open.
    /// </summary>
    public async Task InstallUpdateAsync()
    {
        if (IsInstalling)
            return;
        // Check the server first: the update on offer may be stale (tray menu built minutes ago) or it may be offline.
        await Connection.RefreshAsync();
        var manifest = Connection.Manifest;
        if (manifest is null)
        {
            Notify("Durazzno App Update", "Can't reach Webin Cloud Server right now. Try again when you're online.", ToolTipIcon.Warning,
                NotifyLogo.WebinCloud, tone: NotifyTone.Warning);
            return;
        }
        if (manifest is not { IsAppNewer: true } || IsInstalling)
            return;

        IsInstalling = true;
        UpdateStatus = "Downloading…";
        OnStatusChanged();
        var window = new UpdateWindow(_window, manifest.App.Version);
        window.Show();
        var version = manifest.App.Version;
        var progress = new Progress<UpdateProgress>(p =>
        {
            var text = p.Phase == "installing" ? "Installing…"
                : $"Downloading Durazzno App {version} from Webin Cloud Server" + (p.Percent is { } pct ? $"  ·  {pct:0}%" : "…");
            window.Report(text, p.Phase == "installing" ? null : p.Percent ?? 0);
            UpdateStatus = p.Phase == "installing" ? "Installing…" : $"Downloading {p.Percent ?? 0:0}%";
            OnStatusChanged();
        });

        try
        {
            if (_staging is { IsCompleted: false })
                await _staging; // a background download is already running: finish it first
            await UpdateService.StageAsync(manifest, progress, CancellationToken.None);
            ((IProgress<UpdateProgress>)progress).Report(new UpdateProgress("installing", null));
            await Task.Delay(400); // let the card show "Installing…"
            UpdateService.HandOff(open: true);
            window.Close();
            ForceExit();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            window.Fail(ex.Message);
            IsInstalling = false;
            UpdateStatus = null;
            OnStatusChanged();
        }
    }

    /// <summary>Background download of a new version (Install updates automatically): ready for the next start.</summary>
    private void StageInBackground(UpdateManifest manifest)
    {
        if (_staging is { IsCompleted: false } || UpdateService.IsStaged(manifest))
            return;
        _staging = Task.Run(async () =>
        {
            try
            {
                await UpdateService.StageAsync(manifest, null, CancellationToken.None);
                _ui.Post(_ =>
                {
                    Notify($"Durazzno App {manifest.App.Version} is ready",
                        "It installs the next time Durazzno starts. Click Apply update to install it now.", ToolTipIcon.Info, NotifyLogo.Durazzno,
                        () => _ = InstallUpdateAsync(), NotifyTone.Success);
                    OnStatusChanged();
                }, null);
            }
            catch (Exception ex)
            {
                Log.Warn($"Background update download failed: {ex.Message}"); // retried at the next manifest check
            }
        });
    }

    /// <summary>Shows the main window (creating it on first use), optionally on a given page, and brings it to the front.</summary>
    public void ShowMainWindow(string? page = null)
    {
        if (_exiting)
            return;

        _window ??= CreateWindow();
        if (page is not null)
            _window.Navigate(page);

        _window.BringToFront();
    }

    /// <summary>Tray notification; <paramref name="logo"/> picks the picture (PowerCuts, DiskInfo, Webin Cloud...).</summary>
    public void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, NotifyLogo logo = NotifyLogo.Durazzno, Action? onClick = null,
        NotifyTone? tone = null)
    {
        if (_exiting || !_tray.Visible)
            return;
        _balloonClick = onClick;
        Notifier.Show(_tray, title, text, icon, logo, tone ?? icon switch
        {
            ToolTipIcon.Error => NotifyTone.Error,
            ToolTipIcon.Warning => NotifyTone.Warning,
            _ => NotifyTone.Default,
        });
    }

    /// <summary>A tool page's notification, sent as that tool (its name and icon head the toast).</summary>
    public void NotifyTool(ToolDefinition tool, string text, ToolTipIcon icon = ToolTipIcon.Info) =>
        Notify(text, "", icon, Notifier.ForTool(tool.Id));

    public void Exit()
    {
        if (_exiting || !ConfirmExitWithPowerCuts())
            return;
        ForceExit();
    }

    /// <summary>Exits without asking (an update is restarting the app; PowerCuts' deadline is saved and resumes).</summary>
    private void ForceExit()
    {
        if (_exiting)
            return;

        _exiting = true;
        _tray.Visible = false;
        _window?.CloseForExit();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// PowerCuts counts down inside Durazzno App until its last 30 seconds, so exiting would silently stop it: ask first.
    /// Returns false to stay open.
    /// </summary>
    private bool ConfirmExitWithPowerCuts()
    {
        if (!PowerCuts.IsActive || PowerCuts.HandedToWindows)
            return true;

        var verb = PowerCuts.Action == PowerAction.Restart ? "restart" : "shut down";
        var choice = DurazznoDialog.Show(_window, "PowerCuts is running",
            $"This PC will {verb} in {PowerCutsService.Format(PowerCuts.Remaining)}. PowerCuts counts down inside Durazzno App, so closing it stops the countdown.",
            "power", "Warning",
            [
                new DialogButton("Keep Durazzno running", "PrimaryButton"),
                new DialogButton($"Exit, {verb} on time"),
                new DialogButton("Exit and cancel", "DangerButton"),
            ],
            [
                $"Exit, {verb} on time: Windows takes over and still {verb}s at the same moment, without the 5 and 1 minute warnings.",
                "Exit and cancel: the scheduled " + (PowerCuts.Action == PowerAction.Restart ? "restart" : "shutdown") + " is cancelled.",
            ]);

        switch (choice)
        {
            case 1:
                if (PowerCuts.HandOffNow() is { } error)
                {
                    DurazznoDialog.Show(_window, "PowerCuts", error, "triangle-alert", "Danger", [new DialogButton("OK", "PrimaryButton")]);
                    return false;
                }
                return true;
            case 2:
                PowerCuts.Cancel();
                return true;
            default:
                return false;
        }
    }

    private MainWindow CreateWindow()
    {
        var window = new MainWindow(this);
        // Open window: connection checked now and every 15 s; hidden or minimized (tray): every 5 minutes.
        void Track() => Connection.SetWindowOpen(window.IsVisible && window.WindowState != System.Windows.WindowState.Minimized);
        window.IsVisibleChanged += (_, _) => Track();
        window.StateChanged += (_, _) => Track();
        window.Closed += (_, _) =>
        {
            _window = null;
            Connection.SetWindowOpen(false);
        };
        return window;
    }

    private void OnConnectionChanged()
    {
        var userInitiated = _userCheckPending;
        var quiet = userInitiated && _quietCheck; // the Settings page shows the result itself
        _userCheckPending = false;
        _quietCheck = false;

        OnStatusChanged();

        var manifest = Connection.Manifest;
        // Offline is shown by the tray icon's orange dot, not a notification (a laptop waking up before its Wi-Fi
        // connects would get one every time).
        if (manifest is null)
            return;
        if (manifest.App.Version == AppInfo.VersionText || manifest.IsAppNewer)
            UpdateService.SaveNotes(manifest.App); // What's new (Settings) for this version or the next one

        if (!manifest.HasUpdate)
        {
            if (userInitiated && !quiet)
                Notify("You're up to date", $"Durazzno App {AppInfo.VersionText} is the latest version.");
            return;
        }

        if (IsInstalling)
            return;

        if (Settings.Current.AutoUpdate)
        {
            StageInBackground(manifest); // notifies when ready
        }
        else if (quiet)
        {
            _announcedUpdate = manifest.App.Version; // seen in Settings: no notification for it later
        }
        else if (userInitiated || _announcedUpdate != manifest.App.Version)
        {
            _announcedUpdate = manifest.App.Version;
            Notify("Durazzno App Update", $"A new version is available: {manifest.App.Version}" + (WhatsNewSummary(manifest.App.Notes) is { } news ? $"\n{news}" : ""),
                ToolTipIcon.Info, NotifyLogo.Durazzno, () => _ = InstallUpdateAsync());
        }
        OnStatusChanged();
    }

    private void OnStatusChanged()
    {
        if (_exiting)
            return;

        RefreshTray();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshTray()
    {
        var settings = Settings.Current;
        var manifest = Connection.Manifest;

        // The logo always stays; a coloured dot carries the status:
        // red = no internet, orange = internet but Webin Cloud Server is offline, blue = update available / installing.
        var offline = Connection.State is ConnectionState.NoInternet or ConnectionState.ServerUnreachable;
        Color? badge = Connection.State == ConnectionState.NoInternet ? TrayTheme.Danger
            : Connection.State == ConnectionState.ServerUnreachable ? TrayTheme.OfflineBadge
            : IsInstalling || manifest is { HasUpdate: true } ? TrayTheme.Info
            : null;
        var icon = IconFactory.TrayIcon(IconFactory.Logo(settings.Logo), badge);
        var status = offline ? ConnectionDescription()
            : IsInstalling ? UpdateStatus ?? "Updating…"
            : Connection.State == ConnectionState.Checking ? "Connecting…"
            : manifest is { HasUpdate: true } ? $"Update available: {manifest.App.Version}"
            : "Online";

        var previous = _trayIcon;
        _tray.Icon = _trayIcon = icon;
        previous?.Dispose();

        var text = $"{AppInfo.Name} {AppInfo.VersionText}\n{status}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private void BuildMenu()
    {
        var settings = Settings.Current;
        var palette = TrayTheme.For(settings.Theme);
        var iconSize = (int)Math.Round(16 * _menu.DeviceDpi / 96.0);

        _menu.Renderer = new MenuRenderer(palette);
        _menu.BackColor = palette.Background;
        _menu.ForeColor = palette.Text;
        _menu.ImageScalingSize = new Size(iconSize, iconSize);

        Image MenuIcon(string name, Color accent) =>
            IconFactory.Lucide(name, settings.IconStyle == IconStyle.Colorful ? accent : palette.Text, iconSize);

        void Add(string text, Image image, Action? onClick, bool enabled = true, bool bold = false)
        {
            var item = new ToolStripMenuItem(text, image) { Enabled = enabled, Padding = new Padding(0, 3, 0, 3) };
            if (bold)
                item.Font = new Font(_menu.Font, FontStyle.Bold);
            if (onClick is not null)
                item.Click += (_, _) => onClick();
            _menu.Items.Add(item);
        }

        foreach (var old in _menu.Items.Cast<ToolStripItem>().ToList())
            old.Dispose();
        _menu.Items.Clear();

        var state = Connection.State;
        var manifest = Connection.Manifest;

        // Version and connection status live in Settings → Updates (and the tray tooltip), not in the menu.
        Add("Open Durazzno App", MenuIcon("app-window", TrayTheme.Info), () => ShowMainWindow(), bold: true);

        // The connection is watched automatically (no "Retry"): the menu only offers what can be done now.
        if (IsInstalling)
            Add(UpdateStatus ?? "Updating…", MenuIcon("loader-circle", TrayTheme.Info), null, enabled: false);
        else if (manifest is { IsAppNewer: true })
            Add($"Apply update {manifest.App.Version}", MenuIcon("download", TrayTheme.Info), () => _ = InstallUpdateAsync(), bold: true);
        else if (state == ConnectionState.Online)
            Add("Check for updates", MenuIcon("refresh-cw", TrayTheme.Success), () => _ = CheckNowAsync());

        _menu.Items.Add(new ToolStripSeparator());

        foreach (var tool in ToolCatalog.All)
            Add(tool.Name, MenuIcon(tool.Icon, ColorTranslator.FromHtml(tool.Accent)), () => ShowMainWindow(tool.Id));

        Add(PowerCutsService.Name, settings.IconStyle == IconStyle.Colorful
                ? IconFactory.Embedded("Plugins.powercuts", iconSize)
                : IconFactory.EmbeddedTinted("Plugins.powercuts", iconSize, palette.Text),
            () => ShowMainWindow(MainWindow.PowerCutsPage));
        if (PowerCuts.IsActive)
        {
            var verb = PowerCuts.Action == PowerAction.Restart ? "Restart" : "Shutdown";
            Add($"{verb} in {PowerCutsService.Format(PowerCuts.Remaining)}  ·  Cancel", MenuIcon("x", TrayTheme.Danger), PowerCuts.Cancel);
        }

        _menu.Items.Add(new ToolStripSeparator());

        Add("Settings", MenuIcon("settings", TrayTheme.Neutral), () => ShowMainWindow(MainWindow.SettingsPage));
        Add("Exit", MenuIcon("power", TrayTheme.Danger), Exit);
    }

    /// <summary>Release notes trimmed for a notification (they show in full in Settings).</summary>
    private static string? WhatsNewSummary(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return null;
        var text = string.Join(' ', notes.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= 180 ? text : text[..177].TrimEnd() + "…";
    }

    public string ConnectionDescription() => Connection.State switch
    {
        ConnectionState.NoInternet => "Offline: no internet connection.",
        ConnectionState.ServerUnreachable => "Offline: Webin Cloud Server is unreachable.",
        ConnectionState.Checking => "Connecting…",
        _ => "Online.",
    };


    public void Dispose()
    {
        Connection.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _trayIcon?.Dispose();
    }
}
