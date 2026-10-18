using System.ComponentModel;
using System.Diagnostics;
using Durazzno.Core;

namespace Durazzno.Hardware.Drivers;

/// <summary>A driver backup folder made by BACKUP DRIVERS.</summary>
internal sealed record DriverBackup(string Folder, DateTime Created, int Packages, long SizeBytes)
{
    public string Name => Path.GetFileName(Folder);
}

/// <summary>
/// Driver backups: BACKUP DRIVERS exports every third-party driver package (pnputil /export-driver) into
/// Documents\Durazzno App\Driver Backups\&lt;PC&gt; &lt;date&gt;, and a backup can be put back with
/// pnputil /add-driver *.inf /subdirs /install (both elevated), e.g. after reinstalling Windows.
/// </summary>
internal static class DriverBackups
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Durazzno App", "Driver Backups");

    public static List<DriverBackup> List()
    {
        if (!Directory.Exists(Root))
            return [];
        return Directory.EnumerateDirectories(Root)
            .Select(folder =>
            {
                var infs = Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Count();
                var size = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                return new DriverBackup(folder, Directory.GetCreationTime(folder), infs, size);
            })
            .Where(b => b.Packages > 0)
            .OrderByDescending(b => b.Created)
            .ToList();
    }

    public static string NewFolder() =>
        Path.Combine(Root, $"{SystemInfo.MachineName} {DateTime.Now:yyyy-MM-dd HH-mm}");

    /// <summary>Installs every driver in the backup (elevated). Returns (error or null, restart needed).</summary>
    public static async Task<(string? Error, bool Reboot)> RestoreAsync(DriverBackup backup)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("pnputil.exe", $"/add-driver \"{Path.Combine(backup.Folder, "*.inf")}\" /subdirs /install")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
                return ("Could not start pnputil.", false);
            await process.WaitForExitAsync();
            // 0 = done, 3010 = done, restart needed; 259 = nothing newer to install (already up to date).
            return process.ExitCode switch
            {
                0 or 259 => (null, false),
                3010 => (null, true),
                var code => ($"pnputil finished with code {code}", false),
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ("Cancelled", false);
        }
    }
}
