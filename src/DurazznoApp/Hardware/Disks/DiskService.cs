using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Durazzno.Core;

namespace Durazzno.Hardware.Disks;

/// <summary>
/// Gives the UI disk reports. The app itself runs without admin rights, so for full S.M.A.R.T. it can start an
/// elevated copy of itself (<c>--disk-agent</c>, one UAC prompt per session) and ask it over a named pipe that only
/// the current user can open. The agent exits when the app does.
/// </summary>
internal sealed class DiskService
{
    public const string AgentFlag = "--disk-agent";
    public const string DumpFlag = "--disk-dump";

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    private readonly string _pipeName = $"DurazznoApp.DiskAgent.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private bool _agentStarted;

    /// <summary>Full S.M.A.R.T. is available (elevated app or agent running).</summary>
    public bool HasAdminAccess => DiskScanner.IsElevated || _agentStarted;

    public async Task<List<DiskReport>> ScanAsync()
    {
        if (!DiskScanner.IsElevated && _agentStarted)
        {
            try
            {
                return await RequestFromAgentAsync();
            }
            catch (Exception ex)
            {
                Log.Warn($"Disk agent unavailable, scanning without admin rights: {ex.Message}");
                _agentStarted = false;
            }
        }

        return await Task.Run(DiskScanner.Scan);
    }

    /// <summary>Starts the elevated agent. Returns false if the user declined the UAC prompt.</summary>
    public async Task<bool> StartAgentAsync()
    {
        if (HasAdminAccess)
            return true;

        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.ExePath, $"{AgentFlag} {_pipeName} {Environment.ProcessId}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // UAC cancelled.
        }

        // Wait until the agent's pipe accepts connections.
        for (var i = 0; i < 40; i++)
        {
            try
            {
                await using var probe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
                await probe.ConnectAsync(250);
                await using var writer = new StreamWriter(probe) { AutoFlush = true };
                await writer.WriteLineAsync("ping");
                _agentStarted = true;
                Log.Info("Disk agent started");
                return true;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
                await Task.Delay(250);
            }
        }

        Log.Warn("Disk agent did not start in time");
        return false;
    }

    private async Task<List<DiskReport>> RequestFromAgentAsync()
    {
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);
        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync("scan");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Agent closed the connection.");
        return JsonSerializer.Deserialize<List<DiskReport>>(line, Json) ?? [];
    }

    // ── Agent / dump entry points (run from Program.Main) ──────────────────────────────────

    /// <summary>Elevated helper loop: answers "scan" requests until the parent app exits.</summary>
    public static int RunAgent(string pipeName, int parentPid)
    {
        Log.Info($"Disk agent running (elevated: {DiskScanner.IsElevated})");
        using var stop = new CancellationTokenSource();
        try
        {
            var parent = Process.GetProcessById(parentPid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => stop.Cancel();
            if (parent.HasExited)
                return 0;
        }
        catch (ArgumentException)
        {
            return 0; // Parent already gone.
        }

        // Only the user who launched the app (same SID, elevated or not) may connect.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                server.WaitForConnectionAsync(stop.Token).GetAwaiter().GetResult();

                using var reader = new StreamReader(server);
                using var writer = new StreamWriter(server) { AutoFlush = true };
                if (reader.ReadLine() == "scan")
                    writer.WriteLine(JsonSerializer.Serialize(DiskScanner.Scan(), Json));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Disk agent: {ex.Message}");
            }
        }

        Log.Info("Disk agent exiting");
        return 0;
    }

    /// <summary>Writes a JSON report of every disk to <paramref name="path"/> (diagnostics).</summary>
    public static int RunDump(string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(DiskScanner.Scan(), options));
        return 0;
    }
}
