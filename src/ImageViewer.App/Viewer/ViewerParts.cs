// 1 枚表示（QuickLookView）と 2 枚比較（CompareView）で同じに描くもの・同じに判断するもの
using System.Drawing.Drawing2D;
using ImageViewer.Core.Imaging;

namespace ImageViewer.App.Viewer;

internal static class ViewerParts
{
    /// <summary>同じ画像のまま中身が変わったか（一覧を読み直したときに、表示を読み直すかの判断）</summary>
    public static bool Modified(ImageFile before, ImageFile after) =>
        before.LastWriteTimeUtc != after.LastWriteTimeUtc || before.Length != after.Length || before.Version != after.Version;

    /// <summary>ガイドに出すキーの名前</summary>
    public static string KeyName(Keys key) => key switch
    {
        Keys.Oem5 => "¥",
        Keys.Oem7 => "^",
        _ => new KeysConverter().ConvertToString(key) ?? key.ToString(),
    };

    /// <summary>枠内に中央寄せした画像の位置（allowUpscale が false なら、枠より小さい画像は等倍のまま）</summary>
    public static Rectangle Fit(Size image, Rectangle area, bool allowUpscale)
    {
        double scale = Math.Min((double)area.Width / image.Width, (double)area.Height / image.Height);
        if (!allowUpscale) scale = Math.Min(1.0, scale);
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    /// <summary>チェックの印（画像の左上に、直径 diameter の丸とチェック）</summary>
    public static void DrawCheck(Graphics g, Rectangle image, int diameter)
    {
        int d = diameter;
        var r = new Rectangle(image.X + 8, image.Y + 8, d, d);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(Color.FromArgb(232, 112, 0))) g.FillEllipse(fill, r);
        using (var ring = new Pen(Color.White, 2.5f)) g.DrawEllipse(ring, r);
        using (var tick = new Pen(Color.White, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(tick, new[]
            {
                new PointF(r.X + d * 0.27f, r.Y + d * 0.52f),
                new PointF(r.X + d * 0.44f, r.Y + d * 0.68f),
                new PointF(r.X + d * 0.74f, r.Y + d * 0.34f),
            });
        g.SmoothingMode = SmoothingMode.None;
    }
}
