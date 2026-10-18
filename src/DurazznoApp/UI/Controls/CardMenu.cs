using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Durazzno.UI.Controls;

/// <summary>One entry of a <see cref="CardMenu"/>.</summary>
internal sealed record CardMenuItem(string Icon, string Text, Action Click, bool Danger = false, bool Enabled = true);

/// <summary>
/// Right-click menu in Durazzno's card style (dark chamfered panel, Lucide icons), opened at the mouse. The stock WPF
/// ContextMenu is light-themed and would clash with the app.
/// </summary>
internal static class CardMenu
{
    public static void Show(UIElement target, IReadOnlyList<CardMenuItem> items)
    {
        if (items.Count == 0)
            return;

        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };

        var stack = new StackPanel();
        foreach (var item in items)
        {
            var color = item.Danger ? Ui.B("Danger") : Ui.B("Text");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new LucideIcon { Icon = item.Icon, Width = 15, Height = 15, Foreground = color, Margin = new Thickness(0, 0, 10, 0) });
            row.Children.Add(new TextBlock { Text = item.Text, Foreground = color, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });

            var border = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(14, 9, 18, 9),
                Child = row,
                Cursor = item.Enabled ? Cursors.Hand : Cursors.Arrow,
                Opacity = item.Enabled ? 1 : 0.4,
            };
            if (item.Enabled)
            {
                border.MouseEnter += (_, _) => border.Background = Ui.B("PanelBorder");
                border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
                border.MouseLeftButtonUp += (_, _) =>
                {
                    popup.IsOpen = false;
                    item.Click();
                };
            }
            stack.Children.Add(border);
        }

        popup.Child = new ChamferBorder
        {
            Style = Ui.Res<Style>("Card"),
            Padding = new Thickness(6),
            Cut = 10,
            Child = stack,
            MinWidth = 220,
            LayoutTransform = DurazznoDialog.Scale == 1 ? Transform.Identity : new ScaleTransform(DurazznoDialog.Scale, DurazznoDialog.Scale),
        };
        popup.IsOpen = true;
    }
}
