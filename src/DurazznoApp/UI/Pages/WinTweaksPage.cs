using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Durazzno.Core;
using Durazzno.Hardware.Drivers;
using Durazzno.Tools;
using Durazzno.Tweaks;
using Durazzno.UI.Controls;

namespace Durazzno.UI.Pages;

/// <summary>
/// WinTweaks: suggestions for this PC (only the ones that apply), and Windows tweaks grouped by category, each a
/// switch that can be turned back off to restore exactly what was there before.
/// </summary>
internal sealed class WinTweaksPage : UserControl, IDisposable
{
    private readonly AppController _app;
    private readonly ToolDefinition _tool;
    private readonly ContentControl _tips = new() { Focusable = false };
    private readonly Border _banner = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(14, 10, 10, 10) };
    private readonly TextBlock _bannerText = Ui.Text("", "Body");
    private readonly ContentControl _bannerAction = new() { Focusable = false };
    private readonly Button _undoAll;
    private readonly Dictionary<string, CheckBox> _switches = [];
    private bool _needsExplorer, _needsRestart, _busy;

    public WinTweaksPage(AppController app, ToolDefinition tool)
    {
        _app = app;
        _tool = tool;
        var accent = Imaging.Brush(tool.Accent);

        var title = Ui.Stack(Ui.Text(tool.Name, "PageTitle"), Ui.Text(tool.Tagline, "PageSubtitle"));
        title.Margin = new Thickness(20, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        _undoAll = Action("rotate-ccw", "UNDO ALL", "SecondaryButton", UndoAllAsync);
        _undoAll.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Ui.Row(Ui.IconChip(tool.Icon, accent, Imaging.Brush(tool.Accent, 0x26), 64, 52), title));
        header.Children.Add(_undoAll.At(1));

        // Banner: "restart Explorer / Windows to see the changes".
        _bannerText.TextWrapping = TextWrapping.Wrap;
        _bannerText.VerticalAlignment = VerticalAlignment.Center;
        var bannerGrid = new Grid();
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bannerGrid.Children.Add(_bannerText);
        _bannerAction.Margin = new Thickness(16, 0, 0, 0);
        bannerGrid.Children.Add(_bannerAction.At(1));
        _banner.Child = bannerGrid;

        _tips.Margin = new Thickness(0, 28, 0, 0);

        var categories = Ui.Columns(3, 20);
        categories.Margin = new Thickness(0, 20, 0, 0);
        var column = 0;
        foreach (var group in TweakCatalog.ForThisPc.GroupBy(t => t.Category))
        {
            var stack = Ui.Stack(Ui.PanelTitle(group.Key));
            foreach (var tweak in group)
                stack.Children.Add(TweakRow(tweak));
            categories.Children.Add(Ui.Card(stack).At(column));
            column += 2;
        }

        var note = Ui.Text("Turning a tweak off puts back exactly what this PC had before. Tweaks marked ADMIN ask for permission once.", "CardHint");
        note.Margin = new Thickness(0, 14, 0, 0);
        note.TextWrapping = TextWrapping.Wrap;
        note.TextTrimming = TextTrimming.None;

        Content = Ui.Stack(header, _banner, _tips, categories, note);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Refresh();
        };
        Refresh();
    }

    public void Dispose()
    {
    }

    private void Refresh()
    {
        RenderTips();
        foreach (var tweak in TweakCatalog.ForThisPc)
            if (_switches.TryGetValue(tweak.Id, out var toggle))
                toggle.IsChecked = TweakEngine.IsApplied(tweak);
        _undoAll.IsEnabled = !_busy && TweakEngine.AppliedByDurazzno().Count > 0;
        RenderBanner();
    }

    // ── Tips ─────────────────────────────────────────────────────────────────────────────

    private void RenderTips()
    {
        var tips = Tips.ForThisPc(_app.Settings.Current);
        var stack = Ui.Stack(Ui.PanelTitle("TIPS FOR THIS PC"));
        if (tips.Count == 0)
        {
            var ok = Ui.Row(new LucideIcon { Icon = "circle-check", Width = 20, Height = 20, Foreground = Ui.B("Success") },
                Ui.Text("Nothing to fix right now. This PC is in good shape.", "Body", Ui.B("TextMuted")));
            ((TextBlock)ok.Children[1]).Margin = new Thickness(10, 0, 0, 0);
            ok.Margin = new Thickness(0, 14, 0, 0);
            stack.Children.Add(ok);
        }
        foreach (var tip in tips)
            stack.Children.Add(TipRow(tip));
        _tips.Content = Ui.Card(stack);
    }

    private UIElement TipRow(Tip tip)
    {
        var row = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(Ui.IconChip(tip.Icon, Ui.B(tip.Tone), Ui.B(tip.Tone + "Chip"), 40, 20));
        var text = Ui.Stack(Ui.Text(tip.Title, "Body", Ui.B("Text")), Ui.Text(tip.Text, "CardHint"));
        ((TextBlock)text.Children[0]).FontWeight = FontWeights.SemiBold;
        var detail = (TextBlock)text.Children[1];
        detail.TextWrapping = TextWrapping.Wrap;
        detail.TextTrimming = TextTrimming.None;
        text.Margin = new Thickness(14, 0, 16, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text.At(1));

        if (tip.ActionLabel is { } label)
        {
            var button = Action(tip.Action switch
            {
                TipAction.RestartWindows => "power",
                TipAction.TurnOnSystemRestore => "shield-check",
                _ => "chevron-right",
            }, label, "SecondaryButton", () => RunTipAsync(tip.Action));
            button.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(button.At(2));
        }
        return row;
    }

    private async Task RunTipAsync(TipAction action)
    {
        switch (action)
        {
            case TipAction.RestartWindows:
                RestartWindows();
                break;
            case TipAction.OpenDiskMap:
                _app.ShowMainWindow("diskmap");
                break;
            case TipAction.OpenDriverHub:
                _app.ShowMainWindow("driverhub");
                break;
            case TipAction.TurnOnSystemRestore:
                if (await SystemRestore.TurnOnAsync() is { } error && error != "Cancelled")
                    ShowError("Could not turn on System Restore", error);
                RenderTips();
                break;
        }
    }

    // ── Tweaks ───────────────────────────────────────────────────────────────────────────

    private UIElement TweakRow(Tweak tweak)
    {
        var name = Ui.Text(tweak.Name, "Body", Ui.B("Text"));
        name.FontWeight = FontWeights.SemiBold;
        var description = Ui.Text(tweak.Description, "CardHint");
        description.TextWrapping = TextWrapping.Wrap;
        description.TextTrimming = TextTrimming.None;
        description.Margin = new Thickness(0, 2, 0, 0);
        var text = Ui.Stack(name, description);

        var badges = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (tweak.NeedsAdmin)
            badges.Children.Add(Badge("ADMIN", "Warning"));
        if (tweak.Effect == TweakEffect.Explorer)
            badges.Children.Add(Badge("RESTARTS EXPLORER", "Info"));
        if (tweak.Effect == TweakEffect.Restart)
            badges.Children.Add(Badge("NEEDS A RESTART", "Info"));
        if (badges.Children.Count > 0)
            text.Children.Add(badges);

        var toggle = new CheckBox { Style = Ui.Res<Style>("ToggleSwitch"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 2, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(toggle, tweak.Name);
        toggle.Click += async (_, _) => await ToggleAsync(tweak, toggle);
        _switches[tweak.Id] = toggle;

        var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(text);
        row.Children.Add(toggle.At(1));
        return row;
    }

    private async Task ToggleAsync(Tweak tweak, CheckBox toggle)
    {
        var want = toggle.IsChecked == true;
        SetBusy(true);
        var error = want ? await TweakEngine.ApplyAsync(tweak) : await TweakEngine.UndoAsync(tweak);
        SetBusy(false);
        if (error is not null)
        {
            toggle.IsChecked = TweakEngine.IsApplied(tweak);
            if (error != "Cancelled")
                ShowError($"Could not change \"{tweak.Name}\"", error);
            return;
        }
        Changed(tweak);
        _undoAll.IsEnabled = TweakEngine.AppliedByDurazzno().Count > 0;
    }

    private async Task UndoAllAsync()
    {
        var ids = TweakEngine.AppliedByDurazzno().ToHashSet();
        var tweaks = TweakCatalog.All.Where(t => ids.Contains(t.Id)).ToList();
        if (tweaks.Count == 0 || !DurazznoDialog.Confirm(Window.GetWindow(this), "Undo all tweaks?",
                $"Turns off the {tweaks.Count} tweak{(tweaks.Count == 1 ? "" : "s")} Durazzno applied and puts back what this PC had before.",
                "Undo all", icon: "rotate-ccw", tone: "Warning"))
            return;

        SetBusy(true);
        var failed = new List<string>();
        foreach (var tweak in tweaks)
        {
            if (await TweakEngine.UndoAsync(tweak) is { } error)
                failed.Add(error == "Cancelled" ? $"{tweak.Name} (cancelled)" : $"{tweak.Name}: {error}");
            else
                Changed(tweak);
        }
        SetBusy(false);
        Refresh();
        if (failed.Count > 0)
            ShowError("Some tweaks were not undone", string.Join("\n", failed));
    }

    /// <summary>Remembers what the change needs to show (Explorer or Windows restart) for the banner.</summary>
    private void Changed(Tweak tweak)
    {
        _needsExplorer |= tweak.Effect == TweakEffect.Explorer;
        _needsRestart |= tweak.Effect == TweakEffect.Restart;
        RenderBanner();
    }

    private void RenderBanner()
    {
        if (_needsRestart)
        {
            _banner.Background = Ui.B("WarningChip");
            _bannerText.Foreground = Ui.B("Warning");
            _bannerText.Text = "Restart Windows to finish some of the changes.";
            _bannerAction.Content = Action("power", "RESTART NOW", "SecondaryButton", () => { RestartWindows(); return Task.CompletedTask; });
        }
        else if (_needsExplorer)
        {
            _banner.Background = Ui.B("InfoChip");
            _bannerText.Foreground = Ui.B("Info");
            _bannerText.Text = "Some changes show after File Explorer restarts. The taskbar blinks and open folders close.";
            _bannerAction.Content = Action("refresh-cw", "RESTART EXPLORER", "SecondaryButton", async () =>
            {
                SetBusy(true);
                await Task.Run(TweakEngine.RestartExplorer);
                _needsExplorer = false;
                SetBusy(false);
                RenderBanner();
            });
        }
        _banner.Visibility = _needsRestart || _needsExplorer ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var toggle in _switches.Values)
            toggle.IsEnabled = !busy;
        _undoAll.IsEnabled = !busy && TweakEngine.AppliedByDurazzno().Count > 0;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private void RestartWindows()
    {
        if (!DurazznoDialog.Confirm(Window.GetWindow(this), "Restart Windows now?",
                "Save your work first. Windows restarts right away.", "Restart", icon: "power", tone: "Warning"))
            return;
        try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { Log.Warn($"Could not restart Windows: {ex.Message}"); }
    }

    private void ShowError(string title, string message) =>
        DurazznoDialog.Show(Window.GetWindow(this), title, message, "triangle-alert", "Warning", [new DialogButton("Close", "PrimaryButton")]);

    private static Border Badge(string text, string tone) => new()
    {
        Background = Ui.B(tone + "Chip"),
        Padding = new Thickness(7, 2, 7, 3),
        Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = text, Foreground = Ui.B(tone), FontSize = 10.5, FontWeight = FontWeights.Bold },
    };

    private static Button Action(string icon, string text, string style, Func<Task> click)
    {
        var fg = style == "PrimaryButton" ? Ui.B("AccentText") : Ui.B("Text");
        var button = new Button
        {
            Style = Ui.Res<Style>(style),
            Content = Ui.Row(new LucideIcon { Icon = icon, Width = 15, Height = 15, Foreground = fg }, new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        button.Click += async (_, _) => await click();
        return button;
    }
}
