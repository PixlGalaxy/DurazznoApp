using System.Diagnostics;
using System.Runtime.InteropServices;
using Durazzno.Core;
using Microsoft.Win32;

namespace Durazzno.Hardware;

internal sealed record TimerInfo(double CurrentMs, double BestMs, double DefaultMs);

internal sealed record SleepTest(double AverageMs, double MinMs, double MaxMs);

/// <summary>
/// Windows timer resolution, the same way TimerResolution.exe does it: NtQueryTimerResolution / NtSetTimerResolution
/// from ntdll. Windows keeps a requested resolution only while the requesting process is alive, so the tray app
/// holds it. The process also opts out of timer throttling so Windows 11 does not drop the request while the
/// window is hidden.
/// </summary>
internal sealed class TimerResolutionService
{
    private const string KernelKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";

    private readonly SettingsStore _settings;

    public TimerResolutionService(SettingsStore settings)
    {
        _settings = settings;
        if (settings.Current.TimerResolutionOnStartup && settings.Current.TimerResolutionMs is { } ms)
            Apply(ms);
    }

    /// <summary>Resolution Durazzno is holding, or null when it is not requesting anything.</summary>
    public double? HeldMs { get; private set; }

    public event EventHandler? Changed;

    public static TimerInfo Query()
    {
        NtQueryTimerResolution(out var coarsest, out var finest, out var current);
        return new TimerInfo(current / 10_000.0, finest / 10_000.0, coarsest / 10_000.0);
    }

    /// <summary>Requests a resolution (in ms) and keeps holding it. Returns the resolution Windows actually set.</summary>
    public double Apply(double ms)
    {
        var info = Query();
        ms = Math.Clamp(ms, info.BestMs, info.DefaultMs);

        if (HeldMs is not null)
            NtSetTimerResolution(0, false, out _); // Release the previous request first.

        KeepTimerWhenHidden();
        NtSetTimerResolution((uint)Math.Round(ms * 10_000), true, out var actual);
        HeldMs = ms;
        _settings.Update(s => s.TimerResolutionMs = ms);
        Log.Info($"Timer resolution requested {ms:0.###} ms, now {actual / 10_000.0:0.###} ms");
        Changed?.Invoke(this, EventArgs.Empty);
        return actual / 10_000.0;
    }

    /// <summary>Stops requesting a resolution (Windows goes back to the default once nobody else asks).</summary>
    public void Release()
    {
        if (HeldMs is not null)
            NtSetTimerResolution(0, false, out _);
        HeldMs = null;
        _settings.Update(s => s.TimerResolutionMs = null);
        Log.Info("Timer resolution released");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetApplyOnStartup(bool value) => _settings.Update(s => s.TimerResolutionOnStartup = value);

    /// <summary>Measures how long Sleep(1) really takes (what games and apps actually feel).</summary>
    public static Task<SleepTest> MeasureSleepAsync(int samples = 120) => Task.Run(() =>
    {
        var times = new double[samples];
        var watch = new Stopwatch();
        for (var i = 0; i < samples; i++)
        {
            watch.Restart();
            Thread.Sleep(1);
            times[i] = watch.Elapsed.TotalMilliseconds;
        }

        return new SleepTest(times.Average(), times.Min(), times.Max());
    });

    // ── Windows 11 global requests ────────────────────────────────────────────────────────

    /// <summary>Windows 11 / Server 2022 can make timer requests global again (GlobalTimerResolutionRequests).</summary>
    public static bool SupportsGlobalRequests => Environment.OSVersion.Version.Build >= 22000;

    public static bool GlobalRequestsEnabled
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(KernelKey);
            return key?.GetValue("GlobalTimerResolutionRequests") is int v && v == 1;
        }
    }

    /// <summary>Writes the registry value through an elevated reg.exe. Takes effect after a restart.</summary>
    public static async Task<string?> SetGlobalRequestsAsync(bool enabled)
    {
        var args = enabled
            ? $"add \"HKLM\\{KernelKey}\" /v GlobalTimerResolutionRequests /t REG_DWORD /d 1 /f"
            : $"delete \"HKLM\\{KernelKey}\" /v GlobalTimerResolutionRequests /f";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("reg.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (process is null)
                return "Could not start reg.exe.";
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? null : $"reg.exe failed (exit code {process.ExitCode}).";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Cancelled";
        }
    }

    // ── Native ────────────────────────────────────────────────────────────────────────────

    /// <summary>PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION: keep the request even with no visible window.</summary>
    private static void KeepTimerWhenHidden()
    {
        var state = new PowerThrottlingState { Version = 1, ControlMask = 0x4, StateMask = 0 };
        SetProcessInformation(Process.GetCurrentProcess().Handle, 4 /* ProcessPowerThrottling */, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);

    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desired, [MarshalAs(UnmanagedType.U1)] bool set, out uint current);
}
