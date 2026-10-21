using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text.Json;
using Durazzno.Core;
using Durazzno.Updates;

namespace Durazzno.Network;

/// <summary>
/// Talks to Webin Cloud Server. The server is a plain file host: every file (manifest, exe, zip) is a URL.
/// </summary>
internal static class ServerClient
{
    // The server and path names are in WebinCloud.cs. The manifest can be overridden per machine with "manifestUrl"
    // in %LOCALAPPDATA%\DurazznoApp\settings.json.

    // Same endpoint Windows uses to decide whether there is internet at all.
    private const string InternetProbeUrl = "http://www.msftconnecttest.com/connecttest.txt";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static readonly HttpClient Http = CreateClient();

    public static Uri ResolveManifestUri(string? configured) => WebinCloud.ResolveManifest(configured);

    /// <summary>Downloads manifest.json and manifest.json.sig, checks the signature, then parses and validates it.</summary>
    public static async Task<UpdateManifest> GetManifestAsync(Uri uri, CancellationToken ct)
    {
        var bytes = await GetSignedAsync(uri, "update manifest", ct).ConfigureAwait(false);

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{uri} did not return a valid update manifest.", ex);
        }

        if (manifest is null)
            throw new InvalidDataException("The update manifest is empty.");
        manifest.Source = uri;
        manifest.Validate();
        return manifest;
    }

    /// <summary>
    /// Downloads a file and its .sig (published by publish-main.ps1 / publish-apps.ps1) and returns the bytes only when the
    /// signature verifies against Durazzno's public key.
    /// </summary>
    public static async Task<byte[]> GetSignedAsync(Uri uri, string what, CancellationToken ct)
    {
        var bytes = await ReadAllBytesAsync(uri, ct).ConfigureAwait(false);
        string signature;
        try
        {
            signature = System.Text.Encoding.ASCII.GetString(await ReadAllBytesAsync(WebinCloud.SignatureOf(uri), ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new InvalidDataException($"The {what} is not signed (its .sig file is missing).", ex);
        }
        if (!UpdateSignature.IsValid(bytes, signature))
            throw new InvalidDataException($"The {what}'s signature is not valid; it was ignored.");
        return bytes;
    }

    /// <summary>Opens a remote (http/https) or local (file://) resource for reading. Length is null when unknown.</summary>
    public static async Task<(Stream Stream, long? Length)> OpenReadAsync(Uri uri, CancellationToken ct)
    {
        if (uri.IsFile)
        {
            var file = File.OpenRead(uri.LocalPath);
            return (file, file.Length);
        }

        var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return (stream, response.Content.Headers.ContentLength);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Uri uri, CancellationToken ct)
    {
        if (uri.IsFile)
            return await File.ReadAllBytesAsync(uri.LocalPath, ct).ConfigureAwait(false);
        using var response = await Http.GetAsync(uri, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cheap reachability check for the status dot (WebinCloud.HealthUri): a HEAD request, no body. Any HTTP answer
    /// below 500 means the server is up; the full manifest is fetched far less often.
    /// </summary>
    public static async Task<bool> PingAsync(Uri uri, CancellationToken ct)
    {
        if (uri.IsFile)
            return File.Exists(uri.LocalPath);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (int)response.StatusCode < 500;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    public static async Task<bool> HasInternetAsync(CancellationToken ct)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
            return false;

        try
        {
            using var response = await Http.GetAsync(InternetProbeUrl, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateClient()
    {
        // Certificates are validated (the old app ran with verify=False).
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DurazznoApp", AppInfo.VersionText));
        return client;
    }
}
