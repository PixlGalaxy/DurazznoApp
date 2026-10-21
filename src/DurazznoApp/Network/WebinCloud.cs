namespace Durazzno.Network;

/// <summary>
/// Where Durazzno's files live on Webin Cloud Server. To switch servers change only <see cref="Server"/>.
/// </summary>
internal static class WebinCloud
{
    // Test server: "http://192.168.0.150:5002"   Production: "https://webincloud.itzgalaxy.com"
    public const string Server = "https://webincloud.itzgalaxy.com";

    // Path name "durazznoapp" on Webin Cloud Server, with one folder per kind of file.

    /// <summary>Folder holding manifest.json, manifest.json.sig and DurazznoApp.exe.</summary>
    public const string MainPath = "durazznoapp/DurazznoApp/Main";

    /// <summary>Folder holding the Apps catalog (apps.json, apps.json.sig), the app files and their icons.</summary>
    public const string AppsPath = "durazznoapp/DurazznoApp/Apps";

    public const string ManifestFile = "manifest.json";

    public static Uri DefaultManifestUri => FileUri(MainPath, ManifestFile);

    /// <summary>Raw download link of a file: {Server}/backend/api/public/{path name/folder}/{file}/download?inline=1&amp;path=</summary>
    public static Uri FileUri(string pathName, string fileName, string server = Server) =>
        new($"{server.TrimEnd('/')}/backend/api/public/{pathName}/{Uri.EscapeDataString(fileName)}/download?inline=1&path=");

    /// <summary>
    /// The link of another file in the same folder as <paramref name="source"/> (the manifest): works for Webin
    /// Cloud links (…/{file}/download?…), plain URLs (…/{file}) and local files.
    /// </summary>
    public static Uri Sibling(Uri source, string fileName)
    {
        if (Uri.TryCreate(fileName, UriKind.Absolute, out var absolute))
            return absolute;
        if (source.IsFile)
            return new Uri(Path.Combine(Path.GetDirectoryName(source.LocalPath)!, fileName));

        var path = source.AbsolutePath;
        if (path.EndsWith("/download", StringComparison.OrdinalIgnoreCase))
        {
            var folder = path[..path.LastIndexOf('/', path.Length - "/download".Length - 1)];
            return new UriBuilder(source) { Path = $"{folder}/{Uri.EscapeDataString(fileName)}/download" }.Uri;
        }
        return new Uri(source, fileName);
    }

    /// <summary>
    /// Webin Cloud Server's health endpoint on the manifest's server ({server}/backend/api/health): the cheap "is it up"
    /// check, which stays out of the public file logs. A local manifest file is its own check.
    /// </summary>
    public static Uri HealthUri(Uri manifest) => manifest.IsFile ? manifest : new Uri(manifest, "/backend/api/health");

    /// <summary>The signature next to a file: manifest.json → manifest.json.sig.</summary>
    public static Uri SignatureOf(Uri file)
    {
        var path = Uri.UnescapeDataString(file.IsFile ? file.LocalPath : file.AbsolutePath);
        var name = path.EndsWith("/download", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(path[..^"/download".Length])
            : Path.GetFileName(path);
        return Sibling(file, name + ".sig");
    }

    /// <summary>
    /// A manifest override (settings "manifestUrl"): empty = default; a bare server
    /// ("https://host" or "http://ip:port") = that server's Main folder manifest; anything else is used as is.
    /// </summary>
    public static Uri ResolveManifest(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return DefaultManifestUri;
        var raw = configured!.Trim(); // not null here (.NET Framework's IsNullOrWhiteSpace has no annotation)
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return !uri.IsFile && uri.AbsolutePath.Trim('/').Length == 0 && uri.Query.Length == 0
                ? FileUri(MainPath, ManifestFile, raw)
                : uri;
        return new Uri(Path.GetFullPath(raw));
    }
}
