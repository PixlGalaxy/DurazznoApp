using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Durazzno.Core;

namespace Durazzno.Hardware.Drivers;

/// <summary>A catalog driver downloaded, verified and unpacked, ready for the elevated installer.</summary>
internal sealed record CatalogInstall(string UpdateId, string Title, string HardwareId, string InfPath);

/// <summary>
/// Driver search through the Microsoft Update Catalog, the way Driver Easy uses its own server: every device's
/// hardware IDs are looked up, offers that do not apply to this Windows build or architecture are dropped, and the
/// package's "Supported Hardware IDs" must contain one of the device's IDs (no less specific than the ID the current
/// driver matched) before it is offered. Unlike Windows Update, the catalog lists every published version, so it
/// finds newer drivers Windows Update holds back. Packages are signed .cab files from Microsoft's CDN.
/// </summary>
internal static partial class DriverCatalog
{
    private const string Base = "https://www.catalog.update.microsoft.com/";
    private const int MaxParallel = 4;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);
    private const int MaxDetailsPerDevice = 6;

    internal static readonly HttpClient Http = CreateClient();
    internal static readonly int OsBuild = Environment.OSVersion.Version.Build;
    private static readonly string Arch = RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "ARM64",
        Architecture.X86 => "X86",
        _ => "AMD64",
    };

    private sealed record Row(string UpdateId, string Title, string Products, string Classification, DateTime? Date, Version? Version, long Size);
    private sealed record Details(string Version, DateTime? Date, string Provider, string Model, string DriverClass, string Architecture, string[] HardwareIds);

    private static readonly ConcurrentDictionary<string, Task<List<Row>>> Searches = new(StringComparer.OrdinalIgnoreCase);

    // Answers are kept for a few hours (the app lives in the tray), so checking again does not hit the catalog for
    // every device; it limits clients that search too often. Empty answers are never kept.
    private static readonly ConcurrentDictionary<string, (DateTime At, List<Row> Rows)> SearchCache = new(StringComparer.OrdinalIgnoreCase);
    private static int _asked, _answered;
    private static readonly ConcurrentDictionary<string, Task<Details?>> DetailCache = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(40),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64) DurazznoApp/{AppInfo.Version}");
        return client;
    }

    /// <summary>
    /// Devices worth looking up: real hardware plus driver-installed software components (SWC\..., enumerated under
    /// SWD\DRIVERENUM: Intel DTT, THX, Thunderbolt HSA...), not volumes, endpoints or other software plumbing.
    /// </summary>
    public static bool ShouldSearch(DeviceInfo d) =>
        d.HardwareIds.Length > 0
        && !d.DeviceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)
        && (!d.DeviceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) || d.DeviceId.StartsWith(@"SWD\DRIVERENUM\", StringComparison.OrdinalIgnoreCase))
        && !d.DeviceId.StartsWith(@"STORAGE\", StringComparison.OrdinalIgnoreCase)
        && !d.DeviceId.StartsWith(@"HTREE\", StringComparison.OrdinalIgnoreCase)
        && !SkippedClasses.Contains(d.ClassName);

    private static readonly HashSet<string> SkippedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Volume", "VolumeSnapshot", "AudioEndpoint", "PrintQueue", "LegacyDriver", "Computer", "Processor",
    };

    /// <summary>Looks every device up in the catalog. Returns at most one (the newest valid) offer per device.</summary>
    public static async Task<List<DriverUpdate>> SearchAsync(IReadOnlyList<DeviceInfo> devices, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        Searches.Clear();
        _asked = _answered = 0;
        var targets = devices.Where(ShouldSearch).ToList();
        var results = new ConcurrentBag<DriverUpdate>();
        var gate = new SemaphoreSlim(MaxParallel);
        var done = 0;
        progress?.Report((0, targets.Count));

        await Task.WhenAll(targets.Select(async device =>
        {
            try
            {
                if (await FindForDeviceAsync(device, gate, ct) is { } update)
                    results.Add(update);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"Catalog lookup for {device.Name}: {ex.Message}");
            }
            finally
            {
                progress?.Report((Interlocked.Increment(ref done), targets.Count));
            }
        }));

        // Every search empty means the catalog is refusing us (rate limit or outage), not that nothing exists.
        if (_asked >= 10 && _answered == 0)
            throw new InvalidOperationException("the Microsoft Update Catalog is not answering right now (it limits frequent searches). Try again in a few minutes");
        return results.ToList();
    }

    private static async Task<DriverUpdate?> FindForDeviceAsync(DeviceInfo device, SemaphoreSlim gate, CancellationToken ct)
    {
        var rows = new List<Row>();
        foreach (var query in Queries(device))
            rows.AddRange(await Searches.GetOrAdd(query, q => SearchRowsAsync(q, gate, ct)));

        Version.TryParse(device.DriverVersion, out var installed);
        // Offers published only for a newer Windows are kept (Driver Easy offers them too): many INFs install fine on
        // older builds, and the INF's own OS decorations are checked before installing.
        var candidates = rows
            .Where(r => r.Classification.StartsWith("Drivers", StringComparison.OrdinalIgnoreCase))
            .Select(r => (Row: r, Fit: Applicability(r.Products)))
            .Where(x => x.Fit != Fit.No)
            .Where(x => installed is null || x.Row.Version is null || x.Row.Version >= installed)
            .GroupBy(x => x.Row.UpdateId).Select(g => g.First())
            .OrderByDescending(x => x.Row.Version ?? new Version(0, 0))
            .ThenBy(x => x.Fit)
            .ThenByDescending(x => x.Row.Date ?? DateTime.MinValue)
            .Take(MaxDetailsPerDevice)
            .ToList();
        var allIds = device.HardwareIds.Concat(device.CompatibleIds).Select(Normalize).ToList();
        var currentRank = device.MatchingDeviceId is { } m ? allIds.FindIndex(id => id.Equals(Normalize(m), StringComparison.OrdinalIgnoreCase)) : -1;

        foreach (var (row, fit) in candidates)
        {
            var details = await DetailCache.GetOrAdd(row.UpdateId, id => GetDetailsAsync(id, gate, ct));
            if (details is not { HardwareIds.Length: > 0 })
                DetailCache.TryRemove(row.UpdateId, out _); // a failed answer is asked again next time
            if (details is null)
                continue;
            if (details.Architecture.Length > 0 && !details.Architecture.Contains(Arch, StringComparison.OrdinalIgnoreCase)
                && !details.Architecture.Equals("n/a", StringComparison.OrdinalIgnoreCase))
                continue;

            // The package must target this device, and at least as specifically as the current driver does
            // (otherwise a generic driver would replace the PC maker's tuned one).
            var supported = details.HardwareIds.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rank = allIds.FindIndex(supported.Contains);
            // Prefer the device's ID in the exact spelling the package uses, so it is found verbatim in the INF.
            var rawIds = device.HardwareIds.Concat(device.CompatibleIds).ToList();
            var matched = rank < 0 ? ""
                : rawIds.FirstOrDefault(id => details.HardwareIds.Contains(id, StringComparer.OrdinalIgnoreCase)
                                              && Normalize(id).Equals(allIds[rank], StringComparison.OrdinalIgnoreCase))
                  ?? rawIds[rank];
            if (rank < 0 || (currentRank >= 0 && rank > currentRank))
                continue;

            // Standard IDs every PC has (ACPI\PNP0A08, PCI\CC_0604, USB\Class_03...) say nothing about the vendor:
            // a package that only matches one of them could be another chipset maker's (an AMD driver on an Intel PC).
            if (IsGenericId(matched))
                continue;

            // Extension INFs are added on top of a device's driver, they do not replace it (their version numbers mean
            // something else entirely), and a vendor driver is only replaced by the same vendor's (never an OEM
            // extension from another PC maker for the same chip).
            if (details.DriverClass.Equals("Extension", StringComparison.OrdinalIgnoreCase) && !device.ClassName.Equals("Extension", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!SameProvider(device.DriverProvider, details.Provider))
                continue;

            // Never an older package, even when its version number is higher.
            var date = details.Date ?? row.Date;
            if (date is { } offered && device.DriverDate is { } current && offered < current)
                continue;

            var update = new DriverUpdate(row.UpdateId, row.Title, matched, details.Model, details.Provider, date, details.DriverClass, row.Size)
            {
                Source = DriverSource.Catalog,
                Version = details.Version,
                ForNewerWindows = fit == Fit.NewerWindows,
            };
            if (DriverUpdates.IsNewer(update, device))
            {
                Log.Info($"Catalog offer: {device.Name}  v{device.DriverVersion ?? "none"} ({device.DriverProvider}, {device.DriverDate:yyyy-MM-dd}, matched {device.MatchingDeviceId})"
                         + $"  ->  v{details.Version} ({details.Provider}, {date:yyyy-MM-dd}, matches {matched})  {row.Title}  [{row.UpdateId}]");
                return update;
            }
        }

        return null;
    }

    /// <summary>The device's most specific hardware ID (without the revision), then the generic vendor + device ID.</summary>
    private static IEnumerable<string> Queries(DeviceInfo d)
    {
        // The catalog indexes ACPI IDs as ACPI\INTC109C, not ACPI\VEN_INTC&DEV_109C.
        var specific = Normalize(RevisionRegex().Replace(d.HardwareIds[0], ""));
        yield return specific;

        // Drop the board-specific parts: PCI\VEN_8086&DEV_A788&SUBSYS_30061A58&REV_04 -> PCI\VEN_8086&DEV_A788.
        var generic = GenericRegex().Replace(d.HardwareIds[0], "");
        if (generic.Contains('&') && !generic.Equals(specific, StringComparison.OrdinalIgnoreCase))
            yield return generic;
    }

    /// <summary>
    /// "Intel(R) Corporation" ~ "INTEL" ~ "Intel": the first word decides. Windows' in-box (Microsoft) drivers and
    /// devices without a driver accept any vendor, since the hardware ID already names it.
    /// </summary>
    private static bool SameProvider(string? current, string offered)
    {
        static string Key(string? s) => new((s ?? "").TakeWhile(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var c = Key(current);
        return c.Length == 0 || c == "microsoft" || c == Key(offered);
    }

    /// <summary>True for IDs that do not name a vendor: class codes, PNP standard devices, compatible-class IDs.</summary>
    internal static bool IsGenericId(string id)
    {
        var n = Normalize(id);
        if (n.StartsWith('*') || n.StartsWith(@"ACPI\PNP", StringComparison.OrdinalIgnoreCase) || n.StartsWith(@"ACPI\ACPI", StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase) || n.StartsWith(@"SWC\", StringComparison.OrdinalIgnoreCase))
            return false; // software component IDs are defined by the vendor that ships them // ACPI\INTC1046, ACPI\MSFT0001...: the first four letters are the vendor
        return !(n.Contains("VEN_", StringComparison.OrdinalIgnoreCase) || n.Contains("VID_", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("VID&", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<Row>> SearchRowsAsync(string query, SemaphoreSlim gate, CancellationToken ct)
    {
        Interlocked.Increment(ref _asked);
        if (SearchCache.TryGetValue(query, out var cached) && DateTime.UtcNow - cached.At < CacheLifetime)
        {
            Interlocked.Increment(ref _answered);
            return cached.Rows;
        }

        // The catalog sometimes answers with an empty result page under load (indistinguishable from "no results"),
        // so an empty answer is asked again before it is believed.
        for (var attempt = 1; ; attempt++)
        {
            var rows = await SearchOnceAsync(query, gate, ct);
            if (rows.Count > 0)
            {
                Interlocked.Increment(ref _answered);
                SearchCache[query] = (DateTime.UtcNow, rows);
                return rows;
            }
            if (attempt == 3)
                return rows;
            await Task.Delay(700 * attempt, ct);
        }
    }

    private static async Task<List<Row>> SearchOnceAsync(string query, SemaphoreSlim gate, CancellationToken ct)
    {
        var html = await GetAsync($"{Base}Search.aspx?q={Uri.EscapeDataString(query)}", gate, ct);
        var rows = new List<Row>();
        foreach (Match m in RowRegex().Matches(html))
        {
            var cells = CellRegex().Matches(m.Value).Select(c => Clean(c.Groups[1].Value)).ToArray();
            if (cells.Length < 7)
                continue;
            var size = SizeRegex().Match(m.Value) is { Success: true } s ? long.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            rows.Add(new Row(m.Groups[1].Value, cells[1], cells[2], cells[3], ParseDate(cells[4]), ParseVersion(cells[1]), size));
        }
        return rows;
    }

    private static async Task<Details?> GetDetailsAsync(string updateId, SemaphoreSlim gate, CancellationToken ct)
    {
        // Like searches, the details page sometimes comes back empty: ask again before giving up on the offer.
        for (var attempt = 1; ; attempt++)
        {
            var details = await GetDetailsOnceAsync(updateId, gate, ct);
            if (details is { HardwareIds.Length: > 0 } || attempt == 3)
                return details;
            await Task.Delay(700 * attempt, ct);
        }
    }

    private static async Task<Details?> GetDetailsOnceAsync(string updateId, SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            var html = await GetAsync($"{Base}ScopedViewInline.aspx?updateid={updateId}", gate, ct);
            string Span(string id) => Regex.Match(html, $"(?s)<span id=\"ScopedViewHandler_{id}\"[^>]*>(.*?)</span>") is { Success: true } s ? Clean(s.Groups[1].Value) : "";
            string Div(string id) => Regex.Match(html, $"(?s)<div id=\"{id}\"[^>]*>(.*?)</div>") is { Success: true } s ? Clean(TagRegex().Replace(s.Groups[1].Value, " ")) : "";

            var arch = Div("archDiv");
            arch = arch.Contains(':') ? arch[(arch.IndexOf(':') + 1)..].Trim() : arch;

            var start = html.IndexOf("id=\"driverhwIDs\"", StringComparison.Ordinal);
            var end = start < 0 ? -1 : html.IndexOf("labelSupportedUpdateIDs", start, StringComparison.Ordinal);
            var ids = start < 0 || end < 0 ? [] : HwidRegex().Matches(html[start..end]).Select(x => Clean(x.Groups[1].Value)).Where(x => x.Length > 0).ToArray();

            return new Details(Span("version"), ParseDate(Span("versionDate")), Span("driverProvider"), Span("driverModel"), Span("driverClass"), arch, ids);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"Catalog details {updateId}: {ex.Message}");
            return null;
        }
    }

    private static async Task<string> GetAsync(string url, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await Http.GetStringAsync(url, ct);
                }
                catch (HttpRequestException) when (attempt < 3)
                {
                    await Task.Delay(800 * attempt, ct);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>ACPI\VEN_INTC&amp;DEV_1085 and ACPI\INTC1085 are the same ID in two spellings.</summary>
    private static string Normalize(string id) => AcpiRegex().Replace(id, @"ACPI\$1$2");

    // ── Windows build / product applicability ─────────────────────────────────────────────

    internal enum Fit { ThisWindows, NewerWindows, No }

    /// <summary>
    /// Whether any product line the update targets covers this PC ("Windows 11 Client, version 24H2 and later",
    /// "Windows 10, Vibranium and later", ...), or only newer client releases. Server-only offers are skipped; unknown
    /// product names are allowed and left for the INF check to accept or reject.
    /// </summary>
    internal static Fit Applicability(string products)
    {
        var known = false;
        var newer = false;
        foreach (Match m in ProductRegex().Matches(products))
        {
            known = true;
            if (MinBuild(m.Value) is { } min)
            {
                if (min <= OsBuild)
                    return Fit.ThisWindows;
                newer = true;
            }
        }
        if (newer)
            return Fit.NewerWindows;
        return !known && !products.Contains("Server", StringComparison.OrdinalIgnoreCase) ? Fit.ThisWindows : Fit.No;
    }

    private static int? MinBuild(string product)
    {
        var p = product.ToLowerInvariant();
        if (p.Contains("server"))
            return null;
        if (p.Contains("windows 11") || p.Contains("windows - client"))
        {
            var v = Regex.Match(p, @"version (\d\dh\d)").Groups[1].Value;
            return v switch
            {
                "21h2" => 22000, "22h2" => 22621, "23h2" => 22631, "24h2" => 26100, "25h2" => 26200,
                "" => 22000,
                _ => int.MaxValue, // a release newer than any this code knows: certainly newer than this PC
            };
        }
        if (p.Contains("windows 10"))
        {
            if (p.Contains("vibranium")) return 19041;
            var v = Regex.Match(p, @"version (\d{4})").Groups[1].Value;
            if (v.Length > 0)
                return v switch { "1507" => 10240, "1511" => 10586, "1607" => 14393, "1703" => 15063, "1709" => 16299, "1803" => 17134, "1809" => 17763, "1903" => 18362, "1909" => 18363, _ => 19041 };
            if (p.Contains("anniversary")) return 14393;
            if (p.Contains("fall creators")) return 16299;
            if (p.Contains("creators")) return 15063;
            if (p.Contains("april 2018")) return 17134;
            if (p.Contains("october 2018")) return 17763;
            if (p.Contains("may 2019")) return 18362;
            return 10240;
        }
        if (p.Contains("and later"))
            return p.Contains("8.1") ? 9600 : p.Contains("windows 8") ? 9200 : p.Contains("windows 7") ? 7600 : null;
        return null;
    }

    // ── Download, verify, unpack ──────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads a catalog update, checks the SHA-1 digest the catalog publishes, unpacks the .cab and finds the INF
    /// that declares the device's hardware ID (Driver Easy's InfFinder step). Throws with a readable message on failure.
    /// </summary>
    public static async Task<CatalogInstall> PrepareAsync(DriverUpdate u, string workFolder, Action<long, long>? progress, CancellationToken ct)
    {
        var folder = Path.Combine(workFolder, u.UpdateId);
        Directory.CreateDirectory(folder);
        var files = await GetDownloadFilesAsync(u.UpdateId, ct);
        if (files.Count == 0)
            throw new InvalidOperationException("The catalog returned no download for this driver.");

        var unpacked = Path.Combine(folder, "files");
        foreach (var (url, digest) in files)
        {
            var target = Path.Combine(folder, Path.GetFileName(new Uri(url).LocalPath));
            await DownloadAsync(url, target, (got, total) => progress?.Invoke(got, total), ct);
            if (digest.Length > 0 && !await DigestMatchesAsync(target, digest, ct))
                throw new InvalidOperationException("The download is corrupted (its SHA-1 does not match the catalog).");

            Directory.CreateDirectory(unpacked);
            await ExtractAsync(target, unpacked, ct);
        }

        var inf = FindInf(unpacked, u.HardwareId)
            ?? throw new InvalidOperationException($"No INF in the package declares {u.HardwareId}.");
        VerifyWhql(inf);
        VerifyTargetsThisWindows(inf);
        return new CatalogInstall(u.UpdateId, u.Title, u.HardwareId, inf);
    }

    private static async Task<List<(string Url, string Digest)>> GetDownloadFilesAsync(string updateId, CancellationToken ct)
    {
        var payload = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{updateId}\",\"updateID\":\"{updateId}\"}}]";
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("updateIDs", payload)]);
        using var response = await Http.PostAsync($"{Base}DownloadDialog.aspx", content, ct);
        response.EnsureSuccessStatusCode();
        var js = await response.Content.ReadAsStringAsync(ct);

        var urls = Regex.Matches(js, @"files\[(\d+)\]\.url = '([^']+)'").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var digests = Regex.Matches(js, @"files\[(\d+)\]\.digest = '([^']*)'").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        return urls
            // Only Microsoft's download CDN.
            .Where(kv => Uri.TryCreate(kv.Value, UriKind.Absolute, out var uri)
                         && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                         && uri.Host.EndsWith(".windowsupdate.com", StringComparison.OrdinalIgnoreCase))
            .Select(kv => (kv.Value, digests.GetValueOrDefault(kv.Key, "")))
            .ToList();
    }

    /// <param name="referer">AMD's CDN only serves files linked from amd.com, as when clicking Download on its page.</param>
    internal static async Task DownloadAsync(string url, string target, Action<long, long> progress, CancellationToken ct, string? referer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer is not null)
            request.Headers.Referrer = new Uri(referer);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        var buffer = new byte[1 << 16];
        long got = 0;
        var tick = Stopwatch.StartNew();
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            got += read;
            if (tick.ElapsedMilliseconds > 250)
            {
                progress(got, total);
                tick.Restart();
            }
        }
        progress(got, total);
    }

    private static async Task<bool> DigestMatchesAsync(string file, string digest, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var hash = Convert.ToBase64String(await SHA1.HashDataAsync(stream, ct));
        if (hash == digest)
            return true;
        stream.Position = 0; // some entries publish SHA-256 instead
        return Convert.ToBase64String(await SHA256.HashDataAsync(stream, ct)) == digest;
    }

    /// <summary>tar.exe (libarchive) keeps the folder layout inside the .cab; expand.exe is the fallback.</summary>
    private static async Task ExtractAsync(string cab, string folder, CancellationToken ct)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var (exe, args) in new[]
        {
            (Path.Combine(system, "tar.exe"), $"-xf \"{cab}\" -C \"{folder}\""),
            (Path.Combine(system, "expand.exe"), $"\"{cab}\" -F:* \"{folder}\""),
        })
        {
            if (!File.Exists(exe))
                continue;
            using var process = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false })!;
            await process.WaitForExitAsync(ct);
            if (process.ExitCode == 0 && Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Any())
                return;
        }
        throw new InvalidOperationException("Could not unpack the driver package.");
    }

    /// <summary>
    /// The INF's CatalogFile must carry a valid Microsoft signature (WHQL: "Microsoft Windows Hardware Compatibility
    /// Publisher"), checked before anything is handed to the elevated installer.
    /// </summary>
    private static void VerifyWhql(string inf)
    {
        var catalog = Regex.Match(File.ReadAllText(inf), @"^\s*CatalogFile(?:\.NTamd64)?\s*=\s*(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var path = catalog.Success ? Path.Combine(Path.GetDirectoryName(inf)!, catalog.Groups[1].Value.Trim()) : null;
        var signature = Startup.SignatureChecker.Check(path);
        if (!signature.IsMicrosoft)
            throw new InvalidOperationException(signature.State == Startup.SignatureState.Missing
                ? "The package has no signed catalog file."
                : $"The package is not signed by Microsoft ({signature.State}, {signature.Publisher}).");
    }

    /// <summary>
    /// The INF's [Manufacturer] decorations decide where it installs (NTamd64, NTamd64.10.0...22000 = build 22000+).
    /// At least one must cover this architecture and build, otherwise Windows would refuse it after the UAC prompt.
    /// </summary>
    private static void VerifyTargetsThisWindows(string inf)
    {
        var text = File.ReadAllText(inf);
        var section = Regex.Match(text, @"(?ims)^\s*\[Manufacturer\]\s*$(.*?)(?=^\s*\[)");
        if (!section.Success)
            return;

        var arch = Arch == "AMD64" ? "amd64" : Arch.ToLowerInvariant();
        int? needed = null;
        foreach (var line in section.Groups[1].Value.Split('\n'))
        {
            var entry = line.Split(';')[0];
            var parts = entry.Split('=', 2);
            if (parts.Length < 2)
                continue;
            foreach (var decoration in parts[1].Split(',').Skip(1).Select(x => x.Trim()))
            {
                var fields = decoration.Split('.');
                var platform = fields[0];
                if (!platform.Equals("NT", StringComparison.OrdinalIgnoreCase) && !platform.Equals("NT" + arch, StringComparison.OrdinalIgnoreCase))
                    continue;
                var build = fields.Length > 5 && int.TryParse(fields[5], out var b) ? b : 0;
                if (build <= OsBuild)
                    return;
                needed = needed is null ? build : Math.Min(needed.Value, build);
            }
        }
        if (needed is not null)
            throw new InvalidOperationException($"This driver needs a newer Windows (build {needed} or later; this PC is {OsBuild}).");
    }

    private static string? FindInf(string folder, string hardwareId)
    {
        foreach (var inf in Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories))
        {
            try
            {
                // Match the ID as a whole token so "...DEV_A788" does not match "...DEV_A7881".
                var text = File.ReadAllText(inf);
                if (Regex.IsMatch(text, Regex.Escape(hardwareId) + @"(?![\w&])", RegexOptions.IgnoreCase))
                    return inf;
            }
            catch (IOException)
            {
            }
        }
        return null;
    }

    // ── Parsing helpers ───────────────────────────────────────────────────────────────────

    private static string Clean(string html) => WebUtility.HtmlDecode(TagRegex().Replace(html, " ")).Replace(' ', ' ').Trim() is var s
        ? WhitespaceRegex().Replace(s, " ")
        : "";

    private static DateTime? ParseDate(string text) =>
        DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var d) ? d : null;

    /// <summary>"Intel Corporation - Display - 31.0.101.4146" or "Intel net Driver Update (24.40.0.4)".</summary>
    internal static Version? ParseVersion(string title) =>
        TitleVersionRegex().Match(title) is { Success: true } m && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;

    [GeneratedRegex("(?s)<tr id=\"([0-9a-f-]{36})_R\\d+\".*?</tr>")]
    private static partial Regex RowRegex();

    [GeneratedRegex("(?s)<td[^>]*>(.*?)</td>")]
    private static partial Regex CellRegex();

    [GeneratedRegex("_originalSize\">(\\d+)<")]
    private static partial Regex SizeRegex();

    [GeneratedRegex("(?s)<div[^>]*>\\s*([^<]+?)\\s*</div>")]
    private static partial Regex HwidRegex();

    [GeneratedRegex("&REV_\\w+$", RegexOptions.IgnoreCase)]
    private static partial Regex RevisionRegex();

    [GeneratedRegex("&(?:SUBSYS|REV)_.*$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericRegex();

    [GeneratedRegex(@"^ACPI\\VEN_(\w+)&DEV_(\w+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AcpiRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+){1,3})\)?\s*$")]
    private static partial Regex TitleVersionRegex();

    [GeneratedRegex(@"Windows (?:11 Client(?: S)?|- Client|10(?: S)?|Server[^,]*|8\.1|8|7)[^,]*(?:, (?:version \w+|Vibranium)[^,]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex ProductRegex();
}
