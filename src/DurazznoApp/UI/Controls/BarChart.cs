using System.Windows;
using System.Windows.Media;

namespace Durazzno.UI.Controls;

/// <summary>Simple live bar chart: one bar per sample (0–100), newest on the right, empty slots drawn as a baseline dash.</summary>
internal sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.Gold, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    private IReadOnlyList<double> _samples = [];

    public int Capacity { get; set; } = 60;

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public void SetSamples(IReadOnlyList<double> samples)
    {
        _samples = samples;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([2, 4], 0) };
        foreach (var level in new[] { 0.25, 0.5, 0.75 })
        {
            var y = Math.Round(h - h * level) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        var slot = w / Capacity;
        var barWidth = Math.Max(2, slot * 0.72);
        var offset = Capacity - _samples.Count;

        for (var i = 0; i < Capacity; i++)
        {
            var x = i * slot + (slot - barWidth) / 2;
            var index = i - offset;
            if (index < 0 || _samples[index] < 0.5)
            {
                dc.DrawRectangle(BarBrush, null, new Rect(x, h - 2, barWidth, 2));
                continue;
            }

            var barHeight = Math.Max(2, h * Math.Clamp(_samples[index], 0, 100) / 100);
            dc.DrawRectangle(BarBrush, null, new Rect(x, h - barHeight, barWidth, barHeight));
        }
    }
}
