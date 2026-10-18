using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Durazzno.Network;
using Durazzno.Tools;
using Durazzno.UI.Controls;

namespace Durazzno.UI.Pages;

/// <summary>
/// Page for one tool. Until its native Durazzno version is built, it describes what is coming and
/// launches the bundled classic program.
/// </summary>
internal sealed class ToolPage : UserControl, IDisposable
{
    private static readonly Dictionary<string, string[]> Planned = new()
    {
        ["driverhub"] =
        [
            "Installed drivers with version, date and publisher",
            "Flags drivers that are very old, unsigned or missing",
            "Links to official updates through Windows Update",
        ],
    };

    private readonly AppController _app;
    private readonly ToolDefinition _tool;
    private readonly Button _open;
    private readonly TextBlock _openNote;

    public ToolPage(AppController app, ToolDefinition tool)
    {
        _app = app;
        _tool = tool;
        var accent = Imaging.Brush(tool.Accent);

        var badge = new Border
        {
            Background = Ui.B("InfoChip"),
            Padding = new Thickness(10, 3, 10, 4),
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "IN DEVELOPMENT",
                FontFamily = Ui.Res<FontFamily>("HeadingFont"),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = Ui.B("Info"),
            },
        };

        var titleRow = Ui.Row(Ui.Text(tool.Name, "PageTitle"), badge);
        var titleBlock = Ui.Stack(titleRow, Ui.Text(tool.Tagline, "PageSubtitle"));
        titleBlock.Margin = new Thickness(20, 0, 0, 0);
        titleBlock.VerticalAlignment = VerticalAlignment.Center;
        var header = Ui.Row(Ui.IconChip(tool.Icon, accent, Imaging.Brush(tool.Accent, 0x26), 64, 30), titleBlock);

        // Planned features
        var features = Ui.Stack(Ui.PanelTitle($"WHAT {tool.Name.ToUpperInvariant()} WILL DO"));
        foreach (var line in Planned.GetValueOrDefault(tool.Id, []))
        {
            var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new LucideIcon { Icon = "circle-check", Width = 18, Height = 18, Foreground = accent, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left });
            row.Children.Add(Ui.Text(line, "Body").At(1));
            features.Children.Add(row);
        }

        // Classic tool launcher
        _open = new Button { Style = Ui.Res<Style>("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 22, 0, 0) };
        _open.Content = Ui.Row(
            new LucideIcon { Icon = "external-link", Width = 16, Height = 16, Foreground = Ui.B("AccentText") },
            new TextBlock { Text = $"OPEN {tool.ClassicName.ToUpperInvariant()}", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        _open.Click += (_, _) =>
        {
            if (Launcher.Start(_tool) is { } error)
                _app.NotifyTool(_tool, error, System.Windows.Forms.ToolTipIcon.Error);
        };
        _openNote = Ui.Text("", "CardHint");
        _openNote.Margin = new Thickness(0, 12, 0, 0);
        _openNote.TextWrapping = TextWrapping.Wrap;

        var classicText = Ui.Text(
            $"Until the native {tool.Name} is ready, Durazzno App opens the bundled {tool.ClassicName} for you.", "Body", Ui.B("TextMuted"));
        classicText.Margin = new Thickness(0, 16, 0, 0);
        var classic = Ui.Stack(Ui.PanelTitle("CLASSIC TOOL"), classicText, _open, _openNote);

        var grid = Ui.Columns(2, 20);
        grid.ColumnDefinitions[2].Width = new GridLength(0.8, GridUnitType.Star);
        grid.Margin = new Thickness(0, 32, 0, 0);
        grid.Children.Add(Ui.Card(features).At(0));
        grid.Children.Add(Ui.Card(classic).At(2));

        Content = Ui.Stack(header, grid);

        _app.StatusChanged += OnStatusChanged;
        Refresh();
    }

    public void Dispose() => _app.StatusChanged -= OnStatusChanged;

    private void OnStatusChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var offline = _app.Connection.State == ConnectionState.NoInternet;
        if (!_tool.IsInstalled)
        {
            _open.IsEnabled = false;
            _openNote.Text = $"{_tool.ClassicName} was not found in the Tools folder.";
        }
        else if (_tool.RequiresInternet && offline)
        {
            _open.IsEnabled = false;
            _openNote.Text = $"{_tool.ClassicName} needs an internet connection.";
        }
        else
        {
            _open.IsEnabled = true;
            _openNote.Text = "Some tools ask for administrator rights when they start.";
        }
    }
}
