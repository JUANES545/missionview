namespace WindowScatter;

internal sealed class MissionViewMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
{
    internal MissionViewMenuRenderer() : base(new Colors()) => RoundedEdges = false;

    private sealed class Colors : System.Windows.Forms.ProfessionalColorTable
    {
        private static System.Drawing.Color Ground => System.Drawing.Color.FromArgb(32, 37, 34);
        private static System.Drawing.Color Selected => System.Drawing.Color.FromArgb(53, 67, 57);
        public override System.Drawing.Color ToolStripDropDownBackground => Ground;
        public override System.Drawing.Color MenuBorder => Ground;
        public override System.Drawing.Color MenuItemSelected => Selected;
        public override System.Drawing.Color MenuItemBorder => Selected;
        public override System.Drawing.Color CheckBackground => Selected;
        public override System.Drawing.Color CheckSelectedBackground => Selected;
        public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(60, 69, 63);
        public override System.Drawing.Color SeparatorLight => Ground;
        public override System.Drawing.Color ImageMarginGradientBegin => Ground;
        public override System.Drawing.Color ImageMarginGradientMiddle => Ground;
        public override System.Drawing.Color ImageMarginGradientEnd => Ground;
    }
}
