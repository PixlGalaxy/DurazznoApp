using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Xml;
using System.Xml.Linq;
using Durazzno.Core;
using Microsoft.Win32;

namespace Durazzno.Hardware.Drivers;

/// <summary>
/// Drivers from the PC makers' own public catalogs, the ones Dell Command Update, HP Image Assistant and Lenovo
/// System Update read. They carry the drivers each maker tunes for each model (audio, touchpad, hotkeys...), which
/// Microsoft, Intel and NVIDIA do not publish. Only the catalog of this PC's maker is read, and only drivers (no BIOS or
/// firmware, which need the maker's own tools and a stable power supply).
///  • Dell: downloads.dell.com/catalog/CatalogPC.cab, components for this SKU (Win32_ComputerSystem.SystemSKUNumber)
///    matched by PCI vendor/device IDs; Dell Update Packages install silently with /s.
///  • HP: hpia.hpcloud.hp.com/ref/&lt;board&gt;/&lt;board&gt;_64_&lt;10.0|11.0&gt;.&lt;release&gt;.cab, devices by hardware ID → SoftPaqs
///    (/s installs silently).
///  • Lenovo: download.lenovo.com/catalog/&lt;machine type&gt;_Win10|Win11.xml, one XML per package with the hardware IDs
///    and versions it installs; packages extract and install with their own silent command lines.
/// </summary>
internal static class OemCatalogs
{
    private sealed record Machine(string Maker, string Sku, string Board, string Model);

    public static async Task<List<DriverUpdate>> SearchAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken ct)
    {
        var machine = Detect();
        if (machine is null)
            return [];
        var watch = Stopwatch.StartNew();
        var updates = machine.Maker switch
        {
            "Dell" => await DellAsync(machine, devices, ct),
            "HP" => await HpAsync(machine, devices, ct),
            "Lenovo" => await LenovoAsync(machine, devices, ct),
            _ => [],
        };
        Log.Info($"{machine.Maker} catalog ({machine.Sku}/{machine.Board}/{machine.Model}): {updates.Count} newer drivers in {watch.Elapsed.TotalSeconds:0.0}s");
        return updates;
    }

    private static Machine? Detect()
    {
        string Get(string cls, string prop)
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
            foreach (var o in searcher.Get())
                return (o[prop] as string)?.Trim() ?? "";
            return "";
        }

        // DURAZZNO_OEM=hp:880d (or dell:0A6C, lenovo:20XW) tests a catalog on a PC from another maker.
        if (Environment.GetEnvironmentVariable("DURAZZNO_OEM") is { Length: > 0 } test && test.Split(':') is [var maker, var id])
            return maker.ToLowerInvariant() switch
            {
                "dell" => new Machine("Dell", id, "", ""),
                "hp" => new Machine("HP", "", id, ""),
                "lenovo" => new Machine("Lenovo", "", "", id),
                _ => null,
            };

        var manufacturer = Get("Win32_ComputerSystem", "Manufacturer").ToLowerInvariant();
        if (manufacturer.Contains("dell") || manufacturer.Contains("alienware"))
            return new Machine("Dell", Get("Win32_ComputerSystem", "SystemSKUNumber"), "", "");
        if (manufacturer.Contains("hp") || manufacturer.Contains("hewlett"))
            return new Machine("HP", "", Get("Win32_BaseBoard", "Product"), "");
        if (manufacturer.Contains("lenovo"))
            return new Machine("Lenovo", "", "", Get("Win32_ComputerSystem", "Model"));
        return null;
    }

    private static bool IsWindows11 => DriverCatalog.OsBuild >= 22000;

    private static string ReleaseId()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return (key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "").ToLowerInvariant();
    }

    private static bool Newer(string? offered, DeviceInfo d) =>
        Version.TryParse(offered?.TrimEnd('^'), out var o) && (!Version.TryParse(d.DriverVersion, out var i) || o > i);

    private static DeviceInfo? Find(IReadOnlyList<DeviceInfo> devices, Func<string, bool> matches) =>
        devices.FirstOrDefault(d => d.HardwareIds.Any(matches));

    private static async Task<byte[]> CachedAsync(string url, string name, TimeSpan maxAge, CancellationToken ct)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(AppInfo.DataDir, "catalogs")).FullName, name);
        if (File.Exists(path) && DateTime.Now - File.GetLastWriteTime(path) < maxAge)
            return await File.ReadAllBytesAsync(path, ct);
        var bytes = await DriverCatalog.Http.GetByteArrayAsync(url, ct);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return bytes;
    }

    /// <summary>A .cab with one XML inside, unpacked with tar into a temporary folder.</summary>
    private static async Task<string> CabXmlAsync(byte[] cab, CancellationToken ct)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DurazznoApp", "cab-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var file = Path.Combine(dir, "catalog.cab");
        await File.WriteAllBytesAsync(file, cab, ct);
        using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"), $"-xf \"{file}\" -C \"{dir}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            await p.WaitForExitAsync(ct);
        return Directory.EnumerateFiles(dir, "*.xml").FirstOrDefault() ?? throw new InvalidDataException("The catalog has no XML.");
    }

    // ── Dell ──────────────────────────────────────────────────────────────────────────────

    private static async Task<List<DriverUpdate>> DellAsync(Machine m, IReadOnlyList<DeviceInfo> devices, CancellationToken ct)
    {
        var updates = new List<DriverUpdate>();
        if (m.Sku.Length == 0)
            return updates;
        var xml = await CabXmlAsync(await CachedAsync("https://downloads.dell.com/catalog/CatalogPC.cab", "dell.cab", TimeSpan.FromDays(1), ct), ct);
        try
        {
            using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (reader.ReadToFollowing("SoftwareComponent"))
            {
                if (XNode.ReadFrom(reader) is not XElement c || c.Element("ComponentType")?.Attribute("value")?.Value != "DRVR")
                    continue;
                if (!c.Descendants("Model").Any(x => string.Equals(x.Attribute("systemID")?.Value, m.Sku, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var version = c.Attribute("vendorVersion")?.Value;
                foreach (var pci in c.Descendants("PCIInfo"))
                {
                    var ven = pci.Attribute("vendorID")?.Value;
                    var dev = pci.Attribute("deviceID")?.Value;
                    if (string.IsNullOrEmpty(ven) || string.IsNullOrEmpty(dev))
                        continue;
                    var subsys = pci.Attribute("subDeviceID")?.Value + pci.Attribute("subVendorID")?.Value;
                    var device = Find(devices, h => h.Contains($"VEN_{ven}&DEV_{dev}", StringComparison.OrdinalIgnoreCase)
                                                    && (subsys.Length == 0 || h.Contains($"SUBSYS_{subsys}", StringComparison.OrdinalIgnoreCase)));
                    if (device is null || !Newer(version, device) || updates.Any(u => u.HardwareId == device.HardwareIds[0]))
                        continue;
                    var name = c.Element("Name")?.Element("Display")?.Value ?? "Dell driver";
                    updates.Add(new DriverUpdate($"dell:{c.Attribute("releaseID")?.Value}", name, device.HardwareIds[0], device.Name, "Dell",
                        DateTime.TryParse(c.Attribute("releaseDate")?.Value, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var date) ? date : null,
                        c.Element("Category")?.Element("Display")?.Value ?? "", long.TryParse(c.Attribute("size")?.Value, out var size) ? size : 0)
                    {
                        Source = DriverSource.Dell,
                        Version = version,
                        DownloadUrl = $"https://downloads.dell.com/{c.Attribute("path")?.Value}",
                        Md5 = c.Attribute("hashMD5")?.Value,
                        InstallArgs = "/s",
                        ExpectedSigner = "Dell",
                    });
                    break;
                }
            }
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(xml)!, true); } catch { }
        }
        return updates;
    }

    // ── HP ────────────────────────────────────────────────────────────────────────────────

    private static async Task<List<DriverUpdate>> HpAsync(Machine m, IReadOnlyList<DeviceInfo> devices, CancellationToken ct)
    {
        var updates = new List<DriverUpdate>();
        var board = m.Board.ToLowerInvariant();
        if (board.Length == 0)
            return updates;
        var os = $"{(IsWindows11 ? "11.0" : "10.0")}.{ReleaseId()}";
        var xml = await CabXmlAsync(await CachedAsync($"https://hpia.hpcloud.hp.com/ref/{board}/{board}_64_{os}.cab", $"hp-{board}-{os}.cab", TimeSpan.FromDays(1), ct), ct);
        try
        {
            var doc = XDocument.Load(xml);
            var softpaqs = doc.Descendants("UpdateInfo").Where(u => u.Element("Id") is not null)
                .GroupBy(u => u.Element("Id")!.Value).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var refDevice in doc.Descendants("Device"))
            {
                var hwid = refDevice.Element("HardwareId")?.Value;
                if (string.IsNullOrEmpty(hwid))
                    continue;
                var device = Find(devices, h => h.Equals(hwid, StringComparison.OrdinalIgnoreCase));
                if (device is null)
                    continue;
                foreach (var id in refDevice.Descendants("UpdateInfo").Select(x => x.Attribute("IdRef")?.Value).OfType<string>())
                {
                    if (!softpaqs.TryGetValue(id, out var sp) || !(sp.Element("Category")?.Value ?? "").StartsWith("Driver", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var version = sp.Element("Version")?.Value;
                    if (!Newer(version, device) || updates.Any(u => u.UpdateId == $"hp:{id}"))
                        continue;
                    updates.Add(new DriverUpdate($"hp:{id}", sp.Element("Name")?.Value ?? id, device.HardwareIds[0], device.Name, "HP",
                        DateTime.TryParse(sp.Element("DateReleased")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                        sp.Element("Category")?.Value ?? "", long.TryParse(sp.Element("Size")?.Value, out var size) ? size : 0)
                    {
                        Source = DriverSource.HP,
                        Version = version,
                        DownloadUrl = "https://" + sp.Element("Url")?.Value,
                        Sha256 = sp.Element("SHA256")?.Value,
                        InstallArgs = "/s",
                        ExpectedSigner = "HP",
                        DetailsUrl = sp.Element("ReleaseNotesUrl") is { } notes ? "https://" + notes.Value : null,
                    });
                }
            }
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(xml)!, true); } catch { }
        }
        return updates;
    }

    // ── Lenovo ────────────────────────────────────────────────────────────────────────────

    private static async Task<List<DriverUpdate>> LenovoAsync(Machine m, IReadOnlyList<DeviceInfo> devices, CancellationToken ct)
    {
        var updates = new List<DriverUpdate>();
        var type = m.Model.Length >= 4 ? m.Model[..4].ToUpperInvariant() : "";
        if (type.Length == 0)
            return updates;
        var catalog = XDocument.Load(new MemoryStream(await CachedAsync(
            $"https://download.lenovo.com/catalog/{type}_{(IsWindows11 ? "Win11" : "Win10")}.xml", $"lenovo-{type}.xml", TimeSpan.FromDays(1), ct)));
        var locations = catalog.Descendants("package")
            .Where(p => !(p.Element("category")?.Value ?? "").Contains("BIOS", StringComparison.OrdinalIgnoreCase)
                        && !(p.Element("category")?.Value ?? "").Contains("Firmware", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Element("location")?.Value).OfType<string>().ToList();

        var gate = new SemaphoreSlim(6);
        var packages = await Task.WhenAll(locations.Select(async url =>
        {
            await gate.WaitAsync(ct);
            try { return (Url: url, Xml: XDocument.Load(new MemoryStream(await DriverCatalog.Http.GetByteArrayAsync(url, ct)))); }
            catch (Exception ex) when (ex is HttpRequestException or XmlException) { return (Url: url, Xml: (XDocument?)null); }
            finally { gate.Release(); }
        }));

        Log.Info($"Lenovo catalog: {packages.Count(x => x.Xml is not null)} of {locations.Count} driver packages read");
        foreach (var (url, xml) in packages)
        {
            if (xml?.Root is not { } pkg)
                continue;
            foreach (var driver in pkg.Element("DetectInstall")?.Descendants("_Driver") ?? [])
            {
                var ids = driver.Elements("HardwareID").Select(x => x.Value.Trim()).ToList();
                var device = Find(devices, h => ids.Contains(h, StringComparer.OrdinalIgnoreCase));
                var version = driver.Element("Version")?.Value.TrimEnd('^');
                if (device is null || !Newer(version, device))
                    continue;
                var file = pkg.Element("Files")?.Element("Installer")?.Element("File");
                if (file?.Element("Name")?.Value is not { } exe)
                    break;
                updates.Add(new DriverUpdate($"lenovo:{pkg.Attribute("id")?.Value}", pkg.Element("Title")?.Element("Desc")?.Value ?? exe,
                    device.HardwareIds[0], device.Name, "Lenovo",
                    DateTime.TryParse(pkg.Element("ReleaseDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                    "", long.TryParse(file.Element("Size")?.Value, out var size) ? size : 0)
                {
                    Source = DriverSource.Lenovo,
                    Version = version,
                    DownloadUrl = url[..(url.LastIndexOf('/') + 1)] + exe,
                    Sha256 = file.Element("CRC")?.Value,
                    ExtractCommand = pkg.Element("ExtractCommand")?.Value,
                    InstallCommand = pkg.Element("Install")?.Element("Cmdline")?.Value,
                    ExpectedSigner = "Lenovo",
                });
                break;
            }
        }
        return updates;
    }
}
