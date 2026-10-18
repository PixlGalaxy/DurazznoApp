using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Durazzno.UI.Controls;

namespace Durazzno.UI;

/// <summary>A dialog button: its text and the Theme.xaml button style (PrimaryButton, SecondaryButton, DangerButton).</summary>
internal sealed record DialogButton(string Text, string Style = "SecondaryButton");

/// <summary>
/// Durazzno App's own message box, built like the dashboard cards: dark chamfered panel, a coloured line along the top
/// (tone: Accent, Info, Warning, Danger, Success), an icon chip, title, message and themed buttons. Replaces the
/// system MessageBox. Works with or without an owner window (from the tray it centres on screen and stays on top).
/// Enter picks the first button, Esc or closing returns -1.
/// </summary>
internal sealed class DurazznoDialog : Window
{
    /// <summary>Interface scale of the main window (Compact 80 % ...), set by MainWindow.</summary>
    public static double Scale { get; set; } = 0.8;

    private int _result = -1;

    private DurazznoDialog(string title, string message, string icon, string tone, IReadOnlyList<string> bullets, IReadOnlyList<DialogButton> buttons,
        FrameworkElement? art = null)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Title = $"Durazzno App · {title}";
        FontFamily = Ui.Res<FontFamily>("BodyFont");
        UseLayoutRounding = true;

        var toneBrush = Ui.B(tone);
        // art: a custom picture in place of the icon chip (the Webin Cloud logo for server errors).
        FrameworkElement chip = art ?? Ui.IconChip(icon, toneBrush, new SolidColorBrush(Color.FromArgb(0x26, ((SolidColorBrush)toneBrush).Color.R,
            ((SolidColorBrush)toneBrush).Color.G, ((SolidColorBrush)toneBrush).Color.B)), 44, 22);
        chip.VerticalAlignment = VerticalAlignment.Top;

        var heading = new TextBlock
        {
            Text = title,
            FontFamily = Ui.Res<FontFamily>("HeadingFont"),
            FontWeight = FontWeights.Bold,
            FontSize = 22,
            Foreground = Ui.B("Text"),
            TextWrapping = TextWrapping.Wrap,
        };
        var brand = Ui.Text("DURAZZNO APP", "CardLabel", Ui.B("TextDim"));
        var text = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        text.Children.Add(brand);
        text.Children.Add(heading);

        var header = new DockPanel();
        DockPanel.SetDock(chip, Dock.Left);
        header.Children.Add(chip);
        header.Children.Add(text);

        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.B("TextMuted"),
            FontSize = 14,
            LineHeight = 21,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var content = new StackPanel();
        // The coloured line along the top edge, like the disk cards.
        content.Children.Add(new Border { Height = 3, Background = toneBrush, Margin = new Thickness(-24, -22, -24, 20) });
        content.Children.Add(header);
        content.Children.Add(body);
        foreach (var bullet in bullets)
        {
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var dot = new Border { Width = 6, Height = 6, Background = toneBrush, Margin = new Thickness(2, 7, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = bullet, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B("Text"), FontSize = 14 });
            content.Children.Add(row);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 26, 0, 0) };
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = buttons[i].Text.ToUpperInvariant(),
                Style = Ui.Res<Style>(buttons[i].Style),
                Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0),
                IsDefault = i == 0,
                MinWidth = 110,
            };
            button.Click += (_, _) => { _result = index; Close(); };
            actions.Children.Add(button);
        }
        // Primary action on the right, as in the rest of the app.
        var ordered = actions.Children.Cast<UIElement>().Reverse().ToList();
        actions.Children.Clear();
        for (var i = 0; i < ordered.Count; i++)
        {
            ((FrameworkElement)ordered[i]).Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0);
            actions.Children.Add(ordered[i]);
        }
        content.Children.Add(actions);

        var card = new ChamferBorder
        {
            Style = Ui.Res<Style>("Card"),
            Padding = new Thickness(24, 22, 24, 22),
            Width = 560,
            Child = content,
            Margin = new Thickness(18), // room for the shadow
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6, Color = Colors.Black },
            LayoutTransform = Scale == 1 ? Transform.Identity : new ScaleTransform(Scale, Scale),
        };
        Content = card;

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _result = -1;
                Close();
            }
        };
    }

    /// <summary>Shows the dialog and returns the index of the chosen button, or -1 if it was dismissed.</summary>
    public static int Show(Window? owner, string title, string message, string icon, string tone,
        IReadOnlyList<DialogButton> buttons, IReadOnlyList<string>? bullets = null, FrameworkElement? art = null)
    {
        var dialog = new DurazznoDialog(title, message, icon, tone, bullets ?? [], buttons, art);
        if (owner is { IsVisible: true } && owner.WindowState != WindowState.Minimized)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
            dialog.ShowInTaskbar = true;
        }
        dialog.ShowDialog();
        return dialog._result;
    }

    /// <summary>Yes / no question. Returns true only for the confirm button.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirm, string cancel = "Cancel",
        string icon = "info", string tone = "Accent", bool destructive = false, IReadOnlyList<string>? bullets = null) =>
        Show(owner, title, message, icon, tone,
            [new DialogButton(confirm, destructive ? "DangerButton" : "PrimaryButton"), new DialogButton(cancel)], bullets) == 0;
}
