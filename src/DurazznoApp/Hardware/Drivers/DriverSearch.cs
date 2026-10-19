using System.Diagnostics;
using Durazzno.Core;

namespace Durazzno.Hardware.Drivers;

internal sealed record DriverSearchResult(
    List<DriverUpdate> Offers,
    int FromWindowsUpdate,
    int FromCatalog,
    int FromMakers,
    string? WindowsUpdateError,
    string? CatalogError,
    string? MakersError)
{
    public bool HasErrors => WindowsUpdateError is not null || CatalogError is not null || MakersError is not null;
}

/// <summary>
/// One driver update check across every source (Windows Update, Microsoft Update Catalog, Intel / NVIDIA / AMD), run
/// in parallel; each source failing on its own does not stop the others. Used by the DriverHub page and the
/// background check. On return, <see cref="DeviceInfo.Update"/> is set on each device with a newer driver.
/// </summary>
internal static class DriverSearch
{
    public static async Task<DriverSearchResult> RunAsync(List<DeviceInfo> allDevices, IProgress<(int Done, int Total, string? Device)>? catalogProgress,
        Action? windowsUpdateFinished, CancellationToken ct)
    {
        var devices = allDevices.Where(d => d.IsPresent).ToList(); // unused (not connected) devices get no updates
        var watch = Stopwatch.StartNew();
        string? wuError = null, catalogError = null, makersError = null;

        var wuTask = Capture(DriverUpdates.SearchAsync(ct), e => wuError = e, windowsUpdateFinished);
        var catalogTask = Capture(DriverCatalog.SearchAsync(devices, catalogProgress, ct), e => catalogError = e);
        var makersTask = Capture(VendorDrivers.SearchAsync(devices, ct), e => makersError = e);

        var wu = await wuTask;
        var catalog = await catalogTask;
        var makers = await makersTask;
        List<DriverUpdate> offers = [.. wu, .. catalog, .. makers];
        DriverUpdates.Match(devices, offers);

        var withUpdate = devices.Where(d => d.Update is not null).ToList();
        var fromWu = withUpdate.Count(d => d.Update!.Source == DriverSource.WindowsUpdate);
        var fromMakers = withUpdate.Count(d => d.Update!.IsManufacturer);
        Log.Info($"DriverHub: {wu.Count} Windows Update + {catalog.Count} catalog + {makers.Count} maker offers, "
                 + $"{withUpdate.Count} devices with newer drivers in {watch.Elapsed.TotalSeconds:0.0}s");
        return new DriverSearchResult(offers, fromWu, withUpdate.Count - fromWu - fromMakers, fromMakers, wuError, catalogError, makersError);
    }

    private static async Task<List<DriverUpdate>> Capture(Task<List<DriverUpdate>> task, Action<string> onError, Action? finished = null)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onError(ex.Message);
            return [];
        }
        finally
        {
            finished?.Invoke();
        }
    }
}
