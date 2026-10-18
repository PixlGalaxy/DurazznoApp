using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Durazzno.Tray;

/// <summary>Flat, rounded-hover context menu that follows the light/dark palette.</summary>
internal sealed class MenuRenderer(Palette palette) : ToolStripProfessionalRenderer(new PaletteColorTable(palette))
{
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
            return;

        var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = RoundedRectangle(rect, 5);
        using var brush = new SolidBrush(palette.Hover);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? palette.Text : palette.MutedText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(palette.Border);
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = palette.Text;
        base.OnRenderArrow(e);
    }

    private static GraphicsPath RoundedRectangle(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class PaletteColorTable(Palette p) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => p.Background;
        public override Color ImageMarginGradientBegin => p.Background;
        public override Color ImageMarginGradientMiddle => p.Background;
        public override Color ImageMarginGradientEnd => p.Background;
        public override Color MenuBorder => p.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => p.Hover;
        public override Color SeparatorDark => p.Border;
        public override Color SeparatorLight => p.Background;
    }
}
