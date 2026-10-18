using System.Management;
using Durazzno.Core;

namespace Durazzno.Hardware.Drivers;

/// <summary>
/// The PC (or motherboard) maker and its driver support page. Razer, ASRock, ASUS, MSI, Gigabyte and others publish
/// no public driver API: their BIOS, firmware and board-specific drivers live on per-model support pages, so DriverHub
/// links there instead of guessing. Pre-built PCs use the system maker; self-built ones (placeholder system strings
/// like "System manufacturer") use the motherboard maker.
/// </summary>
internal sealed record PcMaker(string Name, string Model, string SupportUrl)
{
    public static PcMaker? Detect()
    {
        try
        {
            string? Get(string cls, string prop)
            {
                using var searcher = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
                foreach (var o in searcher.Get())
                    return (o[prop] as string)?.Trim();
                return null;
            }

            var maker = Get("Win32_ComputerSystem", "Manufacturer") ?? "";
            var model = Get("Win32_ComputerSystem", "Model") ?? "";
            var sku = Get("Win32_ComputerSystem", "SystemSKUNumber") ?? "";
            if (IsPlaceholder(maker))
            {
                maker = Get("Win32_BaseBoard", "Manufacturer") ?? "";
                model = Get("Win32_BaseBoard", "Product") ?? "";
                sku = "";
            }

            var q = Uri.EscapeDataString(model);
            var m = maker.ToLowerInvariant();
            return m switch
            {
                _ when m.Contains("razer") => new PcMaker("Razer", model,
                    $"https://mysupport.razer.com/app/answers/list/kw/{Uri.EscapeDataString(sku.Length >= 9 ? sku[..9] : model)}"),
                _ when m.Contains("asrock") => new PcMaker("ASRock", model, $"https://www.asrock.com/support/index.asp?cat=Drivers&Model={q}"),
                _ when m.Contains("asus") => new PcMaker("ASUS", model, $"https://www.asus.com/support/search-result/?searchType=support&searchKey={q}"),
                _ when m.Contains("micro-star") || m.Contains("msi") => new PcMaker("MSI", model, $"https://www.msi.com/search/{q}"),
                _ when m.Contains("gigabyte") => new PcMaker("Gigabyte", model, $"https://www.gigabyte.com/Search?kw={q}"),
                _ when m.Contains("dell") || m.Contains("alienware") => new PcMaker("Dell", model, "https://www.dell.com/support/home/?app=drivers"),
                _ when m.Contains("hp") || m.Contains("hewlett") => new PcMaker("HP", model, "https://support.hp.com/drivers"),
                _ when m.Contains("lenovo") => new PcMaker("Lenovo", model, "https://pcsupport.lenovo.com/"),
                _ when m.Contains("acer") => new PcMaker("Acer", model, "https://www.acer.com/us-en/support/drivers-and-manuals"),
                _ when m.Contains("microsoft") => new PcMaker("Microsoft Surface", model, "https://support.microsoft.com/surface/download-drivers-and-firmware-for-surface"),
                _ when m.Contains("samsung") => new PcMaker("Samsung", model, "https://www.samsung.com/support/"),
                _ when m.Length > 0 => new PcMaker(maker, model, $"https://www.bing.com/search?q={Uri.EscapeDataString($"{maker} {model} drivers")}"),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"PC maker detection: {ex.Message}");
            return null;
        }
    }

    private static bool IsPlaceholder(string s) =>
        s.Length == 0 || s.Contains("System manufacturer", StringComparison.OrdinalIgnoreCase)
        || s.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase) || s.Contains("Default string", StringComparison.OrdinalIgnoreCase)
        || s.Contains("O.E.M.", StringComparison.OrdinalIgnoreCase);
}
