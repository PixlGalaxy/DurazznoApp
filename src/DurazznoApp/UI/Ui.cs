using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Durazzno.UI.Controls;

namespace Durazzno.UI;

/// <summary>Small builders for the recurring pieces of the iCUE-style layout.</summary>
internal static class Ui
{
    public static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    public static Brush B(string key) => Res<Brush>(key);

    public static ChamferBorder Card(UIElement child, Thickness? padding = null)
    {
        var card = new ChamferBorder { Style = Res<Style>("Card"), Child = child };
        if (padding is { } p)
            card.Padding = p;
        return card;
    }

    public static ContentControl PanelTitle(string text) =>
        new() { Content = text, Style = Res<Style>("PanelTitle"), Focusable = false };

    public static TextBlock Text(string text, string style, Brush? foreground = null)
    {
        var block = new TextBlock { Text = text, Style = Res<Style>(style) };
        if (foreground is not null)
            block.Foreground = foreground;
        return block;
    }

    public static Border IconChip(string icon, Brush foreground, Brush background, double size = 40, double iconSize = 20) => new()
    {
        Width = size,
        Height = size,
        Background = background,
        Child = new LucideIcon { Icon = icon, Width = iconSize, Height = iconSize, Foreground = foreground },
    };

    /// <summary>Horizontal usage bar (0–100).</summary>
    public static Grid UsageBar(double percent, Brush fill, double height = 6)
    {
        percent = Math.Clamp(percent, 0, 100);
        var grid = new Grid { Height = height, Background = B("HoverBg") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(percent, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - percent, GridUnitType.Star) });
        grid.Children.Add(new Border { Background = fill });
        return grid;
    }

    /// <summary>Grid with fixed gutters between star columns, e.g. Columns(3, 20) → *,20,*,20,*.</summary>
    public static Grid Columns(int count, double gutter)
    {
        var grid = new Grid();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gutter) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        return grid;
    }

    public static T At<T>(this T element, int column, int row = 0, int columnSpan = 1, int rowSpan = 1) where T : UIElement
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        Grid.SetColumnSpan(element, columnSpan);
        Grid.SetRowSpan(element, rowSpan);
        return element;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }

    public static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }
}

/// <summary>Top-row KPI card: label, big value, hint and a tinted icon chip (like "FAILED ATTEMPTS · 24 H").</summary>
internal sealed class StatCard : ContentControl
{
    private readonly TextBlock _value;
    private readonly TextBlock _hint;

    public StatCard(string label, string icon, Brush tone, Brush toneChip)
    {
        Focusable = false;
        _value = Ui.Text("—", "CardValue", tone);
        _hint = Ui.Text("", "CardHint");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Ui.Stack(Ui.Text(label, "CardLabel"), _value, _hint));
        grid.Children.Add(Ui.IconChip(icon, tone, toneChip).At(1));
        ((FrameworkElement)grid.Children[1]).VerticalAlignment = VerticalAlignment.Top;

        Content = Ui.Card(grid);
    }

    public void Set(string value, string hint)
    {
        _value.Text = value;
        _hint.Text = hint;
    }
}
