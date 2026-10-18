using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Durazzno.Hardware;

/// <summary>Download / upload speed over all connected physical adapters, and the name of the main one.</summary>
internal sealed class NetworkMonitor
{
    private long _received;
    private long _sent;
    private readonly Stopwatch _clock = new();

    public NetworkMonitor() => Sample();

    /// <summary>Bytes per second since the previous call.</summary>
    public (double Down, double Up, string? Adapter, long? LinkSpeed) Sample()
    {
        long received = 0, sent = 0;
        NetworkInterface? main = null;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
                    continue;
                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
                // The main adapter is the one with a default gateway.
                if (main is null && nic.GetIPProperties().GatewayAddresses.Count > 0)
                    main = nic;
            }
        }
        catch (NetworkInformationException)
        {
        }

        var seconds = _clock.Elapsed.TotalSeconds;
        var down = seconds > 0 && _received > 0 ? Math.Max(0, (received - _received) / seconds) : 0;
        var up = seconds > 0 && _sent > 0 ? Math.Max(0, (sent - _sent) / seconds) : 0;
        _received = received;
        _sent = sent;
        _clock.Restart();
        return (down, up, main?.Name, main?.Speed > 0 ? main.Speed : null);
    }
}
