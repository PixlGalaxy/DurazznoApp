using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Durazzno.UI.Controls;

/// <summary>Panel background with a cut bottom-right corner (iCUE-style card).</summary>
internal sealed class ChamferBorder : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(
        nameof(Cut), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(new Thickness(0), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public double Cut
    {
        get => (double)GetValue(CutProperty);
        set => SetValue(CutProperty, value);
    }

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var p = Padding;
        var inner = new Size(
            Math.Max(0, constraint.Width - p.Left - p.Right),
            Math.Max(0, constraint.Height - p.Top - p.Bottom));
        Child?.Measure(inner);
        var desired = Child?.DesiredSize ?? default;
        return new Size(desired.Width + p.Left + p.Right, desired.Height + p.Top + p.Bottom);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var p = Padding;
        Child?.Arrange(new Rect(p.Left, p.Top,
            Math.Max(0, arrangeSize.Width - p.Left - p.Right),
            Math.Max(0, arrangeSize.Height - p.Top - p.Bottom)));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, c = Math.Min(Cut, Math.Min(w, h) / 2);
        if (w <= 0 || h <= 0)
            return;

        // Inset by half a pixel so the 1px border lands on whole device pixels.
        const double o = 0.5;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(o, o), true, true);
            g.LineTo(new Point(w - o, o), true, false);
            g.LineTo(new Point(w - o, h - c), true, false);
            g.LineTo(new Point(w - c, h - o), true, false);
            g.LineTo(new Point(o, h - o), true, false);
        }
        geometry.Freeze();

        dc.DrawGeometry(Background, BorderBrush is null ? null : new Pen(BorderBrush, 1), geometry);
    }
}
