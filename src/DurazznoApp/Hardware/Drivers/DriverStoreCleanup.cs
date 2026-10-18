using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Durazzno.Core;

namespace Durazzno.Hardware.Drivers;

/// <summary>A third-party driver package in the Windows driver store (oemNN.inf).</summary>
internal sealed record DriverPackage(
    string PublishedName,   // oem42.inf
    string OriginalName,    // netwtw08.inf
    string Provider,
    string ClassName,
    string Version,
    DateTime? Date,
    long SizeBytes,
    string StorePath,
    bool IsSuperseded);     // an older version of a driver that is in use

/// <summary>
/// Driver store cleanup, like Driver Store Explorer: Windows keeps every driver package it ever installed in
/// System32\DriverStore\FileRepository, often several GB. A package not used by any connected device can be deleted
/// (SetupUninstallOEMInf, elevated); Windows refuses for packages still in use. Deleting older versions also removes
/// them from a device's rollback list, which the page says before deleting.
/// </summary>
internal static partial class DriverStoreCleanup
{
    public const string DeleteFlag = "--delete-driver-packages"; // --delete-driver-packages <names.txt> <result.json>

    /// <summary>Third-party packages not used by any present device.</summary>
    public static List<DriverPackage> FindUnused(IEnumerable<DeviceInfo> devices)
    {
        var inUse = devices.Where(d => d.IsPresent && d.InfName is not null)
            .Select(d => Path.GetFileName(d.InfName!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

        var all = new List<(DriverPackage Package, bool Used)>();
        foreach (var inf in Directory.EnumerateFiles(infDir, "oem*.inf"))
        {
            try
            {
                var name = Path.GetFileName(inf);
                var store = StoreLocation(inf);
                var (provider, cls, version, date) = ReadVersionSection(inf);
                var original = store is null ? name : Path.GetFileName(store);
                var size = store is not null && Directory.Exists(Path.GetDirectoryName(store))
                    ? Directory.EnumerateFiles(Path.GetDirectoryName(store)!, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                    : new FileInfo(inf).Length;
                all.Add((new DriverPackage(name, original, provider, cls, version, date, size, store is null ? inf : Path.GetDirectoryName(store)!, false),
                    inUse.Contains(name)));
            }
            catch (Exception ex)
            {
                Log.Warn($"Driver package {inf}: {ex.Message}");
            }
        }

        // Superseded: the same driver (original INF name + provider) is installed in another, used package.
        var usedKeys = all.Where(x => x.Used).Select(x => (x.Package.OriginalName.ToLowerInvariant(), x.Package.Provider.ToLowerInvariant())).ToHashSet();
        return all.Where(x => !x.Used)
            .Select(x => x.Package with { IsSuperseded = usedKeys.Contains((x.Package.OriginalName.ToLowerInvariant(), x.Package.Provider.ToLowerInvariant())) })
            .OrderByDescending(p => p.IsSuperseded).ThenByDescending(p => p.SizeBytes)
            .ToList();
    }

    /// <summary>Deletes the packages through an elevated helper. Null when UAC was declined.</summary>
    public static async Task<List<RemoveResult>?> DeleteAsync(IReadOnlyCollection<string> publishedNames)
    {
        var list = Path.Combine(Path.GetTempPath(), $"DurazznoPackages-{Guid.NewGuid():N}.txt");
        var result = Path.ChangeExtension(list, ".json");
        try
        {
            await File.WriteAllLinesAsync(list, publishedNames);
            using var process = Process.Start(new ProcessStartInfo(AppInfo.ExePath, $"{DeleteFlag} \"{list}\" \"{result}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
                return [];
            await process.WaitForExitAsync();
            return File.Exists(result) ? JsonSerializer.Deserialize<List<RemoveResult>>(await File.ReadAllTextAsync(result)) : [];
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null;
        }
        finally
        {
            try { File.Delete(list); } catch { }
            try { File.Delete(result); } catch { }
        }
    }

    /// <summary>Elevated entry point. Never forces: packages in use stay.</summary>
    public static int RunDelete(string listFile, string resultFile)
    {
        var results = new List<RemoveResult>();
        foreach (var name in File.ReadAllLines(listFile).Where(l => OemName().IsMatch(l)))
        {
            if (SetupUninstallOEMInfW(name, 0, IntPtr.Zero))
            {
                results.Add(new RemoveResult(name, true, "Deleted"));
                continue;
            }
            var error = Marshal.GetLastWin32Error();
            results.Add(new RemoveResult(name, false, unchecked((uint)error) == 0xE000023D
                ? "Still used by a device" : new Win32Exception(error).Message));
        }
        Log.Info($"DriverHub: deleted {results.Count(r => r.Success)} of {results.Count} driver packages");
        File.WriteAllText(resultFile, JsonSerializer.Serialize(results));
        return 0;
    }

    private static string? StoreLocation(string inf)
    {
        var buffer = new char[1024];
        return SetupGetInfDriverStoreLocationW(inf, IntPtr.Zero, null, buffer, buffer.Length, out var required) && required > 1
            ? new string(buffer, 0, required - 1)
            : null;
    }

    /// <summary>[Version] Provider / Class / DriverVer, resolving %tokens% from [Strings].</summary>
    private static (string Provider, string Class, string Version, DateTime? Date) ReadVersionSection(string inf)
    {
        var text = File.ReadAllText(inf);
        string Value(string key) => Regex.Match(text, $@"(?im)^\s*{key}\s*=\s*(.+?)\s*(;.*)?$") is { Success: true } m ? m.Groups[1].Value.Trim() : "";
        string Resolve(string v)
        {
            var token = Regex.Match(v, "^%(.+)%$");
            if (!token.Success)
                return v.Trim('"');
            var s = Regex.Match(text, $@"(?im)^\s*{Regex.Escape(token.Groups[1].Value)}\s*=\s*""?(.+?)""?\s*$");
            return s.Success ? s.Groups[1].Value : v;
        }

        var driverVer = Value("DriverVer").Split(',');
        DateTime? date = DateTime.TryParse(driverVer[0], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        return (Resolve(Value("Provider")), Value("Class"), driverVer.Length > 1 ? driverVer[1].Trim() : "", date);
    }

    [GeneratedRegex(@"^oem\d+\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex OemName();

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupGetInfDriverStoreLocationW(string fileName, IntPtr altPlatform, string? localeName, char[] returnBuffer, int size, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupUninstallOEMInfW(string infFileName, uint flags, IntPtr reserved);
}
