using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Durazzno.UI.Controls;

/// <summary>
/// One row of tiles that scrolls sideways. Arrow buttons appear only when some tiles are off-screen;
/// the mouse wheel scrolls it too.
/// </summary>
internal sealed class TileStrip : Grid
{
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll;
    private readonly Button _left;
    private readonly Button _right;
    private Point? _dragStart;
    private double _dragOffset;
    private bool _dragging;

    public TileStrip()
    {
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Content = _items,
        };
        _scroll.ScrollChanged += (_, _) => UpdateArrows();
        _scroll.PreviewMouseWheel += OnWheel;

        // Click-and-drag to slide. A drag past a few pixels swallows the click so no tile gets selected.
        _scroll.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            _dragOffset = _scroll.HorizontalOffset;
            _dragging = false;
        };
        _scroll.PreviewMouseMove += (_, e) =>
        {
            if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || _scroll.ScrollableWidth <= 0)
                return;

            var dx = e.GetPosition(this).X - start.X;
            if (!_dragging && Math.Abs(dx) > 6)
            {
                _dragging = true;
                _scroll.CaptureMouse();
                Cursor = Cursors.SizeWE;
            }

            if (_dragging)
            {
                _scroll.ScrollToHorizontalOffset(_dragOffset - dx);
                e.Handled = true;
            }
        };
        _scroll.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_dragging)
            {
                _scroll.ReleaseMouseCapture();
                Cursor = null;
                e.Handled = true;
            }

            _dragStart = null;
            _dragging = false;
        };

        _left = Arrow("chevron-left", HorizontalAlignment.Left, -1);
        _right = Arrow("chevron-right", HorizontalAlignment.Right, +1);

        Children.Add(_scroll);
        Children.Add(_left);
        Children.Add(_right);
        SizeChanged += (_, _) => UpdateArrows();
    }

    /// <summary>Replaces the tiles but keeps the current scroll position (selection re-renders the row).</summary>
    public void SetItems(IEnumerable<UIElement> tiles)
    {
        var offset = _scroll.HorizontalOffset;
        _items.Children.Clear();
        foreach (var tile in tiles)
            _items.Children.Add(tile);

        _scroll.UpdateLayout();
        _scroll.ScrollToHorizontalOffset(Math.Min(offset, _scroll.ScrollableWidth));
        UpdateArrows();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_scroll.ScrollableWidth <= 0)
            return; // Let the page scroll normally.

        _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void ScrollBy(int direction)
    {
        // Move by most of the visible width so the next tiles come fully into view.
        var target = Math.Clamp(_scroll.HorizontalOffset + direction * _scroll.ViewportWidth * 0.8, 0, _scroll.ScrollableWidth);
        _scroll.ScrollToHorizontalOffset(target);
    }

    private void UpdateArrows()
    {
        _left.Visibility = _scroll.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
        _right.Visibility = _scroll.HorizontalOffset < _scroll.ScrollableWidth - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button Arrow(string icon, HorizontalAlignment side, int direction)
    {
        var face = new Border
        {
            Width = 36,
            Height = 36,
            Background = Ui.B("SelectedBg"),
            BorderBrush = Ui.B("Accent"),
            BorderThickness = new Thickness(1),
            Child = new LucideIcon { Icon = icon, Width = 18, Height = 18, Foreground = Ui.B("Accent") },
        };

        var button = new Button
        {
            Content = face,
            Cursor = Cursors.Hand,
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = side == HorizontalAlignment.Left ? new Thickness(-18, 0, 0, 0) : new Thickness(0, 0, -2, 0),
            Visibility = Visibility.Collapsed,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
            Focusable = false,
        };
        button.Click += (_, _) => ScrollBy(direction);
        return button;
    }
}
