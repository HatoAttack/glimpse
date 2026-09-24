// ツールバー・フッターの線のアイコン。16 × 16 の升目で描いた形を、渡された枠の大きさに合わせて描く
// （画像を持たないので DPI・配色が変わってもにじまない）
using System.Drawing.Drawing2D;

namespace ImageViewer.App.Chrome;

public delegate void IconPainter(Graphics g, RectangleF r, Color color);

public static class Icons
{
    public static readonly IconPainter Menu = (g, r, c) => Stroke(g, r, c, 1.5f, p =>
    {
        p.AddLine(2.5f, 4, 13.5f, 4);
        p.StartFigure();
        p.AddLine(2.5f, 8, 13.5f, 8);
        p.StartFigure();
        p.AddLine(2.5f, 12, 13.5f, 12);
    });

    public static readonly IconPainter Sidebar = (g, r, c) => Stroke(g, r, c, 1.4f, p =>
    {
        p.AddRectangle(new RectangleF(2, 3, 12, 10));
        p.StartFigure();
        p.AddLine(6, 3, 6, 13);
    });

    public static readonly IconPainter Back = (g, r, c) => Stroke(g, r, c, 1.6f, p => p.AddLines(new PointF[] { new(10, 3), new(5, 8), new(10, 13) }));

    public static readonly IconPainter Forward = (g, r, c) => Stroke(g, r, c, 1.6f, p => p.AddLines(new PointF[] { new(6, 3), new(11, 8), new(6, 13) }));

    public static readonly IconPainter Up = (g, r, c) => Stroke(g, r, c, 1.6f, p =>
    {
        p.AddLine(8, 13, 8, 3);
        p.StartFigure();
        p.AddLines(new PointF[] { new(3.5f, 7.5f), new(8, 3), new(12.5f, 7.5f) });
    });

    public static readonly IconPainter ChevronDown = (g, r, c) => Stroke(g, r, c, 1.8f, p => p.AddLines(new PointF[] { new(4, 6), new(8, 10), new(12, 6) }));

    /// <summary>サムネイルの大きさ（フッターのスライダーの横）</summary>
    public static readonly IconPainter Grid = (g, r, c) => Stroke(g, r, c, 1.3f, p =>
    {
        p.AddRectangle(new RectangleF(3, 3, 4, 4));
        p.AddRectangle(new RectangleF(9, 3, 4, 4));
        p.AddRectangle(new RectangleF(3, 9, 4, 4));
        p.AddRectangle(new RectangleF(9, 9, 4, 4));
    });

    /// <summary>ライト（今の配色がライトのときにテーマのボタンに出す）</summary>
    public static readonly IconPainter Sun = (g, r, c) => Stroke(g, r, c, 1.4f, p =>
    {
        p.AddEllipse(5, 5, 6, 6);
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            p.StartFigure();
            p.AddLine(8 + 5 * (float)Math.Cos(a), 8 + 5 * (float)Math.Sin(a), 8 + 6.6f * (float)Math.Cos(a), 8 + 6.6f * (float)Math.Sin(a));
        }
    });

    /// <summary>ダーク（三日月）</summary>
    public static readonly IconPainter Moon = (g, r, c) => Stroke(g, r, c, 1.4f, p =>
    {
        float inner = 3 * MathF.Sqrt(2); // 内側の弧: 中心 (11, 5)、(8, 2) から (14, 8) まで
        p.AddArc(11 - inner, 5 - inner, inner * 2, inner * 2, 225, -180);
        p.AddArc(2, 2, 12, 12, 0, 270); // 外側の弧: 中心 (8, 8)、(14, 8) から (8, 2) まで
        p.CloseFigure();
    });

    private static void Stroke(Graphics g, RectangleF r, Color color, float width, Action<GraphicsPath> build)
    {
        using var path = new GraphicsPath();
        build(path);
        float scale = Math.Min(r.Width, r.Height) / 16f;
        using var m = new Matrix();
        m.Translate(r.X + (r.Width - 16 * scale) / 2, r.Y + (r.Height - 16 * scale) / 2);
        m.Scale(scale, scale);
        path.Transform(m);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, width * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
        g.SmoothingMode = old;
    }

    /// <summary>角の丸い四角（ボタンの地・入力欄）</summary>
    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, RectangleF r, float radius, Color color)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
        g.SmoothingMode = old;
    }
}
