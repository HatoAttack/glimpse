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

    // ---- フッターの操作ボタン ----

    public static readonly IconPainter Resize = (g, r, c) => Stroke(g, r, c, 1.5f, p =>
    {
        p.AddLines(new PointF[] { new(2.5f, 9), new(2.5f, 13.5f), new(7, 13.5f) });
        p.StartFigure();
        p.AddLine(2.5f, 13.5f, 13.5f, 2.5f);
        p.StartFigure();
        p.AddLines(new PointF[] { new(9, 2.5f), new(13.5f, 2.5f), new(13.5f, 7) });
    });

    public static readonly IconPainter Crop = (g, r, c) => Stroke(g, r, c, 1.5f, p =>
    {
        p.AddLines(new PointF[] { new(4, 1.5f), new(4, 12), new(14.5f, 12) });
        p.StartFigure();
        p.AddLines(new PointF[] { new(1.5f, 4), new(12, 4), new(12, 14.5f) });
    });

    public static readonly IconPainter Combine = (g, r, c) => Stroke(g, r, c, 1.4f, p =>
    {
        p.AddRectangle(new RectangleF(1.5f, 4, 6, 8));
        p.AddRectangle(new RectangleF(8.5f, 4, 6, 8));
    });

    public static readonly IconPainter Rename = (g, r, c) => Stroke(g, r, c, 1.4f, p =>
        p.AddPolygon(new PointF[] { new(3, 13), new(6, 13), new(13, 6), new(10, 3), new(3, 10) }));

    public static readonly IconPainter Move = (g, r, c) => Stroke(g, r, c, 1.4f, p =>
    {
        p.AddPolygon(new PointF[] { new(1.5f, 4), new(6, 4), new(7.5f, 5.5f), new(14.5f, 5.5f), new(14.5f, 13), new(1.5f, 13) });
        p.StartFigure();
        p.AddLine(6, 9.5f, 10.5f, 9.5f);
        p.StartFigure();
        p.AddLines(new PointF[] { new(9, 8), new(10.5f, 9.5f), new(9, 11) });
    });

    public static readonly IconPainter Trash = (g, r, c) => Stroke(g, r, c, 1.4f, p =>
    {
        p.AddLine(3, 4.5f, 13, 4.5f);
        p.StartFigure();
        p.AddLines(new PointF[] { new(6.5f, 4.5f), new(6.5f, 3), new(9.5f, 3), new(9.5f, 4.5f) });
        p.StartFigure();
        p.AddLines(new PointF[] { new(4.5f, 4.5f), new(5.2f, 13), new(10.8f, 13), new(11.5f, 4.5f) });
    });

    public static readonly IconPainter Close = (g, r, c) => Stroke(g, r, c, 1.6f, p =>
    {
        p.AddLine(4, 4, 12, 12);
        p.StartFigure();
        p.AddLine(12, 4, 4, 12);
    });

    /// <summary>⋯（その他）</summary>
    public static readonly IconPainter More = (g, r, c) => Fill(g, r, c, p =>
    {
        p.AddEllipse(1.7f, 6.7f, 2.6f, 2.6f);
        p.AddEllipse(6.7f, 6.7f, 2.6f, 2.6f);
        p.AddEllipse(11.7f, 6.7f, 2.6f, 2.6f);
    });

    /// <summary>チェックの色の丸（色は呼ぶ側が渡す）</summary>
    public static IconPainter Dot(Color color) => (g, r, _) => Fill(g, r, color, p => p.AddEllipse(4, 4, 8, 8));

    private static void Fill(Graphics g, RectangleF r, Color color, Action<GraphicsPath> build)
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
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
        g.SmoothingMode = old;
    }

    /// <summary>ⓘ（詳細パネル）</summary>
    public static readonly IconPainter Info = (g, r, c) => Stroke(g, r, c, 1.4f, p =>
    {
        p.AddEllipse(2, 2, 12, 12);
        p.StartFigure();
        p.AddLine(8, 7.2f, 8, 11.5f);
        p.StartFigure();
        p.AddLine(8, 4.8f, 8, 4.9f);
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
