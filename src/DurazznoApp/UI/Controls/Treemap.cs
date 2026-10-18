using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Durazzno.Hardware.Storage;

namespace Durazzno.UI.Controls;

/// <summary>One tile on the map: a folder, a big file, or the aggregated small files of a folder.</summary>
internal sealed record MapItem(DirNode Owner, DirNode? Dir, FileLeaf? File, long Size)
{
    public bool IsSmallFiles => Dir is null && File is null;

    public string Name => Dir?.Name ?? File?.Name ?? $"{Owner.SmallFilesCount:N0} small files";

    public string Path => Dir?.FullPath ?? (File is { } f ? System.IO.Path.Combine(Owner.FullPath, f.Name) : Owner.FullPath);

    /// <summary>Identity that survives size changes (used to animate a tile from its previous position).</summary>
    public object Key => Dir ?? (object)(Owner, File?.Name ?? "\0small");
}

/// <summary>
/// Squarified treemap coloured by folder: every top-level item gets its own colour (stable per name) and everything
/// inside inherits it, a bit darker per level. Layout runs off the UI thread and is drawn as one geometry per colour.
/// In live mode (while a scan runs) only folder outlines are drawn, animating from their previous rectangles so the
/// map visibly grows like a blueprint; when the scan ends the colours fade in over the outlines.
/// </summary>
internal sealed class Treemap : FrameworkElement
{
    private const double FolderPadding = 1.5;
    private static readonly TimeSpan AnimationTime = TimeSpan.FromMilliseconds(420);
    private static readonly Color Background = Color.FromRgb(0x0E, 0x0E, 0x11);

    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(900);

    private readonly DrawingVisual _wire = new();
    private readonly DrawingVisual _map = new();
    private readonly DrawingVisual _overlay = new();
    private bool _fading;
    private readonly DispatcherTimer _resizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Stopwatch _clock = new();

    private DirNode? _root;
    private Layout? _current;
    private Dictionary<object, Rect> _from = new();
    private bool _animating;
    private CancellationTokenSource? _layoutCts;
    private bool _layoutRunning, _layoutPending;
    private Tile? _hover;
    private MapItem? _selected;

    public Treemap()
    {
        AddVisualChild(_wire);
        AddVisualChild(_map);
        AddVisualChild(_overlay);
        RenderOptions.SetEdgeMode(_map, EdgeMode.Aliased);
        RenderOptions.SetEdgeMode(_wire, EdgeMode.Aliased);
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            Relayout();
        };
        SizeChanged += (_, _) =>
        {
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        };
        Unloaded += (_, _) => StopAnimation();
    }

    public event Action<MapItem?>? HoverChanged;
    public event Action<MapItem>? ItemSelected;
    public event Action<DirNode>? FolderOpened;

    /// <summary>Live mode: less detail and animated transitions (used while scanning).</summary>
    public bool Live { get; set; }

    public DirNode? Root
    {
        get => _root;
        set
        {
            if (_root != value)
                _from.Clear(); // A different folder: no point animating from the old one.
            _root = value;
            _hover = null;
            Relayout();
        }
    }

    public MapItem? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            DrawOverlay();
        }
    }

    /// <summary>Stable colour for a top-level item (also used by the folder list).</summary>
    public static Color ColorFor(MapItem item) => FromHsl(Hue(item.Key is DirNode d ? d.Name : item.Name), 0.62, 0.56);

    /// <summary>Recompute the layout. Calls during a running layout are coalesced into one more run.</summary>
    public void Relayout()
    {
        if (_root is null || ActualWidth < 4 || ActualHeight < 4)
        {
            _layoutCts?.Cancel();
            _current = null;
            StopAnimation();
            ClearWire();
            using (_map.RenderOpen()) { }
            DrawOverlay();
            return;
        }

        if (_layoutRunning)
        {
            _layoutPending = true;
            return;
        }

        _layoutRunning = true;
        var cts = _layoutCts = new CancellationTokenSource();
        var root = _root;
        var size = new Size(ActualWidth, ActualHeight);
        var live = Live;

        Task.Run(() => Build(root, size, live, cts.Token), cts.Token).ContinueWith(t =>
        {
            _layoutRunning = false;
            if (t.IsFaulted)
                Core.Log.Error("Treemap layout failed", t.Exception);

            if (!cts.IsCancellationRequested && t.Status == TaskStatus.RanToCompletion && root == _root)
                Show(t.Result);

            if (_layoutPending)
            {
                _layoutPending = false;
                Relayout();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override int VisualChildrenCount => 3;

    protected override Visual GetVisualChild(int index) => index switch { 0 => _wire, 1 => _map, _ => _overlay };

    // ── Drawing ───────────────────────────────────────────────────────────────────────────

    private void Show(Layout layout)
    {
        var previous = _current;
        _current = layout;
        _hover = null;

        if (layout.Live)
        {
            // Blueprint mode: outlines only, animated from where each tile was.
            _from = previous is { Live: true } ? CurrentRects(previous) : new Dictionary<object, Rect>();
            _map.Opacity = 0;
            _wire.Opacity = 1;
            _fading = false;
            StartClock();
        }
        else if (previous is { Live: true })
        {
            // Scan finished: colours fade in over the last outline frame.
            Draw(layout.StaticLayers);
            _map.Opacity = 0;
            _fading = true;
            StartClock();
        }
        else
        {
            StopAnimation();
            ClearWire();
            _map.Opacity = 1;
            Draw(layout.StaticLayers);
        }

        DrawOverlay();
    }

    private void StartClock()
    {
        _clock.Restart();
        if (_animating)
            return;
        _animating = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private Dictionary<object, Rect> CurrentRects(Layout layout)
    {
        var t = _animating && !_fading ? Ease(Progress(AnimationTime)) : 1;
        var map = new Dictionary<object, Rect>(layout.Tiles.Length);
        foreach (var tile in layout.Tiles)
            map[tile.Item.Key] = Lerp(_from.TryGetValue(tile.Item.Key, out var f) ? f : Collapsed(tile.Rect), tile.Rect, t);
        return map;
    }

    private double Progress(TimeSpan duration) => Math.Min(1, _clock.Elapsed.TotalMilliseconds / duration.TotalMilliseconds);

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_current is not { } layout)
        {
            StopAnimation();
            return;
        }

        if (_fading)
        {
            var f = Ease(Progress(FadeTime));
            _map.Opacity = f;
            _wire.Opacity = 1 - f;
            if (f >= 1)
            {
                _fading = false;
                StopAnimation();
                ClearWire();
            }
            return;
        }

        var p = Progress(AnimationTime);
        DrawWire(layout, Ease(p));
        if (p >= 1)
            StopAnimation();
    }

    /// <summary>Outlines of every tile: accent for top-level folders, fainter white deeper down.</summary>
    private void DrawWire(Layout layout, double t)
    {
        var geometries = new StreamGeometry[3];
        var contexts = new StreamGeometryContext[3];
        foreach (var tile in layout.Tiles)
        {
            var r = Lerp(_from.TryGetValue(tile.Item.Key, out var f) ? f : Collapsed(tile.Rect), tile.Rect, t);
            if (r.Width < 1 || r.Height < 1)
                continue;
            var level = Math.Min(tile.Depth, 2);
            contexts[level] ??= (geometries[level] = new StreamGeometry()).Open();
            AddOutline(contexts[level], Inset(r, 0.5));
        }

        using var dc = _wire.RenderOpen();
        dc.DrawRectangle(new SolidColorBrush(Background), null, new Rect(RenderSize));
        for (var i = 2; i >= 0; i--)
        {
            if (geometries[i] is null)
                continue;
            contexts[i].Close();
            dc.DrawGeometry(null, WirePens[i], geometries[i]);
        }
    }

    private static readonly Pen[] WirePens = CreateWirePens();

    private static Pen[] CreateWirePens()
    {
        var accent = Color.FromRgb(0xF5, 0xC4, 0x00);
        static Pen P(Color c, byte alpha, double width)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)), width);
            pen.Freeze();
            return pen;
        }
        return [P(accent, 0xE0, 1.4), P(Colors.White, 0x70, 1), P(Colors.White, 0x30, 1)];
    }

    private void ClearWire()
    {
        using (_wire.RenderOpen()) { }
        _wire.Opacity = 1;
    }

    private void StopAnimation()
    {
        if (!_animating)
            return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void Draw(IEnumerable<(Geometry Geometry, Brush Brush)> layers)
    {
        using var dc = _map.RenderOpen();
        dc.DrawRectangle(new SolidColorBrush(Background), null, new Rect(RenderSize));
        foreach (var (geometry, brush) in layers)
            dc.DrawGeometry(brush, null, geometry);
    }

    private static double Ease(double t) => 1 - Math.Pow(1 - t, 3); // ease-out cubic

    private static Rect Collapsed(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2, 0, 0);

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
        Math.Max(0, a.Width + (b.Width - a.Width) * t), Math.Max(0, a.Height + (b.Height - a.Height) * t));

    /// <summary>Rectangle whose four edges are all stroked (fills use unstroked edges, which only draw the closing side).</summary>
    private static void AddOutline(StreamGeometryContext c, Rect r)
    {
        c.BeginFigure(r.TopLeft, false, true);
        c.LineTo(r.TopRight, true, false);
        c.LineTo(r.BottomRight, true, false);
        c.LineTo(r.BottomLeft, true, false);
    }

    private static void AddRect(StreamGeometryContext c, Rect r)
    {
        c.BeginFigure(r.TopLeft, true, true);
        c.LineTo(r.TopRight, false, false);
        c.LineTo(r.BottomRight, false, false);
        c.LineTo(r.BottomLeft, false, false);
    }

    // ── Mouse ─────────────────────────────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_hover is { } h && h.Rect.Contains(p) && h.Item.Dir is null)
            return;

        var hit = HitTest(p);
        if (hit?.Item.Key == _hover?.Item.Key)
            return;

        _hover = hit;
        DrawOverlay();
        HoverChanged?.Invoke(hit?.Item);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        DrawOverlay();
        HoverChanged?.Invoke(null);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (HitTest(e.GetPosition(this)) is not { } hit)
            return;

        if (e.ClickCount == 2)
        {
            var target = hit.Item.Dir ?? hit.Item.Owner;
            if (target != _root)
                FolderOpened?.Invoke(target);
        }
        else
        {
            Selected = hit.Item;
            ItemSelected?.Invoke(hit.Item);
        }
    }

    /// <summary>Deepest tile under the point (tiles are stored parents first).</summary>
    private Tile? HitTest(Point p)
    {
        var tiles = _current?.Tiles ?? [];
        for (var i = tiles.Length - 1; i >= 0; i--)
            if (tiles[i].Rect.Contains(p))
                return tiles[i];
        return null;
    }

    private void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        var tiles = _current?.Tiles ?? [];

        if (_selected is { } sel && tiles.FirstOrDefault(t => t.Item.Key.Equals(sel.Key)) is { Item: not null } selTile)
            dc.DrawRectangle(null, new Pen(Ui.B("Accent"), 2), Inset(selTile.Rect, 1));

        if (_hover is { } h)
        {
            if (h.Item.Dir is null && tiles.FirstOrDefault(t => t.Item.Dir == h.Item.Owner) is { Item: not null } folder)
                dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1), Inset(folder.Rect, 0.5));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), new Pen(Brushes.White, 1.5), Inset(h.Rect, 0.75));
        }
    }

    private static Rect Inset(Rect r, double d) =>
        r.Width > 2 * d && r.Height > 2 * d ? new Rect(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d) : r;

    // ── Layout (background thread) ────────────────────────────────────────────────────────

    private readonly record struct Tile(Rect Rect, MapItem Item, int Depth);

    private readonly record struct Paint(Rect Rect, object Key, int Color);

    private sealed record Layout(Tile[] Tiles, Paint[] Paints, Brush[] Brushes, List<(Geometry, Brush)> StaticLayers, bool Live);

    private static Layout Build(DirNode root, Size size, bool live, CancellationToken ct)
    {
        var minArea = live ? 30.0 : 6.0;
        var maxDepth = live ? 3 : 40;

        var tiles = new List<Tile>(4096);
        var paints = new List<Paint>(4096);
        var colorIndex = new Dictionary<Color, int>();

        int ColorId(Color c)
        {
            if (!colorIndex.TryGetValue(c, out var id))
                colorIndex[c] = id = colorIndex.Count;
            return id;
        }

        void Fill(object key, Color color, Rect r)
        {
            if (r.Width >= 3) r.Width -= 1; // 1 px gap shows the dark background as grid lines
            if (r.Height >= 3) r.Height -= 1;
            paints.Add(new Paint(r, key, ColorId(color)));
        }

        void Recurse(DirNode node, Rect rect, int depth, double hue)
        {
            ct.ThrowIfCancellationRequested();
            var items = Children(node);
            var total = items.Sum(i => (double)i.Size);
            if (total <= 0)
                return;

            Squarify(items, rect, total, (item, r) =>
            {
                if (r.Width * r.Height < minArea)
                    return;

                // Top-level items pick the colour; everything below inherits it, darker per level.
                var h = depth == 0 ? Hue(item.Key is DirNode d0 ? d0.Name : item.Name) : hue;
                var lightness = Math.Max(0.28, 0.56 - 0.06 * depth);
                tiles.Add(new Tile(r, item, depth));

                if (item.Dir is { } dir)
                {
                    double w = r.Width - 2 * FolderPadding, hgt = r.Height - 2 * FolderPadding;
                    if (depth < maxDepth && w > 4 && hgt > 4 && dir.Size > 0)
                        Recurse(dir, new Rect(r.X + FolderPadding, r.Y + FolderPadding, w, hgt), depth + 1, h);
                    else
                        Fill(item.Key, FromHsl(h, 0.45, lightness - 0.06), r);
                }
                else
                {
                    Fill(item.Key, FromHsl(h, item.IsSmallFiles ? 0.25 : 0.62, item.IsSmallFiles ? lightness - 0.1 : lightness), r);
                }
            });
        }

        Recurse(root, new Rect(size), 0, 0);

        var brushes = new Brush[colorIndex.Count];
        foreach (var (color, id) in colorIndex)
        {
            var b = new SolidColorBrush(color);
            b.Freeze();
            brushes[id] = b;
        }

        // Pre-built geometries for the final (non-animated) frame.
        var geometries = new StreamGeometry[brushes.Length];
        var contexts = new StreamGeometryContext[brushes.Length];
        foreach (var p in paints)
        {
            contexts[p.Color] ??= (geometries[p.Color] = new StreamGeometry()).Open();
            AddRect(contexts[p.Color], p.Rect);
        }

        var layers = new List<(Geometry, Brush)>();
        for (var i = 0; i < geometries.Length; i++)
        {
            if (geometries[i] is null)
                continue;
            contexts[i].Close();
            geometries[i].Freeze();
            layers.Add((geometries[i], brushes[i]));
        }

        return new Layout(tiles.ToArray(), paints.ToArray(), brushes, layers, live);
    }

    /// <summary>Folder contents as map items, biggest first. Safe to call while a scan is running.</summary>
    public static List<MapItem> Children(DirNode node)
    {
        var dirs = node.Dirs;
        var files = node.Files;
        var items = new List<MapItem>(dirs.Count + files.Count + 1);
        foreach (var d in dirs)
        {
            var size = d.Size;
            if (size > 0)
                items.Add(new MapItem(node, d, null, size));
        }
        foreach (var f in files)
            items.Add(new MapItem(node, null, f, f.Size));
        if (node.SmallFilesSize > 0)
            items.Add(new MapItem(node, null, null, node.SmallFilesSize));
        items.Sort((a, b) => b.Size.CompareTo(a.Size));
        return items;
    }

    /// <summary>Squarified layout (Bruls, Huizing, van Wijk): rows that keep tiles as close to square as possible.</summary>
    private static void Squarify(List<MapItem> items, Rect rect, double total, Action<MapItem, Rect> place)
    {
        var scale = rect.Width * rect.Height / total;
        var i = 0;
        while (i < items.Count && rect.Width > 0 && rect.Height > 0)
        {
            var side = Math.Min(rect.Width, rect.Height);
            double rowSum = 0, rowMin = double.MaxValue, rowMax = 0, worst = double.MaxValue;
            var j = i;
            for (; j < items.Count; j++)
            {
                var area = items[j].Size * scale;
                double sum = rowSum + area, min = Math.Min(rowMin, area), max = Math.Max(rowMax, area);
                var w = Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
                if (j > i && w > worst)
                    break;
                (rowSum, rowMin, rowMax, worst) = (sum, min, max, w);
            }

            var thickness = rowSum / side;
            if (rect.Width >= rect.Height)
            {
                var y = rect.Y;
                for (var k = i; k < j; k++)
                {
                    var h = Math.Max(0, items[k].Size * scale / thickness);
                    place(items[k], new Rect(rect.X, y, thickness, h));
                    y += h;
                }
                rect = new Rect(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height);
            }
            else
            {
                var x = rect.X;
                for (var k = i; k < j; k++)
                {
                    var w = Math.Max(0, items[k].Size * scale / thickness);
                    place(items[k], new Rect(x, rect.Y, w, thickness));
                    x += w;
                }
                rect = new Rect(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness));
            }

            i = j;
        }
    }

    // ── Colour ────────────────────────────────────────────────────────────────────────────

    /// <summary>"Random" but stable hue per name (FNV-1a hash), so a folder keeps its colour across refreshes.</summary>
    private static double Hue(string name)
    {
        uint hash = 2166136261;
        foreach (var ch in name.ToUpperInvariant())
            hash = (hash ^ ch) * 16777619;
        return hash % 360;
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
