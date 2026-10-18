using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Durazzno.UI.Controls;

/// <summary>
/// The Webin Cloud Server logo with a coloured Lucide icon on the bottom-right of the cloud (warning, offline...),
/// outlined in black so it stands out on the orange. Used for connection problems (Apps page, download errors).
/// </summary>
internal static class WebinCloudArt
{
    public static FrameworkElement Create(string icon, Brush tone, double size = 96)
    {
        var iconSize = size * 0.27;
        var margin = new Thickness(0, 0, size * 0.02, size * 0.167); // overlaps the bottom-right of the cloud
        var outline = new LucideIcon
        {
            Icon = icon,
            Width = iconSize,
            Height = iconSize,
            StrokeWidth = 5.5,
            Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = margin,
        };
        var badge = new LucideIcon
        {
            Icon = icon,
            Width = iconSize,
            Height = iconSize,
            Foreground = tone,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = margin,
        };
        var logo = new Grid { Width = size, Height = size };
        logo.Children.Add(new Image { Source = Imaging.Embedded("Plugins.webincloud"), Width = size, Height = size });
        logo.Children.Add(outline);
        logo.Children.Add(badge);
        return logo;
    }
}
