using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Durazzno.Core;
using Microsoft.Win32;

namespace Durazzno.Hardware.Drivers;

/// <summary>A manufacturer installer downloaded and verified, ready for the elevated helper to run.</summary>
internal sealed record VendorInstall(string UpdateId, string Title, string ExePath, string Arguments, bool Interactive);

/// <summary>
/// Drivers straight from the chip makers, which publish newer versions than the Microsoft Update Catalog:
///  • Intel: the public data package Intel Driver &amp; Support Assistant downloads (dsadata.intel.com/data/en):
///    software-configurations.json lists each driver's hardware ID patterns, version, supported Windows builds,
///    installer URL on downloadmirror.intel.com and its SHA-1.
///  • NVIDIA: the GeForce Experience lookup by GPU device ID (gfwsl.geforce.com ... getDispDrvrByDevid), which
///    returns the newest WHQL Game Ready driver for that GPU, laptop or desktop.
///  • AMD: the official driver pages on amd.com (no public API): the WHQL Recommended AMD Software: Adrenalin Edition
///    for RDNA Radeon GPUs, and AMD Chipset Software (one package for every Ryzen platform: chipset, CPU power
///    management, GPIO, PSP...), compared against the installed package versions.
/// Installers must carry the maker's Authenticode signature before they run.
/// </summary>
internal static partial class VendorDrivers
{
    private const string IntelData = "https://dsadata.intel.com/data/en";
    private const string NvidiaLookup = "https://gfwsl.geforce.com/nvidia_web_services/controller.gfeclientcontent.NG.php/com.nvidia.services.GFEClientContent_NG.getDispDrvrByDevid/";

    public static async Task<List<DriverUpdate>> SearchAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct)
    {
        var results = new List<DriverUpdate>();
        var intel = SearchIntelAsync(devices, ct);
        var nvidia = SearchNvidiaAsync(devices, ct);
        var amd = SearchAmdAsync(devices, ct);
        var oem = OemCatalogs.SearchAsync(devices, ct); // Dell / HP / Lenovo, when this PC is one
        foreach (var task in new[] { intel, nvidia, amd, oem })
        {
            try
            {
                results.AddRange(await task);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warn($"Vendor driver lookup: {ex.Message}");
            }
        }
        return results;
    }

    // ── Intel ─────────────────────────────────────────────────────────────────────────────

    private sealed record IntelOs(string DsaId, int? BuildMin, int? BuildMax, bool Is64Bit, bool IsServer);

    private static async Task<List<DriverUpdate>> SearchIntelAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct)
    {
        var bytes = await DriverCatalog.Http.GetByteArrayAsync(IntelData, ct);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        JsonElement Read(string name)
        {
            using var stream = zip.GetEntry(name)?.Open() ?? throw new InvalidDataException($"{name} missing from Intel's data package.");
            return JsonDocument.Parse(stream).RootElement.Clone();
        }

        var osIds = Read("os.json").GetProperty("OperatingSystems").EnumerateArray()
            .Select(o => new IntelOs(
                o.GetProperty("DsaId").GetString() ?? "",
                o.TryGetProperty("BuildMin", out var min) && min.ValueKind == JsonValueKind.Number ? min.GetInt32() : null,
                o.TryGetProperty("BuildMax", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetInt32() : null,
                o.TryGetProperty("Is64Bit", out var x64) && x64.ValueKind == JsonValueKind.True,
                o.TryGetProperty("IsServer", out var server) && server.ValueKind == JsonValueKind.True))
            .Where(o => o.BuildMin is { } lo && lo <= DriverCatalog.OsBuild && (o.BuildMax ?? int.MaxValue) >= DriverCatalog.OsBuild
                        && o.Is64Bit == Environment.Is64BitOperatingSystem && !o.IsServer)
            .Select(o => o.DsaId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var updates = new List<DriverUpdate>();
        foreach (var entry in Read("software-configurations.json").EnumerateArray())
        {
            try
            {
                if (entry.GetProperty("IsBeta").GetBoolean())
                    continue;
                var file = entry.GetProperty("Files").EnumerateArray()
                    .FirstOrDefault(f => f.GetProperty("OperatingSystems").EnumerateArray().Any(o => osIds.Contains(o.GetString() ?? "")));
                if (file.ValueKind != JsonValueKind.Object)
                    continue;

                var id = entry.GetProperty("Id").GetInt32();
                var name = Clean(entry.GetProperty("Name").GetString());
                var date = entry.TryGetProperty("DisplayReleaseDate", out var d) && d.TryGetDateTime(out var dt) ? dt.Date : (DateTime?)null;
                var silent = entry.GetProperty("AllowSilentInstallation").GetBoolean();
                var options = entry.TryGetProperty("InstallOptions", out var o) ? o.GetString() ?? "" : "";

                foreach (var component in entry.GetProperty("Components").EnumerateArray())
                {
                    if (component.GetProperty("DetectionType").GetString() != "Default")
                        continue; // Guid / Custom detections are applications, not device drivers
                    var version = component.GetProperty("Version").GetString();
                    if (string.IsNullOrEmpty(version) || !Version.TryParse(version, out var offered))
                        continue;
                    var mask = component.TryGetProperty("VersionComparisonMask", out var m) ? m.GetString() : null;
                    var patterns = component.GetProperty("DetectionValues").EnumerateArray().Select(v => PatternRegex(v.GetString() ?? "")).ToList();

                    foreach (var device in devices)
                    {
                        var hwid = device.HardwareIds.FirstOrDefault(h => patterns.Any(p => p.IsMatch(h)));
                        if (hwid is null)
                            continue;
                        if (device.DriverVersion is not null && Version.TryParse(device.DriverVersion, out var installed)
                            && Compare(offered, installed, mask) <= 0)
                            continue;

                        updates.Add(new DriverUpdate($"intel:{id}", name, hwid, name, "Intel", date, component.GetProperty("Category").GetString() ?? "",
                            file.GetProperty("Size").GetInt64())
                        {
                            Source = DriverSource.Intel,
                            Version = version,
                            DownloadUrl = file.GetProperty("Url").GetString(),
                            Sha1 = file.GetProperty("Hash").GetString(),
                            InstallArgs = silent ? options : options.Replace("-s", "", StringComparison.Ordinal).Trim(),
                            Interactive = !silent,
                            ExpectedSigner = "Intel",
                            DetailsUrl = entry.TryGetProperty("Url", out var u) ? u.GetString() : null,
                        });
                        Log.Info($"Intel offer: {device.Name}  v{device.DriverVersion ?? "none"}  ->  v{version}  {name}  [{id}]");
                    }
                }
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                Log.Warn($"Intel entry skipped: {ex.Message}");
            }
        }
        return updates;
    }

    /// <summary>"VEN_8086&amp;DEV_02F0&amp;SUBSYS_*8086" matches PCI\VEN_8086&amp;DEV_02F0&amp;SUBSYS_00948086&amp;REV_11.</summary>
    private static Regex PatternRegex(string pattern)
    {
        var body = Regex.Escape(pattern).Replace(@"\*", @"[^&\\]*");
        return new Regex(@"^(?:[^\\]+\\)?" + body + @"(?:&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Intel's comparison mask marks which version parts count: "X.X.O.O" on 31.0.101.2115 compares only 101.2115
    /// (graphics drivers moved from 31.0 to 32.0 without the driver itself being newer).
    /// </summary>
    private static int Compare(Version a, Version b, string? mask)
    {
        int[] pa = [a.Major, a.Minor, Math.Max(a.Build, 0), Math.Max(a.Revision, 0)];
        int[] pb = [b.Major, b.Minor, Math.Max(b.Build, 0), Math.Max(b.Revision, 0)];
        var marks = (mask ?? "").Split('.');
        for (var i = 0; i < 4; i++)
        {
            if (marks.Length == 4 && marks[i].Equals("X", StringComparison.OrdinalIgnoreCase))
                continue;
            if (pa[i] != pb[i])
                return pa[i].CompareTo(pb[i]);
        }
        return 0;
    }

    // ── NVIDIA ────────────────────────────────────────────────────────────────────────────

    private static async Task<List<DriverUpdate>> SearchNvidiaAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct)
    {
        var updates = new List<DriverUpdate>();
        var gpus = devices.Where(d => d.ClassName.Equals("Display", StringComparison.OrdinalIgnoreCase)
                                      && d.DeviceId.StartsWith(@"PCI\VEN_10DE&DEV_", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var gpu in gpus)
        {
            var devId = gpu.DeviceId.Substring(@"PCI\VEN_10DE&DEV_".Length, 4);
            var laptop = System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery;
            var query = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["dIDa"] = new[] { $"{devId}_10DE" },
                ["osC"] = "10.0",
                ["osB"] = DriverCatalog.OsBuild.ToString(CultureInfo.InvariantCulture),
                ["is6"] = Environment.Is64BitOperatingSystem ? "1" : "0",
                ["lg"] = "1033",
                ["iLp"] = laptop ? "1" : "0",
                ["prvMd"] = "0",
                ["gcV"] = "3.27.0.112",
                ["gIsB"] = "0",
                ["dch"] = "1",
                ["upCRD"] = "0",
                ["isCRD"] = "0",
            });

            using var doc = JsonDocument.Parse(await DriverCatalog.Http.GetStringAsync(NvidiaLookup + Uri.EscapeDataString(query), ct));
            var root = doc.RootElement;
            if (!root.TryGetProperty("DriverAttributes", out var drv) || drv.ValueKind != JsonValueKind.Object)
                continue;
            if (Str(drv, "IsWHQL") != "1" || Str(drv, "IsBeta") == "1")
                continue;

            var display = Str(drv, "Version");               // "617.42"
            var windows = ToWindowsVersion(display, gpu.DriverVersion); // "32.0.16.1742"
            if (windows is null || (Version.TryParse(gpu.DriverVersion, out var installed) && windows <= installed))
                continue;

            var url = Str(drv, "DownloadURL");
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase))
                continue;

            var size = await ContentLengthAsync(url, ct);
            var title = $"{Str(drv, "Name") ?? "NVIDIA driver"} {display}";
            updates.Add(new DriverUpdate($"nvidia:{Str(drv, "ID")}", title, gpu.HardwareIds[0], gpu.Name, "NVIDIA",
                DateTime.TryParse(Str(drv, "ReleaseDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date) ? date.Date : null,
                "Display", size)
            {
                Source = DriverSource.Nvidia,
                Version = windows.ToString(),
                DownloadUrl = url,
                InstallArgs = "-s -noreboot -noeula",
                ExpectedSigner = "NVIDIA",
                DetailsUrl = Str(drv, "DetailsURL"),
            });
            Log.Info($"NVIDIA offer: {gpu.Name}  v{gpu.DriverVersion}  ->  {display} (v{windows})");
        }
        return updates;
    }

    /// <summary>NVIDIA "617.42" is Windows driver 32.0.16.1742: the last five digits move into the last two fields.</summary>
    internal static Version? ToWindowsVersion(string? display, string? installed)
    {
        if (display is null || !decimal.TryParse(display, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return null;
        var digits = ((int)Math.Round(value * 100)).ToString("D5", CultureInfo.InvariantCulture); // 61742
        var major = Version.TryParse(installed, out var current) ? current.Major : 32;
        var minor = current?.Minor ?? 0;
        return new Version(major, minor, 10 + (digits[0] - '0'), int.Parse(digits[1..], CultureInfo.InvariantCulture));
    }

    // ── AMD ───────────────────────────────────────────────────────────────────────────────

    private const string AmdReferer = "https://www.amd.com/";
    private const string AmdGraphicsPage = "https://www.amd.com/en/support/downloads/drivers.html/graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7900-xtx.html";
    private const string AmdChipsetPage = "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am5/x670e.html";

    /// <summary>
    /// GPUs on the current Adrenalin branch: RDNA discrete (Radeon RX 5000 and newer, device IDs 0x7310+) and RDNA
    /// integrated graphics. Vega / Polaris moved to a separate maintenance driver and are left out.
    /// </summary>
    private static readonly HashSet<int> AmdRdnaIntegrated = [0x164E, 0x1681, 0x15BF, 0x15C8, 0x150E, 0x1586, 0x13C0, 0x1114];

    private static async Task<List<DriverUpdate>> SearchAmdAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct)
    {
        var updates = new List<DriverUpdate>();

        var gpus = devices.Where(d => d.ClassName.Equals("Display", StringComparison.OrdinalIgnoreCase)
                                      && d.DeviceId.StartsWith(@"PCI\VEN_1002&DEV_", StringComparison.OrdinalIgnoreCase)
                                      && int.TryParse(d.DeviceId.AsSpan(@"PCI\VEN_1002&DEV_".Length, 4), NumberStyles.HexNumber, null, out var dev)
                                      && (dev >= 0x7310 || AmdRdnaIntegrated.Contains(dev))).ToList();
        if (gpus.Count > 0)
        {
            var html = await DriverCatalog.Http.GetStringAsync(AmdGraphicsPage, ct);
            var text = WebTextRegex().Replace(html, " ");
            var recommended = Regex.Match(text, @"Adrenalin\s+(\d+\.\d+\.\d+)\s*\(WHQL Recommended\)");
            var version = recommended.Success ? recommended.Groups[1].Value : null;
            var url = version is null ? null
                : Regex.Matches(html, @"https://drivers\.amd\.com/drivers/whql-amd-software-adrenalin-edition-" + Regex.Escape(version) + @"[^""'\s<>]*\.exe")
                    .Select(m => m.Value).FirstOrDefault();
            var installed = InstalledPackage("AMD Software");
            if (version is not null && url is not null && IsPackageNewer(version, installed))
            {
                var size = await ContentLengthAsync(url, ct, AmdReferer);
                foreach (var gpu in gpus)
                {
                    updates.Add(new DriverUpdate($"amd:adrenalin-{version}", $"AMD Software: Adrenalin Edition {version}", gpu.HardwareIds[0], gpu.Name, "AMD", null, "Display", size)
                    {
                        Source = DriverSource.Amd,
                        Version = version,
                        InstalledPackage = installed is null ? null : $"AMD Software {installed}",
                        DownloadUrl = url,
                        Referer = AmdReferer,
                        InstallArgs = "",
                        Interactive = true,
                        ExpectedSigner = "Advanced Micro Devices",
                        DetailsUrl = AmdGraphicsPage,
                    });
                    Log.Info($"AMD offer: {gpu.Name}  AMD Software {installed ?? "not installed"}  ->  Adrenalin {version}");
                }
            }
        }

        // AMD platform: an AMD (VEN_1022) system device such as the PSP, SMBus or GPIO controller carries the offer.
        var platform = devices.FirstOrDefault(d => d.DeviceId.StartsWith(@"PCI\VEN_1022&", StringComparison.OrdinalIgnoreCase)
                                                   && d.ClassName.Equals("System", StringComparison.OrdinalIgnoreCase));
        if (platform is not null)
        {
            var html = await DriverCatalog.Http.GetStringAsync(AmdChipsetPage, ct);
            var link = Regex.Match(html, @"https://drivers\.amd\.com/drivers/AMD_Chipset_Software_(\d+(?:\.\d+){2,3})\.exe");
            var installed = InstalledPackage("AMD Chipset Software");
            if (link.Success && IsPackageNewer(link.Groups[1].Value, installed))
            {
                var version = link.Groups[1].Value;
                updates.Add(new DriverUpdate($"amd:chipset-{version}", $"AMD Chipset Software {version}", platform.HardwareIds[0], platform.Name, "AMD", null, "System",
                    await ContentLengthAsync(link.Value, ct, AmdReferer))
                {
                    Source = DriverSource.Amd,
                    Version = version,
                    InstalledPackage = installed is null ? null : $"AMD Chipset Software {installed}",
                    DownloadUrl = link.Value,
                    Referer = AmdReferer,
                    InstallArgs = "",
                    Interactive = true,
                    ExpectedSigner = "Advanced Micro Devices",
                    DetailsUrl = AmdChipsetPage,
                });
                Log.Info($"AMD offer: chipset {installed ?? "not installed"}  ->  {version}");
            }
        }

        return updates;
    }

    private static bool IsPackageNewer(string offered, string? installed) =>
        installed is null || !Version.TryParse(installed, out var have) || !Version.TryParse(offered, out var get) || get > have;

    /// <summary>DisplayVersion of an installed program (Apps &amp; features), matched by exact display name.</summary>
    private static string? InstalledPackage(string displayName)
    {
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var root = Registry.LocalMachine.OpenSubKey(path);
            if (root is null)
                continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("DisplayName") is string shown && shown.Equals(displayName, StringComparison.OrdinalIgnoreCase)
                    && key.GetValue("DisplayVersion") is string version)
                    return Regex.Match(version, @"^\d+(?:\.\d+){1,3}").Value is { Length: > 0 } v ? v : null;
            }
        }
        return null;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex WebTextRegex();

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null;

    private static async Task<long> ContentLengthAsync(string url, CancellationToken ct, string? referer = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            if (referer is not null)
                request.Headers.Referrer = new Uri(referer);
            using var response = await DriverCatalog.Http.SendAsync(request, ct);
            return response.Content.Headers.ContentLength ?? 0;
        }
        catch (HttpRequestException)
        {
            return 0;
        }
    }

    // ── Download + verify ─────────────────────────────────────────────────────────────────

    /// <summary>Downloads the installer, checks Intel's SHA-1 when given, and requires the maker's signature.</summary>
    public static async Task<VendorInstall> PrepareAsync(DriverUpdate u, string workFolder, Action<long, long>? progress, CancellationToken ct)
    {
        var folder = Path.Combine(workFolder, u.UpdateId.Replace(':', '-'));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(new Uri(u.DownloadUrl!).LocalPath));
        await DriverCatalog.DownloadAsync(u.DownloadUrl!, target, (got, total) => progress?.Invoke(got, total), ct, u.Referer);

        foreach (var (expected, algorithm) in new[] { (u.Sha256, "SHA256"), (u.Sha1, "SHA1"), (u.Md5, "MD5") })
        {
            if (string.IsNullOrEmpty(expected))
                continue;
            await using var stream = File.OpenRead(target);
            var hash = Convert.ToHexString(algorithm switch
            {
                "SHA256" => await SHA256.HashDataAsync(stream, ct),
                "SHA1" => await SHA1.HashDataAsync(stream, ct),
                _ => await MD5.HashDataAsync(stream, ct),
            });
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The download is corrupted (its {algorithm} does not match the manufacturer's).");
            break;
        }
        var signature = await Task.Run(() => Startup.SignatureChecker.Check(target), ct);
        if (signature.State != Startup.SignatureState.Verified
            || !signature.Publisher.Contains(u.ExpectedSigner ?? "\0", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The installer is not signed by {u.ExpectedSigner} ({signature.State}, {signature.Publisher}).");

        if (u.ExtractCommand is { } extract && u.InstallCommand is { } install)
        {
            // Lenovo: unpack the signed installer, then run the package's own silent command line.
            var unpacked = Path.Combine(folder, "pkg");
            Directory.CreateDirectory(unpacked);
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{extract.Replace("%PACKAGEPATH%", unpacked)}\"")
                   { WorkingDirectory = folder, CreateNoWindow = true, UseShellExecute = false })!)
                await p.WaitForExitAsync(ct);
            return new VendorInstall(u.UpdateId, u.Title, Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                $"/c \"{install.Replace("%PACKAGEPATH%", unpacked)}\"", false);
        }

        return new VendorInstall(u.UpdateId, u.Title, target, u.InstallArgs ?? "", u.Interactive);
    }

    private static string Clean(string? text) => (text ?? "").Replace("®", "").Replace("™", "").Replace("*", "").Trim();
}
