using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Durazzno.UI.Controls;

namespace Durazzno.UI;

/// <summary>
/// Progress card shown when the user applies an update: a bar with "Downloading Durazzno App 3.1.0 from Webin Cloud
/// Server · 45%", then "Installing…". On failure it shows the reason and a Close button. Same card style as
/// <see cref="DurazznoDialog"/>.
/// </summary>
internal sealed class UpdateWindow : Window
{
    private readonly TextBlock _title;
    private readonly TextBlock _text;
    private readonly ProgressBar _bar;
    private readonly Border _line;
    private readonly Button _close;

    public UpdateWindow(Window? owner, string version)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Durazzno App · Updating";
        Topmost = owner is null;
        ShowInTaskbar = owner is null;
        if (owner is { IsVisible: true } && owner.WindowState != WindowState.Minimized)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _line = new Border { Height = 3, Background = Ui.B("Accent"), Margin = new Thickness(-24, -22, -24, 20) };
        var chip = Ui.IconChip("download", Ui.B("Accent"), Imaging.Brush("#F5C400", 0x26), 44, 22);
        _title = new TextBlock
        {
            Text = $"Updating to {version}",
            FontFamily = Ui.Res<FontFamily>("HeadingFont"),
            FontWeight = FontWeights.Bold,
            FontSize = 22,
            Foreground = Ui.B("Text"),
        };
        var heading = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        heading.Children.Add(Ui.Text("DURAZZNO APP", "CardLabel", Ui.B("TextDim")));
        heading.Children.Add(_title);
        var header = new DockPanel();
        DockPanel.SetDock(chip, Dock.Left);
        header.Children.Add(chip);
        header.Children.Add(heading);

        _bar = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 22, 0, 0), BorderThickness = new Thickness(0),
            Background = Ui.B("PanelBorder"), Foreground = Ui.B("Accent") };
        _bar.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ProgressBar">
                <Grid>
                    <Border x:Name="PART_Track" Background="{TemplateBinding Background}" />
                    <Border x:Name="PART_Indicator" Background="{TemplateBinding Foreground}" HorizontalAlignment="Left" />
                </Grid>
            </ControlTemplate>
            """);
        _text = new TextBlock { Text = "Preparing…", Foreground = Ui.B("TextMuted"), FontSize = 13.5, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        _close = new Button { Content = "CLOSE", Style = Ui.Res<Style>("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0), Visibility = Visibility.Collapsed };
        _close.Click += (_, _) => Close();

        var content = new StackPanel();
        content.Children.Add(_line);
        content.Children.Add(header);
        content.Children.Add(_bar);
        content.Children.Add(_text);
        content.Children.Add(_close);

        Content = new ChamferBorder
        {
            Style = Ui.Res<Style>("Card"),
            Padding = new Thickness(24, 22, 24, 22),
            Width = 560,
            Child = content,
            Margin = new Thickness(18),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6, Color = Colors.Black },
            LayoutTransform = DurazznoDialog.Scale == 1 ? Transform.Identity : new ScaleTransform(DurazznoDialog.Scale, DurazznoDialog.Scale),
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    public void Report(string text, double? percent)
    {
        _text.Text = text;
        _bar.IsIndeterminate = false;
        _bar.Value = percent ?? 100;
    }

    public void Fail(string message)
    {
        _title.Text = "Update failed";
        _text.Text = message;
        _text.Foreground = Ui.B("Danger");
        _bar.Foreground = Ui.B("Danger");
        _bar.Value = 100;
        _line.Background = Ui.B("Danger");
        _close.Visibility = Visibility.Visible;
    }
}
