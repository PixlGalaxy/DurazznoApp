using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Durazzno.UI.Controls;
using Path = System.Windows.Shapes.Path;

namespace Durazzno.Installer;

/// <summary>Lucide icon outlines (24×24, stroke 2), the same set Durazzno App draws.</summary>
internal static class Icons
{
    private const string Circle = "M2,12 A10,10 0 1 1 22,12 A10,10 0 1 1 2,12";
    public const string Check = Circle + " M16,9 L10.5,14.5 L8,12";
    public const string Alert = Circle + " M12,8 L12,12 M12,16 L12.01,16";
    public const string Pending = Circle;
    public const string Loader = "M21,12 A9,9 0 1 1 14.781,3.44";
    public const string Close = "M18,6 L6,18 M6,6 L18,18";
    public const string Minimize = "M5,12 L19,12";
}

/// <summary>
/// The installer's one window: Welcome → Checking Webin Cloud Server → Installing → Done, in Durazzno App's style
/// (dark panels with a cut corner, Rajdhani headings, yellow accent).
/// </summary>
internal sealed class InstallerWindow : Window
{
    private readonly ContentControl _body = new() { Focusable = false };
    private readonly StackPanel _footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private CancellationTokenSource? _work;
    private bool _copying; // the exe is being replaced: closing now could leave no working copy

    public InstallerWindow()
    {
        Title = "Durazzno App Installer";
        Width = 680;
        Height = 560;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B("WindowBg");
        Foreground = B("Text");
        FontFamily = (FontFamily)FindResource("BodyFont");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Icon = LoadLogo(32);
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 48,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(TitleBar());
        _body.Margin = new Thickness(40, 26, 40, 0);
        Grid.SetRow(_body, 1);
        grid.Children.Add(_body);
        _footer.Margin = new Thickness(40, 0, 40, 30);
        Grid.SetRow(_footer, 2);
        grid.Children.Add(_footer);
        Content = new Border { BorderBrush = B("PanelBorder"), BorderThickness = new Thickness(1), Child = grid };

        Closing += (_, e) =>
        {
            if (_copying)
                e.Cancel = true; // a few seconds: the old exe is gone and the new one is not in place yet
            else
                _work?.Cancel();
        };
        ShowStart();
    }

    /// <summary>Nothing installed: the install flow. Durazzno App in the folder: Repair or Uninstall.</summary>
    private void ShowStart()
    {
        if (Installer.IsInstalled)
            ShowInstalled();
        else
            ShowWelcome();
    }

    // ── Pages ───────────────────────────────────────────────────────────────────────────

    private void ShowWelcome()
    {
        var logo = new Image { Source = LoadLogo(256), Width = 76, Height = 76, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var location = Card(Stack(
            Text("INSTALL LOCATION", "CardLabel"),
            Mono(Installer.InstallDir, 6),
            Spaced(Text("Desktop and Start Menu shortcuts are added. Durazzno starts with Windows; you can turn that off in its Settings.", "Body"), 10)));
        Show(Stack(logo,
                Spaced(Text("Durazzno App Installer", "PageTitle"), 16),
                Spaced(Text("Gets Durazzno App from Webin Cloud Server, makes sure it's the signed release and installs it.", "Body"), 6),
                Spaced(location, 22)),
            Button("CANCEL", "SecondaryButton", Close),
            Button("NEXT", "PrimaryButton", () => _ = CheckAsync()));
    }

    private void ShowInstalled()
    {
        var logo = new Image { Source = LoadLogo(256), Width = 76, Height = 76, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        Show(Stack(logo,
                Spaced(Text("Durazzno App is installed", "PageTitle"), 16),
                Spaced(Text("Repair downloads Durazzno App again and puts it back in place, keeping your settings and apps. "
                    + "Uninstall removes it from this PC.", "Body"), 6),
                Spaced(Card(Stack(Text("INSTALLED IN", "CardLabel"), Mono(Installer.InstallDir, 6))), 22)),
            Button("CANCEL", "SecondaryButton", Close),
            Button("UNINSTALL", "SecondaryButton", ShowConfirmUninstall),
            Button("REPAIR", "PrimaryButton", () => _ = CheckAsync()));
    }

    private void ShowConfirmUninstall()
    {
        var removed = Card(Stack(
            Text("THIS REMOVES", "CardLabel"),
            Spaced(Text("•  Durazzno App and every app you downloaded through it", "Body"), 8),
            Text("•  Its shortcuts and its \"start with Windows\" entries", "Body"),
            Text("•  Its settings, logs and disk health history", "Body"),
            Spaced(Text("Driver backups in Documents\\Durazzno App are kept.", "CardHint"), 10)));
        Show(Stack(Glyph(Icons.Alert, B("Warning"), 58),
                Spaced(Text("Uninstall Durazzno App?", "PageTitle"), 16),
                Spaced(removed, 22)),
            Button("CANCEL", "SecondaryButton", ShowInstalled),
            Button("UNINSTALL", "DangerButton", () => _ = UninstallAsync()));
    }

    private async Task UninstallAsync()
    {
        var close = new StatusRow("Close Durazzno App", "Waiting…");
        var links = new StatusRow("Shortcuts and startup", "Waiting…");
        var files = new StatusRow("Files", "Waiting…");
        Show(Stack(Text("Uninstalling Durazzno App", "PageTitle"),
                Spaced(Card(Stack(close, Spaced(links, 12), Spaced(files, 12))), 22)));

        StatusRow current = close;
        try
        {
            _copying = true; // no closing halfway
            close.Busy("Closing Durazzno and its apps…");
            await Task.Run(Installer.CloseEverything);
            close.Done("Closed");

            current = links;
            links.Busy("Removing…");
            await Task.Run(Installer.RemoveShortcutsAndStartup);
            links.Done("Removed");

            current = files;
            files.Busy("Deleting the app, its apps and its settings…");
            await Task.Run(Installer.DeleteFiles);
            files.Done("Deleted");
            _copying = false;

            await Task.Delay(400);
            Show(Stack(Glyph(Icons.Check, B("Success"), 58),
                    Spaced(Text("Durazzno App was uninstalled", "PageTitle"), 16),
                    Spaced(Text("Everything it installed is gone. Thanks for using it.", "Body"), 6)),
                Button("CLOSE", "PrimaryButton", Close));
        }
        catch (Exception ex)
        {
            _copying = false;
            current.Failed(ex.Message);
            SetFooter(Button("CLOSE", "SecondaryButton", Close), Button("TRY AGAIN", "PrimaryButton", () => _ = UninstallAsync()));
        }
    }

    private async Task CheckAsync()
    {
        var server = new StatusRow("Webin Cloud Server", "Connecting…");
        var signed = new StatusRow("Signed release", "Waiting…");
        var file = new StatusRow("Durazzno App download", "Waiting…");
        var rows = Card(Stack(server, Spaced(signed, 16), Spaced(file, 16)));
        var note = new ContentControl { Focusable = false };
        var cancel = Button("CANCEL", "SecondaryButton", Close);
        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/webincloud.png")), Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        Show(Stack(logo,
                Spaced(Text("Checking Webin Cloud Server", "PageTitle"), 14),
                Spaced(Text("Making sure the server is online and Durazzno App is there before anything is installed.", "Body"), 6),
                Spaced(rows, 22), note),
            cancel);

        _work = new CancellationTokenSource();
        var ct = _work.Token;
        StatusRow current = server;
        try
        {
            server.Busy();
            var (manifest, signature) = await Installer.ReachServerAsync(ct);
            server.Done("Online");

            current = signed;
            signed.Busy();
            var release = Installer.ReadRelease(manifest, signature);
            signed.Done($"Durazzno App {release.Version}, signed by Galaxy Apps");

            current = file;
            file.Busy("Looking for DurazznoApp.exe…");
            await Installer.CheckExeAsync(release, ct);
            file.Done($"DurazznoApp.exe is available  ·  {Megabytes(release.Size)}");

            var running = Installer.RunningCopy();
            running?.Dispose();
            if (running is not null)
                note.Content = Spaced(Chip("Durazzno App is running from the install folder. It's closed while the new copy goes in.", "Warning"), 14);
            SetFooter(cancel, Button(running is null ? "INSTALL" : "CLOSE AND INSTALL", "PrimaryButton", () => _ = InstallAsync(release)));
        }
        catch (OperationCanceledException)
        {
            // the window is closing
        }
        catch (Exception ex)
        {
            current.Failed(ex is InstallException ? ex.Message : $"Unexpected error: {ex.Message}");
            SetFooter(cancel, Button("RETRY", "PrimaryButton", () => _ = CheckAsync()));
        }
    }

    private async Task InstallAsync(Release release)
    {
        var bar = new ProgressLine();
        var download = new StatusRow("Download", "Waiting…");
        var verify = new StatusRow("SHA-256 check", "Waiting…");
        var copy = new StatusRow("Install", "Waiting…");
        var shortcuts = new StatusRow("Shortcuts", "Waiting…");
        var cancel = Button("CANCEL", "SecondaryButton", () => _work?.Cancel());
        Show(Stack(Text("Installing Durazzno App", "PageTitle"),
                Spaced(bar, 22),
                Spaced(Card(Stack(download, Spaced(verify, 12), Spaced(copy, 12), Spaced(shortcuts, 12))), 18)),
            cancel);

        _work = new CancellationTokenSource();
        var ct = _work.Token;
        StatusRow current = download;
        try
        {
            download.Busy("Starting…");
            bar.Set(0, "Downloading…");
            var file = await Installer.DownloadAsync(release, new Progress<(long Got, long Total)>(p =>
            {
                var fraction = p.Total > 0 ? (double)p.Got / p.Total : 0;
                var text = $"{Megabytes(p.Got)} of {Megabytes(p.Total)}";
                download.Busy(text);
                bar.Set(fraction * 0.9, $"Downloading…  {fraction * 100:0}%  ·  {text}");
            }), ct);
            download.Done(Megabytes(release.Size));

            // DownloadAsync already compared size and SHA-256 with the signed manifest while the bytes came in.
            current = verify;
            verify.Done("Matches the signed release");
            cancel.IsEnabled = false;

            current = copy;
            copy.Busy("Copying to the install folder…");
            bar.Set(0.93, "Installing…");
            _copying = true;
            await Task.Run(() => Installer.CopyIntoPlace(file));
            _copying = false;
            copy.Done(Installer.ExePath);

            current = shortcuts;
            shortcuts.Busy("Desktop and Start Menu…");
            bar.Set(0.97, "Creating shortcuts…");
            await Task.Run(() =>
            {
                Installer.CreateShortcuts();
                Installer.RegisterUninstaller(release);
            });
            shortcuts.Done("Desktop, Start Menu and the uninstaller");
            bar.Set(1, "Done");

            await Task.Delay(400);
            ShowDone();
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded)
                ShowStart(); // cancelled while downloading: nothing was changed
        }
        catch (Exception ex)
        {
            _copying = false;
            current.Failed(ex.Message);
            ShowProblem(ex is InstallException ? ex.Message : $"Unexpected error: {ex.Message}");
        }
    }

    private void ShowDone()
    {
        Show(Stack(Glyph(Icons.Check, B("Success"), 58),
                Spaced(Text("Durazzno App is installed", "PageTitle"), 16),
                Spaced(Text("It lives in the tray next to the clock and starts with Windows. Open it now, or finish and find it there.", "Body"), 6),
                Spaced(Card(Stack(Text("INSTALLED IN", "CardLabel"), Mono(Installer.InstallDir, 6))), 22)),
            Button("FINISH", "SecondaryButton", () => LaunchAndClose(openWindow: false)),
            Button("OPEN DURAZZNO APP", "PrimaryButton", () => LaunchAndClose(openWindow: true)));
    }

    private void ShowProblem(string message)
    {
        Show(Stack(Glyph(Icons.Alert, B("Danger"), 58),
                Spaced(Text("The installation didn't finish", "PageTitle"), 16),
                Spaced(Text(message, "Body"), 6),
                Spaced(Text("Nothing was changed if the download failed. Try again, or close the installer and try later.", "Body"), 10)),
            Button("CLOSE", "SecondaryButton", Close),
            Button("TRY AGAIN", "PrimaryButton", () => _ = CheckAsync()));
    }

    private void LaunchAndClose(bool openWindow)
    {
        try
        {
            Installer.Launch(openWindow);
        }
        catch (Exception ex)
        {
            ShowProblem($"Durazzno App is installed but did not start: {ex.Message}");
            return;
        }
        Close();
    }

    // ── Building blocks ─────────────────────────────────────────────────────────────────

    private void Show(UIElement body, params Button[] buttons)
    {
        _body.Content = body;
        SetFooter(buttons);
    }

    private void SetFooter(params Button[] buttons)
    {
        _footer.Children.Clear();
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0);
            _footer.Children.Add(buttons[i]);
        }
    }

    private UIElement TitleBar()
    {
        var logo = new Image { Source = LoadLogo(64), Width = 22, Height = 22 };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var brand = new TextBlock { Text = "DURAZZNO APP", FontFamily = (FontFamily)FindResource("BrandFont"), FontSize = 16, Foreground = B("Text"), Margin = new Thickness(10, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = "INSTALLER", FontFamily = (FontFamily)FindResource("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 13, Foreground = B("TextDim"), Margin = new Thickness(10, 3, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(logo);
        left.Children.Add(brand);
        left.Children.Add(label);

        var minimize = WindowButton(Icons.Minimize, () => WindowState = WindowState.Minimized);
        var close = WindowButton(Icons.Close, Close);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(minimize);
        right.Children.Add(close);

        var bar = new Grid();
        bar.Children.Add(left);
        bar.Children.Add(right);
        return new Border { Background = B("WindowBg"), BorderBrush = B("Divider"), BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
    }

    private Button WindowButton(string icon, Action click)
    {
        var button = new Button { Style = (Style)FindResource("WindowButton") };
        button.Content = Glyph(icon, null, 15, 1.6, button);
        button.Click += (_, _) => click();
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    private Button Button(string text, string style, Action click)
    {
        var button = new Button { Content = text, Style = (Style)FindResource(style) };
        button.Click += (_, _) => click();
        return button;
    }

    private FrameworkElement Card(UIElement child) =>
        new ChamferBorder { Background = B("PanelBg"), BorderBrush = B("PanelBorder"), Padding = new Thickness(20, 18, 20, 18), Cut = 14, Child = child };

    private FrameworkElement Chip(string text, string tone) =>
        new Border { Background = B(tone + "Chip"), Padding = new Thickness(12, 9, 12, 9), Child = new TextBlock { Text = text, Foreground = B(tone), FontSize = 13, TextWrapping = TextWrapping.Wrap } };

    private TextBlock Text(string text, string style) => new() { Text = text, Style = (Style)FindResource(style) };

    private TextBlock Mono(string text, double top) => new()
    {
        Text = text, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 13, Foreground = B("Text"),
        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, top, 0, 0),
    };

    private static StackPanel Stack(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children)
            stack.Children.Add(child);
        return stack;
    }

    private static T Spaced<T>(T element, double top) where T : FrameworkElement
    {
        element.Margin = new Thickness(element.Margin.Left, top, element.Margin.Right, element.Margin.Bottom);
        return element;
    }

    /// <summary>A Lucide outline. With no brush it follows <paramref name="foregroundOf"/>'s Foreground (window buttons).</summary>
    internal static Viewbox Glyph(string data, Brush? brush, double size, double stroke = 2, Control? foregroundOf = null)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data), StrokeThickness = stroke, Stroke = brush,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        if (brush is null && foregroundOf is not null)
            path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = foregroundOf });
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, HorizontalAlignment = HorizontalAlignment.Left };
    }

    internal static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    private static string Megabytes(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    /// <summary>The Durazzno logo from the embedded .ico, at the frame closest to <paramref name="size"/>.</summary>
    private static BitmapSource LoadLogo(int size)
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/durazzno_icon.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).First();
    }

    // ── Pieces with state ───────────────────────────────────────────────────────────────

    /// <summary>One line of a checklist: icon (waiting, spinning, done, failed), title and detail.</summary>
    private sealed class StatusRow : Grid
    {
        private readonly ContentControl _icon = new() { Focusable = false, Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        private readonly TextBlock _detail;

        public StatusRow(string title, string detail)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Children.Add(_icon);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("RowTitle") });
            _detail = new TextBlock { Text = detail, Style = (Style)Application.Current.FindResource("CardHint"), Margin = new Thickness(0, 2, 0, 0) };
            text.Children.Add(_detail);
            SetColumn(text, 1);
            Children.Add(text);
            _icon.Content = Glyph(Icons.Pending, B("TextDim"), 22);
        }

        public void Busy(string? detail = null)
        {
            if (detail is not null)
                _detail.Text = detail;
            _detail.Foreground = B("TextMuted");
            if (_icon.Content is Viewbox { Tag: "spin" })
                return;
            var spinner = Glyph(Icons.Loader, B("Accent"), 22);
            spinner.Tag = "spin";
            spinner.RenderTransformOrigin = new Point(0.5, 0.5);
            var rotate = new RotateTransform();
            spinner.RenderTransform = rotate;
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            _icon.Content = spinner;
        }

        public void Done(string detail)
        {
            _detail.Text = detail;
            _detail.Foreground = B("TextMuted");
            _icon.Content = Glyph(Icons.Check, B("Success"), 22);
        }

        public void Failed(string detail)
        {
            _detail.Text = detail;
            _detail.Foreground = B("Danger");
            _detail.TextTrimming = TextTrimming.None;
            _detail.TextWrapping = TextWrapping.Wrap;
            _icon.Content = Glyph(Icons.Alert, B("Danger"), 22);
        }
    }

    /// <summary>A flat progress bar in the accent color with a caption under it.</summary>
    private sealed class ProgressLine : StackPanel
    {
        private readonly ColumnDefinition _done = new() { Width = new GridLength(0, GridUnitType.Star) };
        private readonly ColumnDefinition _left = new() { Width = new GridLength(1, GridUnitType.Star) };
        private readonly TextBlock _caption;

        public ProgressLine()
        {
            var track = new Grid { Height = 8, Background = B("HoverBg") };
            track.ColumnDefinitions.Add(_done);
            track.ColumnDefinitions.Add(_left);
            track.Children.Add(new Border { Background = B("Accent") });
            Children.Add(track);
            _caption = new TextBlock { Style = (Style)Application.Current.FindResource("CardHint"), Margin = new Thickness(0, 8, 0, 0) };
            Children.Add(_caption);
        }

        public void Set(double fraction, string caption)
        {
            fraction = Math.Max(0, Math.Min(1, fraction));
            _done.Width = new GridLength(fraction, GridUnitType.Star);
            _left.Width = new GridLength(1 - fraction, GridUnitType.Star);
            _caption.Text = caption;
        }
    }
}
