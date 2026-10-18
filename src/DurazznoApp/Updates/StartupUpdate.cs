using System.Net.NetworkInformation;
using Durazzno.Core;
using Durazzno.Network;

namespace Durazzno.Updates;

/// <summary>
/// The update check that runs when Durazzno starts (normally with Windows), before any window or tray icon exists:
/// an update downloaded earlier is installed right away; otherwise the manifest is read (6 s at most) and a newer
/// version is downloaded (2 minutes at most) and installed. On success the new exe is started and this process ends;
/// anything that fails just lets the current version start normally (the running app retries in the background).
/// </summary>
internal static class StartupUpdate
{
    /// <summary>True when an update was installed and the new version launched: the caller must exit.</summary>
    public static bool TryApply(SettingsStore settings, bool open)
    {
        try
        {
            if (UpdateService.StagedVersion is null)
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                    return false;
                var uri = ServerClient.ResolveManifestUri(settings.Current.ManifestUrl);
                using var check = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var manifest = ServerClient.GetManifestAsync(uri, check.Token).GetAwaiter().GetResult();
                if (!manifest.IsAppNewer)
                    return false;
                using var download = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                UpdateService.StageAsync(manifest, null, download.Token).GetAwaiter().GetResult();
            }

            UpdateService.HandOff(open);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Startup update skipped: {ex}");
            return false;
        }
    }
}
