using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Durazzno.Core;

namespace Durazzno.Hardware.Storage;

/// <summary>Something DiskMap can clean: a set of folders (or the Recycle Bin) that only holds disposable files.</summary>
internal sealed record CleanupCategory(string Id, string Name, string Description, bool NeedsAdmin, string[] Folders, TimeSpan? MinAge = null)
{
    public bool IsRecycleBin => Id == "recyclebin";
}

/// <summary>
/// Disk cleanup for things that are safe to delete: temp files, the Recycle Bin, the Windows Update download cache,
/// crash dumps and error reports, and leftovers from driver installers. Only files are deleted (folders are kept, then
/// emptied subfolders removed); files in use are skipped. Categories under Windows / ProgramData need admin rights and
/// run in one elevated helper (--cleanup), so there is a single UAC prompt.
/// </summary>
internal static class CleanupService
{
    public const string Flag = "--cleanup"; // --cleanup <id,id,...> <result.txt>

    private static string Env(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
    private static string Windows => Env(Environment.SpecialFolder.Windows);
    private static string ProgramData => Env(Environment.SpecialFolder.CommonApplicationData);
    private static string LocalAppData => Env(Environment.SpecialFolder.LocalApplicationData);
    private static string SystemDrive => Path.GetPathRoot(Windows) ?? @"C:\";

    public static readonly IReadOnlyList<CleanupCategory> All =
    [
        new("usertemp", "Temporary files", "Your temp folder. Only files older than a day, so running installers keep theirs.", false,
            [Path.GetTempPath()], TimeSpan.FromDays(1)),
        new("recyclebin", "Recycle Bin", "Everything in the Recycle Bin on every drive.", false, []),
        new("crashdumps", "Crash dumps", "Memory dumps apps left when they crashed.", false,
            [Path.Combine(LocalAppData, "CrashDumps")]),
        new("wintemp", "Windows temporary files", "Windows' own temp folder. Files older than a day.", true,
            [Path.Combine(Windows, "Temp")], TimeSpan.FromDays(1)),
        new("wucache", "Windows Update cache", "Updates that were already downloaded and installed. Windows downloads again anything it still needs.", true,
            [Path.Combine(Windows, "SoftwareDistribution", "Download")]),
        new("wer", "Error reports", "Windows Error Reporting archives and queued reports.", true,
            [Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"), Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue")]),
        new("driverleftovers", "Driver installer leftovers", "Unpacked NVIDIA / AMD installers (C:\\NVIDIA, C:\\AMD) and DriverHub downloads.", true,
            [Path.Combine(SystemDrive, "NVIDIA"), Path.Combine(SystemDrive, "AMD"),
             Path.Combine(ProgramData, "DurazznoApp", "Drivers")]),
    ];

    /// <summary>Bytes the category would free now (files in use included: they are skipped when cleaning).</summary>
    public static long Measure(CleanupCategory category)
    {
        if (category.IsRecycleBin)
            return RecycleBinSize();
        long total = 0;
        foreach (var file in Files(category))
            total += file.Length;
        return total;
    }

    /// <summary>Cleans categories that need no admin rights. Returns the bytes freed.</summary>
    public static long Clean(CleanupCategory category)
    {
        if (category.IsRecycleBin)
        {
            var size = RecycleBinSize();
            var hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return hr is 0 or unchecked((int)0x8000FFFF) ? size : 0; // E_UNEXPECTED = already empty
        }

        long freed = 0;
        foreach (var file in Files(category))
        {
            try
            {
                var length = file.Length;
                file.Attributes = FileAttributes.Normal;
                file.Delete();
                freed += length;
            }
            catch
            {
                // in use or protected: skip it
            }
        }
        foreach (var folder in category.Folders)
            RemoveEmptyFolders(folder, keepRoot: !IsLeftoverRoot(folder));
        return freed;
    }

    /// <summary>Runs the admin categories in an elevated copy of the app. Returns bytes freed, or an error / "Cancelled".</summary>
    public static async Task<(long Freed, string? Error)> CleanElevatedAsync(IEnumerable<CleanupCategory> categories)
    {
        var result = Path.Combine(Path.GetTempPath(), $"DurazznoCleanup-{Guid.NewGuid():N}.txt");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(AppInfo.ExePath, $"{Flag} {string.Join(',', categories.Select(c => c.Id))} \"{result}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
                return (0, "Could not start the cleanup.");
            await process.WaitForExitAsync();
            var text = File.Exists(result) ? (await File.ReadAllTextAsync(result)).Trim() : "";
            return long.TryParse(text, out var freed) ? (freed, null) : (0, text.Length > 0 ? text : "The cleanup did not report back.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (0, "Cancelled");
        }
        finally
        {
            try { File.Delete(result); } catch { }
        }
    }

    /// <summary>Elevated entry point.</summary>
    public static int RunElevated(string ids, string resultFile)
    {
        long freed = 0;
        try
        {
            foreach (var id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (All.FirstOrDefault(c => c.Id == id) is { } category)
                    freed += Clean(category);
            Log.Info($"Cleanup ({ids}): freed {SystemInfo.FormatBytes(freed)}");
            File.WriteAllText(resultFile, freed.ToString());
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultFile, ex.Message);
        }
        return 0;
    }

    private static IEnumerable<FileInfo> Files(CleanupCategory category)
    {
        var cutoff = category.MinAge is { } age ? DateTime.Now - age : DateTime.MaxValue;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in category.Folders)
        {
            if (!Directory.Exists(folder))
                continue;
            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(folder).EnumerateFiles("*", options);
            }
            catch
            {
                continue;
            }
            foreach (var file in files)
            {
                DateTime written;
                try { written = file.LastWriteTime; } catch { continue; }
                if (category.MinAge is null || written < cutoff)
                    yield return file;
            }
        }
    }

    // C:\NVIDIA and C:\AMD exist only as installer leftovers: remove the folder itself once it is empty.
    private static bool IsLeftoverRoot(string folder) =>
        folder.Equals(Path.Combine(SystemDrive, "NVIDIA"), StringComparison.OrdinalIgnoreCase)
        || folder.Equals(Path.Combine(SystemDrive, "AMD"), StringComparison.OrdinalIgnoreCase);

    private static void RemoveEmptyFolders(string folder, bool keepRoot)
    {
        if (!Directory.Exists(folder))
            return;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(folder))
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0)
                    RemoveEmptyFolders(sub, keepRoot: false);
            if (!keepRoot && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch
        {
            // in use: leave it
        }
    }

    private static long RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    [StructLayout(LayoutKind.Sequential)] // x64 layout (shellapi.h packs it to 8 on 64-bit)
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
