using System.Windows.Threading;
using Durazzno.Core;

namespace Durazzno.Hardware.Drivers;

/// <summary>
/// DriverHub's background check ("Notify about new drivers"): every 6 hours, day or week it runs the same search as
/// the Check for updates button and notifies only about updates it has not announced before. Off by default. The first
/// check after Durazzno starts waits 10 minutes so it never competes with Windows starting up.
/// </summary>
internal sealed class DriverWatch
{
    private readonly SettingsStore _settings;
    private readonly Action<string, string> _notify;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(10) };
    private bool _running;

    public DriverWatch(SettingsStore settings, Action<string, string> notify)
    {
        _settings = settings;
        _notify = notify;
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    /// <summary>Remembers what a manual check found, so the background check does not announce it again.</summary>
    public void MarkSeen(IEnumerable<DriverUpdate> updates) =>
        _settings.Update(s =>
        {
            s.DriverNotifiedIds = updates.Select(u => u.UpdateId).Distinct().ToList();
            s.DriverCheckLast = DateTime.Now;
        });

    private async Task TickAsync()
    {
        var s = _settings.Current;
        if (!s.DriverCheckNotify || _running)
            return;
        if (s.DriverCheckLast is { } last && DateTime.Now - last < TimeSpan.FromHours(Math.Max(1, s.DriverCheckHours)))
            return;

        _running = true;
        try
        {
            var devices = await Task.Run(DeviceScanner.Scan);
            await DriverSearch.RunAsync(devices, null, null, CancellationToken.None);
            var hidden = (s.HiddenDriverUpdates ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pending = PendingRestart.List(); // installed, waiting for a restart: not new
            var found = devices.Where(d => d.Update is not null && !hidden.Contains(d.Update.UpdateId) && PendingRestart.Find(pending, d.Update) is null).ToList();
            var known = (s.DriverNotifiedIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fresh = found.Where(d => !known.Contains(d.Update!.UpdateId)).ToList();
            MarkSeen(found.Select(d => d.Update!));
            Log.Info($"DriverHub background check: {found.Count} updates, {fresh.Count} new");

            if (fresh.Count == 0)
                return;
            var names = fresh.Select(d => d.Name).Distinct().ToList();
            if (names.Count == 1)
                _notify($"New driver for {names[0]}", $"{fresh[0].Update!.Title}. Open DriverHub to install it.");
            else
                _notify($"{names.Count} new driver updates",
                    string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" and {names.Count - 3} more" : "") + ". Open DriverHub to install them.");
        }
        catch (Exception ex)
        {
            Log.Warn($"DriverHub background check failed: {ex.Message}");
            _settings.Update(x => x.DriverCheckLast = DateTime.Now); // try again next interval, not every 10 minutes
        }
        finally
        {
            _running = false;
        }
    }
}
