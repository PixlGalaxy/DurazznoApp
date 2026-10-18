using System.Net.NetworkInformation;
using System.Windows.Threading;
using Durazzno.Core;
using Durazzno.Updates;

namespace Durazzno.Network;

internal enum ConnectionState
{
    Checking,
    Online,
    ServerUnreachable,
    NoInternet,
}

/// <summary>
/// Keeps track of Webin Cloud Server without loading it: a HEAD "ping" every 10 seconds keeps the status dot live,
/// while the full (signed) manifest is read only on start, every 15 minutes, when the server comes back, on a network
/// change and when the user asks. Must be created on the UI thread; <see cref="Changed"/> is raised there.
/// </summary>
internal sealed class ConnectivityMonitor : IDisposable
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ManifestInterval = TimeSpan.FromMinutes(15);

    private readonly Func<Uri> _manifestUri;
    private readonly SynchronizationContext _ui;
    private readonly DispatcherTimer _ping = new() { Interval = PingInterval };
    private readonly DispatcherTimer _networkDebounce = new() { Interval = TimeSpan.FromSeconds(3) };
    private Task? _running;
    private DateTime _lastManifest = DateTime.MinValue;
    private bool _pinging;

    public ConnectivityMonitor(Func<Uri> manifestUri)
    {
        _manifestUri = manifestUri;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Create on the UI thread.");

        _ping.Tick += async (_, _) => await PingAsync();
        _networkDebounce.Tick += (_, _) =>
        {
            _networkDebounce.Stop();
            _ = RefreshAsync();
        };

        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _ping.Start();
    }

    public ConnectionState State { get; private set; } = ConnectionState.Checking;

    /// <summary>Latest manifest; null unless <see cref="State"/> is Online.</summary>
    public UpdateManifest? Manifest { get; private set; }

    public string? LastError { get; private set; }

    public DateTime? LastChecked { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Reads the manifest now, or joins the read already in progress.</summary>
    public Task RefreshAsync() => _running is { IsCompleted: false } ? _running : _running = CheckAsync();

    private async Task PingAsync()
    {
        if (_pinging || _running is { IsCompleted: false })
            return;
        _pinging = true;
        try
        {
            if (State == ConnectionState.Online && DateTime.Now - _lastManifest >= ManifestInterval)
            {
                await RefreshAsync();
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reachable = await ServerClient.PingAsync(_manifestUri(), cts.Token);
            // Server answers but the manifest was missing or invalid last time: retry that at most once a minute.
            if (reachable && State != ConnectionState.Online && DateTime.Now - _lastManifest < TimeSpan.FromMinutes(1) && LastError is not null
                && !LastError.Contains("Connection", StringComparison.OrdinalIgnoreCase))
                return;
            if (reachable != (State == ConnectionState.Online))
                await RefreshAsync(); // came back, or just went away: read the manifest / find out why
        }
        finally
        {
            _pinging = false;
        }
    }

    private async Task CheckAsync()
    {
        var previous = State;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Manifest = await ServerClient.GetManifestAsync(_manifestUri(), cts.Token);
            State = ConnectionState.Online;
            LastError = null;
        }
        catch (Exception ex)
        {
            Manifest = null;
            LastError = ex.Message;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            State = await ServerClient.HasInternetAsync(cts.Token) ? ConnectionState.ServerUnreachable : ConnectionState.NoInternet;
        }

        _lastManifest = DateTime.Now;
        LastChecked = DateTime.Now;
        if (State != previous)
            Log.Info($"Connection: {previous} -> {State}{(LastError is null ? "" : $" ({LastError})")}");

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Raised on a thread-pool thread; bounce to the UI thread and debounce bursts of events.
    private void OnNetworkChanged(object? sender, EventArgs e) => _ui.Post(_ =>
    {
        _networkDebounce.Stop();
        _networkDebounce.Start();
    }, null);

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _ping.Stop();
        _networkDebounce.Stop();
    }
}
