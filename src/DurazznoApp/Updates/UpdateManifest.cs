using System.Text.Json.Serialization;
using Durazzno.Core;

namespace Durazzno.Updates;

/// <summary>
/// manifest.json published on Webin Cloud Server. Relative URLs are resolved against the manifest's own URL.
/// </summary>
internal sealed class UpdateManifest
{
    public PackageInfo App { get; init; } = null!;

    /// <summary>Versions below this are flagged as unsupported in the menu.</summary>
    public string? MinimumSupportedVersion { get; init; }

    [JsonIgnore]
    public Uri Source { get; set; } = null!;

    [JsonIgnore]
    public Version AppVersion => AppInfo.Normalize(Version.Parse(App.Version));

    public bool IsAppNewer => AppVersion > AppInfo.Version;

    public bool HasUpdate => IsAppNewer;

    public bool IsCurrentVersionSupported =>
        MinimumSupportedVersion is null || AppInfo.Version >= AppInfo.Normalize(Version.Parse(MinimumSupportedVersion));

    public Uri Resolve(PackageInfo package) => Network.WebinCloud.Sibling(Source, package.Url);

    public void Validate()
    {
        if (App is null)
            throw new InvalidDataException("Manifest has no \"app\" entry.");

        App.Validate("app", requireSemVer: true);

        if (MinimumSupportedVersion is not null && !Version.TryParse(MinimumSupportedVersion, out _))
            throw new InvalidDataException("\"minimumSupportedVersion\" is not a version.");
    }
}

internal sealed class PackageInfo
{
    public string Version { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long? Size { get; init; }
    public string? Notes { get; init; }

    public void Validate(string name, bool requireSemVer)
    {
        if (string.IsNullOrWhiteSpace(Version) || (requireSemVer && !System.Version.TryParse(Version, out _)))
            throw new InvalidDataException($"\"{name}.version\" is missing or invalid.");
        if (string.IsNullOrWhiteSpace(Url))
            throw new InvalidDataException($"\"{name}.url\" is missing.");
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException($"\"{name}.sha256\" must be a 64-character hex SHA-256.");
    }
}
