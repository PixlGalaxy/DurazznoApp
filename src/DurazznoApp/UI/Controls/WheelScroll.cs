using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Durazzno.UI.Controls;

/// <summary>
/// Pixel-based wheel scrolling for virtualized lists. By default a virtualized ListBox scrolls whole items and
/// every wheel event (even a trackpad's tiny ones) moves at least one row, so precision touchpads fly through the
/// list. This scrolls by pixels in proportion to the wheel delta: one mouse notch (120) moves ~70 px, and a
/// trackpad's small deltas move a few pixels each.
/// </summary>
internal static class WheelScroll
{
    private const double PixelsPerDelta = 0.6;

    public static void Attach(ListBox list)
    {
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        list.PreviewMouseWheel += OnWheel;
    }

    /// <summary>
    /// The page scroller: a stock ScrollViewer scrolls 3 lines (48 px) per wheel event whatever the delta, so a
    /// trackpad's stream of tiny events races. Scroll by the delta instead, except over an inner list that can scroll
    /// itself (DriverHub, StartupHub...), which keeps its own scrolling.
    /// </summary>
    public static void AttachPage(ScrollViewer page)
    {
        page.PreviewMouseWheel += (_, e) =>
        {
            for (var node = e.OriginalSource as DependencyObject; node is not null && node != page; node = Parent(node))
                if (node is ScrollViewer inner && inner.ScrollableHeight > 0)
                    return;
            if (page.ScrollableHeight <= 0)
                return;
            page.ScrollToVerticalOffset(Math.Clamp(page.VerticalOffset - e.Delta * PixelsPerDelta, 0, page.ScrollableHeight));
            e.Handled = true;
        };
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindScrollViewer((DependencyObject)sender) is not { } viewer || viewer.ScrollableHeight <= 0)
            return; // nothing to scroll: let the page handle it

        var target = Math.Clamp(viewer.VerticalOffset - e.Delta * PixelsPerDelta, 0, viewer.ScrollableHeight);
        viewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject node)
    {
        if (node is ScrollViewer viewer)
            return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(node, i)) is { } found)
                return found;
        return null;
    }
}
