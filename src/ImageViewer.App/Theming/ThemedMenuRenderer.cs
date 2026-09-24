// メニュー（☰・右クリック・並び順）を今の配色で描く。ToolStripManager.Renderer に設定するので、
// 描くたびに Theme.Current を読む（配色を変えても作り直さなくてよい）
using System.Drawing.Drawing2D;

namespace ImageViewer.App.Theming;

public sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemedMenuRenderer() : base(new PaletteColors()) => RoundedEdges = false;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var p = Theme.Current;
        e.TextColor = !e.Item.Enabled ? p.Disabled
            : e.Item.ForeColor is var c && c != SystemColors.ControlText && c != SystemColors.MenuText ? c // 個別に色を付けた項目（削除など）
            : p.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? Theme.Current.Disabled : Theme.Current.TextSecondary;
        base.OnRenderArrow(e);
    }

    /// <summary>標準のチェックの印は黒い画像でダークでは見えないので、自分で描く</summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        var g = e.Graphics;
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = r.Height;
        using var pen = new Pen(Theme.Current.Text, Math.Max(1.5f, s / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(r.X + s * 0.22f, r.Y + s * 0.52f),
            new PointF(r.X + s * 0.42f, r.Y + s * 0.72f),
            new PointF(r.X + s * 0.78f, r.Y + s * 0.30f),
        });
        g.SmoothingMode = old;
    }

    private sealed class PaletteColors : ProfessionalColorTable
    {
        private static Palette P => Theme.Current;
        public PaletteColors() => UseSystemColors = false;

        public override Color ToolStripDropDownBackground => P.Surface;
        public override Color ImageMarginGradientBegin => P.Surface;
        public override Color ImageMarginGradientMiddle => P.Surface;
        public override Color ImageMarginGradientEnd => P.Surface;
        public override Color MenuBorder => P.Border;
        public override Color MenuItemBorder => P.Hover;
        public override Color MenuItemSelected => P.Hover;
        public override Color MenuItemSelectedGradientBegin => P.Hover;
        public override Color MenuItemSelectedGradientEnd => P.Hover;
        public override Color MenuItemPressedGradientBegin => P.Pressed;
        public override Color MenuItemPressedGradientMiddle => P.Pressed;
        public override Color MenuItemPressedGradientEnd => P.Pressed;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
        public override Color SeparatorDark => P.Border;
        public override Color SeparatorLight => P.Surface;
        public override Color ToolStripBorder => P.Border;
        public override Color ToolStripGradientBegin => P.Surface;
        public override Color ToolStripGradientMiddle => P.Surface;
        public override Color ToolStripGradientEnd => P.Surface;
        public override Color MenuStripGradientBegin => P.Surface;
        public override Color MenuStripGradientEnd => P.Surface;
        public override Color StatusStripGradientBegin => P.Surface;
        public override Color StatusStripGradientEnd => P.Surface;
    }
}
