using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WindowScatter;

internal sealed class MissionViewMenuRenderer : ToolStripProfessionalRenderer
{
    internal MissionViewMenuRenderer() : base(new Colors()) => RoundedEdges = false;

    internal static (Color Background, Color Foreground) Palette(bool selected, bool pressed, bool isChecked,
        bool enabled, bool highContrast, bool informational = false)
    {
        bool active = enabled && (selected || pressed);
        if (highContrast)
            return (active ? SystemColors.Highlight : SystemColors.Menu,
                active ? SystemColors.HighlightText : enabled || informational ? SystemColors.MenuText : SystemColors.GrayText);
        return (active ? Color.FromArgb(53, 67, 57) : isChecked ? Color.FromArgb(43, 54, 46) : Color.FromArgb(32, 37, 34),
            enabled ? Color.FromArgb(243, 245, 244) : Color.FromArgb(188, 197, 190));
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Palette(false, false, false, true, SystemInformation.HighContrast).Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var colors = ItemColors(e.Item);
        if (!SystemInformation.HighContrast)
        {
            using var ground = new SolidBrush(Palette(false, false, false, true, false).Background);
            e.Graphics.FillRectangle(ground, new Rectangle(Point.Empty, e.Item.Size));
            var bounds = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            if (bounds.Width < 12 || bounds.Height < 12) return;
            using var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, 12, 12, 180, 90);
            path.AddArc(bounds.Right - 12, bounds.Top, 12, 12, 270, 90);
            path.AddArc(bounds.Right - 12, bounds.Bottom - 12, 12, 12, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - 12, 12, 12, 90, 90);
            path.CloseFigure();
            using var surface = new SolidBrush(colors.Background);
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(surface, path);
            e.Graphics.Restore(state);
            return;
        }
        using var brush = new SolidBrush(colors.Background);
        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = ItemColors(e.Item).Foreground;
        e.TextFormat &= ~TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, e.TextColor, e.TextFormat);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var colors = ItemColors(e.Item);
        using var pen = new Pen(colors.Foreground, 2);
        var r = e.ImageRectangle;
        e.Graphics.DrawLines(pen, [new Point(r.Left + 2, r.Top + r.Height / 2),
            new Point(r.Left + r.Width / 2 - 1, r.Bottom - 3), new Point(r.Right - 2, r.Top + 2)]);
    }

    private static (Color Background, Color Foreground) ItemColors(ToolStripItem item)
        => Palette(item.Selected, item.Pressed, item is ToolStripMenuItem { Checked: true }, item.Enabled,
            SystemInformation.HighContrast, item.Tag is "Version");

    private sealed class Colors : ProfessionalColorTable
    {
        private static Color Ground => SystemInformation.HighContrast ? SystemColors.Menu : Color.FromArgb(32, 37, 34);
        private static Color Selected => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(53, 67, 57);
        public override Color ToolStripDropDownBackground => Ground;
        public override Color MenuBorder => Ground;
        public override Color MenuItemSelected => Selected;
        public override Color MenuItemSelectedGradientBegin => Selected;
        public override Color MenuItemSelectedGradientEnd => Selected;
        public override Color MenuItemPressedGradientBegin => Selected;
        public override Color MenuItemPressedGradientMiddle => Selected;
        public override Color MenuItemPressedGradientEnd => Selected;
        public override Color MenuItemBorder => Selected;
        public override Color CheckBackground => Selected;
        public override Color CheckSelectedBackground => Selected;
        public override Color CheckPressedBackground => Selected;
        public override Color SeparatorDark => SystemInformation.HighContrast ? SystemColors.MenuText : Color.FromArgb(105, 121, 110);
        public override Color SeparatorLight => Ground;
        public override Color ImageMarginGradientBegin => Ground;
        public override Color ImageMarginGradientMiddle => Ground;
        public override Color ImageMarginGradientEnd => Ground;
    }
}
