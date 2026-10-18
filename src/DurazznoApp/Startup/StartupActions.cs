using System.ComponentModel;
using System.Diagnostics;
using Durazzno.Core;
using Microsoft.Win32;

namespace Durazzno.Startup;

/// <summary>
/// Enable / disable / delete. Logon entries use the StartupApproved values that Task Manager uses, so both tools
/// always agree and nothing is lost when an item is turned off. Per-user changes are made directly; machine-wide
/// ones go through reg.exe / schtasks.exe / sc.exe with a UAC prompt for that one change.
/// </summary>
internal static class StartupActions
{
    /// <summary>Returns an error message, or null on success. A declined UAC prompt returns "Cancelled".</summary>
    public static async Task<string?> SetEnabledAsync(StartupEntry e, bool enabled)
    {
        try
        {
            return e.Source switch
            {
                EntrySource.RunKey or EntrySource.StartupFolder => await SetApprovedAsync(e, enabled),
                EntrySource.Task => await SetTaskAsync(e, enabled),
                EntrySource.Service => await SetServiceAsync(e, enabled),
                _ => "This entry cannot be switched off from here.",
            };
        }
        catch (Exception ex)
        {
            Log.Error($"StartupHub toggle failed: {e.Name}", ex);
            return ex.Message;
        }
    }

    public static async Task<string?> DeleteAsync(StartupEntry e)
    {
        try
        {
            if (e.Source is EntrySource.RunKey or EntrySource.RunOnceKey)
            {
                var hive = e.Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
                var reg = e.View == RegistryView.Registry32 ? " /reg:32" : " /reg:64";
                if (!e.Machine)
                {
                    using var key = Registry.CurrentUser.OpenSubKey(e.KeyPath, writable: true);
                    key?.DeleteValue(e.ValueName, throwOnMissingValue: false);
                }
                else if (await ElevatedAsync("reg.exe", $"delete \"{hive}\\{e.KeyPath}\" /v \"{e.ValueName}\" /f{reg}") is { } error)
                {
                    return error;
                }
            }
            else if (e.Source == EntrySource.StartupFolder && e.FilePath is { } file)
            {
                if (!e.Machine)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else if (await ElevatedAsync("cmd.exe", $"/c del /f /q \"{file}\"") is { } error)
                    return error;
            }
            else
            {
                return "Only logon entries can be deleted here. Switch it off instead.";
            }

            if (e.ApprovedKey is not null)
                await RemoveApprovedAsync(e);
            Log.Info($"StartupHub: deleted {e.Name} ({e.Location})");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"StartupHub delete failed: {e.Name}", ex);
            return ex.Message;
        }
    }

    // ── Logon (StartupApproved) ───────────────────────────────────────────────────────────

    private static async Task<string?> SetApprovedAsync(StartupEntry e, bool enabled)
    {
        // 02 = enabled; 03 + FILETIME of the change = disabled (the format Task Manager writes).
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled)
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);

        var path = $@"{StartupScanner.ApprovedRoot}\{e.ApprovedKey}";
        if (!e.Machine)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue(e.ValueName, data, RegistryValueKind.Binary);
            return null;
        }

        return await ElevatedAsync("reg.exe", $"add \"HKLM\\{path}\" /v \"{e.ValueName}\" /t REG_BINARY /d {Convert.ToHexString(data)} /f /reg:64");
    }

    private static async Task RemoveApprovedAsync(StartupEntry e)
    {
        var path = $@"{StartupScanner.ApprovedRoot}\{e.ApprovedKey}";
        if (!e.Machine)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            key?.DeleteValue(e.ValueName, throwOnMissingValue: false);
        }
        else
        {
            await ElevatedAsync("reg.exe", $"delete \"HKLM\\{path}\" /v \"{e.ValueName}\" /f /reg:64");
        }
    }

    // ── Tasks ─────────────────────────────────────────────────────────────────────────────

    private static async Task<string?> SetTaskAsync(StartupEntry e, bool enabled)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service")!;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            var folderPath = e.TaskPath![..Math.Max(1, e.TaskPath!.LastIndexOf('\\'))];
            dynamic task = service.GetFolder(folderPath).GetTask(e.TaskPath![(e.TaskPath!.LastIndexOf('\\') + 1)..]);
            task.Enabled = enabled;
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070005))
        {
            // System tasks need admin rights.
            return await ElevatedAsync("schtasks.exe", $"/Change /TN \"{e.TaskPath}\" {(enabled ? "/ENABLE" : "/DISABLE")}");
        }
    }

    // ── Services ──────────────────────────────────────────────────────────────────────────

    private static async Task<string?> SetServiceAsync(StartupEntry e, bool enabled)
    {
        // Off = manual start (the service still works when something needs it), never "Disabled".
        var error = await ElevatedAsync("sc.exe", $"config \"{e.ServiceName}\" start= {(enabled ? "auto" : "demand")}");
        if (error is not null)
            return error;

        var mine = StartupScanner.LoadDisabledServices();
        if (enabled)
            mine.Remove(e.ServiceName!);
        else
            mine.Add(e.ServiceName!);
        StartupScanner.SaveDisabledServices(mine);
        return null;
    }

    // ── Elevation ─────────────────────────────────────────────────────────────────────────

    private static async Task<string?> ElevatedAsync(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
                return "Could not start the elevated helper.";
            await process.WaitForExitAsync();
            Log.Info($"StartupHub: {file} {arguments} -> {process.ExitCode}");
            return process.ExitCode == 0 ? null : $"{Path.GetFileNameWithoutExtension(file)} failed (exit code {process.ExitCode}).";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Cancelled";
        }
    }
}
