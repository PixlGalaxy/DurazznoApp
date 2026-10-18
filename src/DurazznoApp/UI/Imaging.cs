using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Durazzno.UI;

internal static class Imaging
{
    private static readonly Dictionary<System.Drawing.Bitmap, BitmapSource> Cache = new();

    /// <summary>GDI+ bitmap to a frozen WPF image (cached per bitmap instance).</summary>
    public static BitmapSource ToBitmapSource(System.Drawing.Bitmap bitmap)
    {
        if (Cache.TryGetValue(bitmap, out var cached))
            return cached;

        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();

        Cache[bitmap] = image;
        return image;
    }

    private static readonly Dictionary<string, BitmapSource?> Embeds = new();

    /// <summary>Embedded PNG by logical name, e.g. "Plugins.powercuts" (cached, frozen).</summary>
    public static BitmapSource? Embedded(string name)
    {
        if (Embeds.TryGetValue(name, out var cached))
            return cached;

        using var stream = typeof(Imaging).Assembly.GetManifestResourceStream(name);
        BitmapSource? image = null;
        if (stream is not null)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }

        Embeds[name] = image;
        return image;
    }

    public static SolidColorBrush Brush(string hex, byte alpha = 255)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        color.A = alpha;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
