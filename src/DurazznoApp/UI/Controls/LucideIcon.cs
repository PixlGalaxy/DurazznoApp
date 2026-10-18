using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Durazzno.UI.Controls;

/// <summary>Draws an embedded Lucide icon as crisp vector strokes, tinted with <see cref="Foreground"/>.</summary>
public sealed class LucideIcon : FrameworkElement
{
    private const double ViewBox = 24;
    private static readonly Dictionary<string, Geometry> Cache = new();

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(LucideIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(LucideIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(LucideIcon),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public LucideIcon()
    {
        Width = Height = 20;
        SnapsToDevicePixels = true;
    }

    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Stroke width in Lucide's 24-unit grid (default 2).</summary>
    public double StrokeWidth
    {
        get => (double)GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // "img:<name>" draws a full-colour embedded image (plugin logos) instead of a Lucide glyph.
        if (Icon?.StartsWith("img:") == true)
        {
            if (Imaging.Embedded(Icon[4..]) is { } image)
                dc.DrawImage(image, new Rect(0, 0, ActualWidth, ActualHeight));
            return;
        }

        if (string.IsNullOrEmpty(Icon) || Load(Icon) is not { } geometry)
            return;

        var scale = Math.Min(ActualWidth, ActualHeight) / ViewBox;
        var pen = new Pen(Foreground, StrokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        dc.PushTransform(new TranslateTransform((ActualWidth - ViewBox * scale) / 2, (ActualHeight - ViewBox * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();
        dc.Pop();
    }

    public static Geometry? Load(string name)
    {
        if (Cache.TryGetValue(name, out var cached))
            return cached;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Lucide.{name}");
        if (stream is null)
            return null;

        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var e in XDocument.Load(stream).Root!.Elements())
        {
            double N(string attr) => double.Parse((string?)e.Attribute(attr) ?? "0", System.Globalization.CultureInfo.InvariantCulture);

            Geometry? part = e.Name.LocalName switch
            {
                "path" => Geometry.Parse((string)e.Attribute("d")!),
                "circle" => new EllipseGeometry(new Point(N("cx"), N("cy")), N("r"), N("r")),
                "ellipse" => new EllipseGeometry(new Point(N("cx"), N("cy")), N("rx"), N("ry")),
                "line" => new LineGeometry(new Point(N("x1"), N("y1")), new Point(N("x2"), N("y2"))),
                "rect" => new RectangleGeometry(new Rect(N("x"), N("y"), N("width"), N("height")), N("rx"), e.Attribute("ry") is null ? N("rx") : N("ry")),
                "polyline" or "polygon" => Geometry.Parse("M" + (string)e.Attribute("points")! + (e.Name.LocalName == "polygon" ? "Z" : "")),
                _ => null,
            };
            if (part is not null)
                group.Children.Add(part);
        }

        group.Freeze();
        Cache[name] = group;
        return group;
    }
}
