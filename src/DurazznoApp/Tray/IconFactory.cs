using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using Durazzno.Core;
using Svg;
using SystemInformation = System.Windows.Forms.SystemInformation;

namespace Durazzno.Tray;

/// <summary>Renders the embedded Lucide SVGs and Durazzno/Galaxy logos into bitmaps and icons.</summary>
internal static class IconFactory
{
    private static readonly Assembly Assembly = typeof(IconFactory).Assembly;
    private static readonly Dictionary<(string Name, int Argb, int Size), Bitmap> LucideCache = new();
    private static readonly Dictionary<string, Bitmap> LogoCache = new();

    /// <summary>Lucide icon tinted with <paramref name="color"/>. Cached; do not dispose the result.</summary>
    public static Bitmap Lucide(string name, Color color, int size)
    {
        var key = (name, color.ToArgb(), size);
        if (LucideCache.TryGetValue(key, out var cached))
            return cached;

        using var stream = Assembly.GetManifestResourceStream($"Lucide.{name}")
            ?? throw new ArgumentException($"Unknown Lucide icon '{name}'.", nameof(name));
        using var reader = new StreamReader(stream);
        var svg = reader.ReadToEnd().Replace("currentColor", $"#{color.R:X2}{color.G:X2}{color.B:X2}");

        var bitmap = SvgDocument.FromSvg<SvgDocument>(svg).Draw(size, size);
        LucideCache[key] = bitmap;
        return bitmap;
    }

    /// <summary>Embedded PNG (e.g. "Plugins.powercuts") scaled to <paramref name="size"/>. Cached.</summary>
    public static Bitmap Embedded(string name, int size)
    {
        var key = $"{name}@{size}";
        if (LogoCache.TryGetValue(key, out var cached))
            return cached;

        using var stream = Assembly.GetManifestResourceStream(name) ?? throw new ArgumentException($"Unknown resource '{name}'.");
        using var source = new Bitmap(stream);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, size, size));
        }

        LogoCache[key] = bitmap;
        return bitmap;
    }

    /// <summary>
    /// An embedded PNG in one color, keeping its shape (alpha): the Monochrome menu icons for the ones that are not
    /// Lucide outlines (PowerCuts). Cached.
    /// </summary>
    public static Bitmap EmbeddedTinted(string name, int size, Color color)
    {
        var key = $"{name}@{size}#{color.ToArgb():X8}";
        if (LogoCache.TryGetValue(key, out var cached))
            return cached;

        var source = Embedded(name, size);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        // Every pixel becomes the color; only its transparency is kept.
        var matrix = new ColorMatrix(
        [
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, color.A / 255f, 0],
            [color.R / 255f, color.G / 255f, color.B / 255f, 0, 1],
        ]);
        using (var attributes = new ImageAttributes())
        using (var g = Graphics.FromImage(bitmap))
        {
            attributes.SetColorMatrix(matrix);
            g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, size, size, GraphicsUnit.Pixel, attributes);
        }

        LogoCache[key] = bitmap;
        return bitmap;
    }

    /// <summary>Full-resolution logo (transparent background). Cached; do not dispose the result.</summary>
    public static Bitmap Logo(LogoKind kind)
    {
        var name = $"{kind.ToString().ToLowerInvariant()}_icon";

        if (!LogoCache.TryGetValue(name, out var bitmap))
        {
            using var stream = Assembly.GetManifestResourceStream($"Logos.{name}")
                ?? throw new ArgumentException($"Unknown logo '{name}'.");
            LogoCache[name] = bitmap = LoadLargestIconFrame(stream);
        }

        return bitmap;
    }

    /// <summary>Tray icon: the logo with an optional status dot in the bottom-right corner.</summary>
    public static Icon TrayIcon(Bitmap logo, Color? badge)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            g.DrawImage(logo, new Rectangle(0, 0, size, size));

            if (badge is { } badgeColor)
            {
                var d = Math.Max(6, size * 7 / 16);
                var rect = new Rectangle(size - d, size - d, d - 1, d - 1);
                using var ring = new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20));
                using var fill = new SolidBrush(badgeColor);
                g.FillEllipse(ring, rect);
                rect.Inflate(-1, -1);
                g.FillEllipse(fill, rect);
            }
        }

        return ToIcon(canvas);
    }

    public static Icon ToIcon(Bitmap bitmap)
    {
        // Wrap the PNG in a single-image ICO container; Windows reads PNG-compressed icon frames natively.
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);

        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0);
            w.Write((short)1);
            w.Write((short)1);
            w.Write((byte)(bitmap.Width >= 256 ? 0 : bitmap.Width));
            w.Write((byte)(bitmap.Height >= 256 ? 0 : bitmap.Height));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write((int)png.Length);
            w.Write(6 + 16);
            w.Write(png.ToArray());
        }

        ico.Position = 0;
        return new Icon(ico);
    }

    /// <summary>Reads the biggest frame of an .ico (the bundled logos are single 256px PNG frames).</summary>
    private static Bitmap LoadLargestIconFrame(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var data = buffer.ToArray();

        int count = BitConverter.ToUInt16(data, 4), best = -1, bestSize = -1;
        for (var i = 0; i < count; i++)
        {
            var size = data[6 + i * 16] == 0 ? 256 : data[6 + i * 16];
            if (size > bestSize)
                (best, bestSize) = (i, size);
        }

        if (best >= 0)
        {
            var length = BitConverter.ToInt32(data, 6 + best * 16 + 8);
            var offset = BitConverter.ToInt32(data, 6 + best * 16 + 12);
            if (data.AsSpan(offset, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            {
                using var png = new MemoryStream(data, offset, length);
                return new Bitmap(png);
            }
        }

        using var icon = new Icon(new MemoryStream(data), 256, 256);
        return icon.ToBitmap();
    }
}
