// モザイク・ぼかし。画像の中の選んだ範囲（いくつでも。四角（傾け・倒しもできる）・楕円・自由な形）だけにかける。
// 保存先が元のファイルなら上書きする（切り抜きの上書きと同じ条件で）
using ImageViewer.Core.Imaging;
using SkiaSharp;

namespace ImageViewer.Core.Editing;

/// <summary>かけ方。ぼかしはガウス、ボックスぼかしは周りを同じ重みで平均する。塗りつぶしは黒一色（強さは使わない。一番確実に隠せる）</summary>
public enum MaskEffect { Mosaic, Blur, BoxBlur, Fill }

public enum MaskShape { Rectangle, Ellipse, Freehand }

/// <summary>
/// モザイク・ぼかしをかける範囲（画像座標）。X0〜X1 / Y0〜Y1 は外枠（四角を傾けたときは、傾ける前の四角）。
/// 自由な形（Freehand）の Points は外枠に対する割合（0〜1）なので、外枠を動かす・大きさを変える・引き継ぐだけで形が付いてくる
/// </summary>
public sealed record MaskRegion(MaskShape Shape, double X0, double Y0, double X1, double Y1, IReadOnlyList<(double X, double Y)>? Points = null)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;

    /// <summary>四角の傾き（度。時計回りが正）。傾ける前の四角の中心を軸に回す。四角のほかは使わない（赤枠の Annotation.Angle と同じ）</summary>
    public double Angle { get; init; }

    /// <summary>
    /// 四角の左右の辺の倒れ（度。-MaxSkew〜MaxSkew。正なら上の辺が右へずれる平行四辺形）。幅・高さは変えない。
    /// 倒してから Angle だけ回す。四角のほかは使わない（赤枠の Annotation.Skew と同じ）
    /// </summary>
    public double Skew { get; init; }

    public const double MaxSkew = Annotator.MaxSkew;

    /// <summary>左右の辺の倒れの tan（縦に 1 進むと横にどれだけずれるか）</summary>
    public double Shear => Shape == MaskShape.Rectangle ? Math.Tan(Math.Clamp(Skew, -MaxSkew, MaxSkew) * Math.PI / 180) : 0;

    /// <summary>傾けたか倒した四角か（false なら画像の縦横にそろった四角か、四角以外）</summary>
    public bool Transformed => Shape == MaskShape.Rectangle && (Angle % 360 != 0 || Shear != 0);

    public (double X, double Y) Center => ((X0 + X1) / 2, (Y0 + Y1) / 2);

    /// <summary>傾ける前の四角での位置（中心からの横 u・縦 v）を、画像の位置にする</summary>
    public (double X, double Y) FromLocal(double u, double v)
    {
        double rad = (Shape == MaskShape.Rectangle ? Angle : 0) * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        var (cx, cy) = Center;
        return (cx + u * cos - v * sin, cy + u * sin + v * cos);
    }

    /// <summary>四隅（倒して傾けた後。左上・右上・右下・左下の順。四角以外は外枠の四隅）</summary>
    public (double X, double Y)[] Corners
    {
        get
        {
            double hx = Width / 2, hy = Height / 2, s = Shear * hy; // 上の辺は右へ s、下の辺は左へ s ずれる
            return new[] { FromLocal(-hx + s, -hy), FromLocal(hx + s, -hy), FromLocal(hx - s, hy), FromLocal(-hx - s, hy) };
        }
    }

    /// <summary>形が収まる四角（傾けた・倒した四角は四隅が収まる四角）</summary>
    public (double Left, double Top, double Right, double Bottom) Bounds
    {
        get
        {
            if (!Transformed) return (X0, Y0, X1, Y1);
            var c = Corners;
            return (c.Min(p => p.X), c.Min(p => p.Y), c.Max(p => p.X), c.Max(p => p.Y));
        }
    }

    /// <summary>
    /// 形を変えた後の範囲が、width × height の画像からはみ出さないか（四隅が収まる四角で見る）。
    /// 前の範囲 before が既にはみ出していた辺は、それより外へ出なければよい（はみ出した範囲を縮めて戻せるように）
    /// </summary>
    public bool StaysInside(MaskRegion before, int width, int height)
    {
        const double eps = 1e-6;
        var (left, top, right, bottom) = Bounds;
        var (bl, bt, br, bb) = before.Bounds;
        return left >= Math.Min(0, bl) - eps && top >= Math.Min(0, bt) - eps
               && right <= Math.Max(width, br) + eps && bottom <= Math.Max(height, bb) + eps;
    }

    /// <summary>形を変えずに動かす</summary>
    public MaskRegion Offset(double dx, double dy) => WithBounds(X0 + dx, Y0 + dy, X1 + dx, Y1 + dy);

    public static MaskRegion Rect(double x0, double y0, double x1, double y1) => new(MaskShape.Rectangle, x0, y0, x1, y1);

    /// <summary>なぞった点（画像座標）から自由な形の範囲を作る。点が 3 つ未満か、幅・高さが無ければ null</summary>
    public static MaskRegion? FromPath(IReadOnlyList<(double X, double Y)> path)
    {
        if (path.Count < 3) return null;
        double x0 = path.Min(p => p.X), y0 = path.Min(p => p.Y), x1 = path.Max(p => p.X), y1 = path.Max(p => p.Y);
        if (x1 - x0 <= 0 || y1 - y0 <= 0) return null;
        var points = path.Select(p => ((p.X - x0) / (x1 - x0), (p.Y - y0) / (y1 - y0))).ToList();
        return new MaskRegion(MaskShape.Freehand, x0, y0, x1, y1, points);
    }

    /// <summary>外枠を置き換える（形はそのまま付いてくる）</summary>
    public MaskRegion WithBounds(double x0, double y0, double x1, double y1) => this with { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 };

    /// <summary>
    /// 縦・横それぞれの倍率で拡大・縮小する（表示用の縮小や、大きさの違う画像への引き継ぎ用）。
    /// 傾けた四角は、中心の位置と、傾ける前の幅・高さをそれぞれの倍率で変え、傾きと倒れはそのまま（縦横の倍率が違うときは形が少し変わる）
    /// </summary>
    public MaskRegion Scale(double sx, double sy) => WithBounds(X0 * sx, Y0 * sy, X1 * sx, Y1 * sy);

    /// <summary>自由な形の頂点（画像座標）</summary>
    public IEnumerable<(double X, double Y)> Polygon() =>
        (Points ?? Array.Empty<(double X, double Y)>()).Select(p => (X0 + p.X * Width, Y0 + p.Y * Height));

    /// <summary>点が範囲の中か（クリックで選ぶとき用）</summary>
    public bool Contains(double x, double y)
    {
        var (left, top, right, bottom) = Bounds;
        if (x < left || x > right || y < top || y > bottom) return false;
        switch (Shape)
        {
            case MaskShape.Ellipse:
                double dx = (x - (X0 + X1) / 2) / (Width / 2), dy = (y - (Y0 + Y1) / 2) / (Height / 2);
                return dx * dx + dy * dy <= 1;
            case MaskShape.Freehand:
                return Crossings(y).Count(cx => cx <= x) % 2 == 1;
            default:
                if (!Transformed) return true;
                // 傾けた四角: 傾ける前の向きへ戻して、倒れの分を戻してから四角の中か見る
                double rad = Angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
                var (cx, cy) = Center;
                double u = (x - cx) * cos + (y - cy) * sin, v = -(x - cx) * sin + (y - cy) * cos;
                u += Shear * v; // 縦に v 進んだ所では、左右の辺が -Shear × v ずれている
                return Math.Abs(u) <= Width / 2 && Math.Abs(v) <= Height / 2;
        }
    }

    /// <summary>横の線 y が範囲の境目と交わる x（小さい順）。自由な形・傾けた四角は偶奇規則で、楕円は左右の 2 点</summary>
    internal List<double> Crossings(double y)
    {
        var xs = new List<double>();
        switch (Shape)
        {
            case MaskShape.Ellipse:
            {
                double ry = Height / 2, dy = (y - (Y0 + Y1) / 2) / ry;
                if (ry <= 0 || Math.Abs(dy) >= 1) break;
                double half = Width / 2 * Math.Sqrt(1 - dy * dy), cx = (X0 + X1) / 2;
                xs.Add(cx - half);
                xs.Add(cx + half);
                break;
            }
            case MaskShape.Freehand:
                PolygonCrossings(Polygon().ToList(), y, xs);
                break;
            case MaskShape.Rectangle when Transformed:
                PolygonCrossings(Corners, y, xs);
                break;
            default:
                if (y >= Y0 && y < Y1)
                {
                    xs.Add(X0);
                    xs.Add(X1);
                }
                break;
        }
        return xs;
    }

    /// <summary>多角形の辺と横の線 y が交わる x を xs に足して、小さい順に並べる</summary>
    private static void PolygonCrossings(IReadOnlyList<(double X, double Y)> poly, double y, List<double> xs)
    {
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            if ((a.Y <= y && y < b.Y) || (b.Y <= y && y < a.Y))
                xs.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
        }
        xs.Sort();
    }
}

public static class Masker
{
    public const int MinLevel = 1, MaxLevel = 10, DefaultLevel = 4;

    /// <summary>ぼかしの強さ（ガウスの σ）の上限。大きすぎると重いだけで見た目は変わらない</summary>
    private const float MaxSigma = 50;

    /// <summary>ボックスぼかしの半径の上限（ガウスの σ の上限と同じくらいのぼけ方まで）</summary>
    private const int MaxBoxRadius = 100;

    /// <summary>塗りつぶしの色</summary>
    private static readonly Rgba FillColor = new(0, 0, 0);

    /// <summary>
    /// 強さ（1〜10）から、モザイクの 1 マスの大きさ（px）を決める。画像の長い辺に対する割合なので、
    /// 大きさの違う画像にも同じ見た目でかかる（ぼかしはこの半分を σ に、ボックスぼかしはこの半分を半径にする）
    /// </summary>
    public static int EffectSize(int width, int height, int level) =>
        Math.Max(2, (int)Math.Round(Math.Max(width, height) * Math.Clamp(level, MinLevel, MaxLevel) / 300.0));

    /// <summary>image の regions の範囲に、マスの大きさ size でモザイク・ぼかしをかける（その場で変える。範囲の外は変えない）</summary>
    public static void Apply(SKBitmap image, IEnumerable<MaskRegion> regions, MaskEffect effect, int size) =>
        SkiaEdit.Edit(image, rgba => ApplyRgba(rgba, regions, effect, size));

    private static void ApplyRgba(SKBitmap image, IEnumerable<MaskRegion> regions, MaskEffect effect, int size)
    {
        if (size < 2 && effect != MaskEffect.Fill) return;
        foreach (var region in regions)
        {
            var (left, top, right, bottom) = region.Bounds;
            var box = Cropper.ClampBox(left, top, right, bottom, image.Width, image.Height);
            if (box.Width <= 0 || box.Height <= 0) continue;
            if (effect == MaskEffect.Fill)
            {
                FillRegion(image, region, box);
                continue;
            }
            // 範囲（傾けた四角は四隅が収まる四角）を切り出してかけ（範囲の外の画素はぼかしに混ぜない。端の画素が続いているものとして扱う）、
            // 縦横にそろった四角はそのまま、ほかは形の中の画素だけを戻す（モザイクのマスは画像の縦横にそろえたまま）
            using var part = SkiaEdit.Crop(image, box);
            Effect(part, effect, size);
            for (int y = 0; y < box.Height; y++)
            {
                var dstRow = image.Row(box.Top + y);
                var srcRow = part.Row(y);
                if (region.Shape == MaskShape.Rectangle && !region.Transformed)
                {
                    srcRow.CopyTo(dstRow.Slice(box.Left, box.Width));
                    continue;
                }
                var xs = region.Crossings(box.Top + y + 0.5); // 画素の中心で判定する
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    // 中心（x + 0.5）が xs[k]〜xs[k+1] に入る画素
                    int from = Math.Max(box.Left, (int)Math.Ceiling(xs[k] - 0.5));
                    int to = Math.Min(box.Right, (int)Math.Ceiling(xs[k + 1] - 0.5));
                    for (int x = from; x < to; x++) dstRow[x] = srcRow[x - box.Left];
                }
            }
        }
    }

    /// <summary>切り出した範囲の画像全体にかける</summary>
    private static void Effect(SKBitmap part, MaskEffect effect, int size)
    {
        // マス・ぼかしの差し渡し（半径 × 2 + 1）が範囲の幅・高さより大きくならないよう、
        // 小さい範囲（ドラッグし始めの数 px など）では範囲に収まるまで弱める（ImageSharp のときと同じ強さ）
        int shortSide = Math.Min(part.Width, part.Height);
        int maxRadius = (shortSide - 1) / 2;
        switch (effect)
        {
            case MaskEffect.Mosaic:
                int cell = Math.Min(size, shortSide);
                if (cell >= 2) SkiaEdit.Pixelate(part, cell);
                break;
            case MaskEffect.BoxBlur:
                int radius = Math.Min(Math.Clamp(size / 2, 1, MaxBoxRadius), maxRadius);
                if (radius >= 1) SkiaEdit.BoxBlur(part, radius);
                break;
            default:
                // ガウスの半径は ceil(σ × 3)
                float sigma = Math.Min(Math.Min(MaxSigma, size / 2f), maxRadius / 3f);
                if (sigma > 0 && maxRadius >= 1) SkiaEdit.GaussianBlur(part, sigma);
                break;
        }
    }

    /// <summary>範囲の形の中を塗りつぶす（四角も楕円・自由な形も、行ごとに境目との交点の間を塗る）</summary>
    private static void FillRegion(SKBitmap image, MaskRegion region, SKRectI box)
    {
        for (int y = box.Top; y < box.Bottom; y++)
        {
            var xs = region.Crossings(y + 0.5);
            var row = image.Row(y);
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int from = Math.Max(box.Left, (int)Math.Ceiling(xs[k] - 0.5));
                int to = Math.Min(box.Right, (int)Math.Ceiling(xs[k + 1] - 0.5));
                for (int x = from; x < to; x++) row[x] = FillColor;
            }
        }
    }

    /// <summary>
    /// ほかの画像（fromWidth × fromHeight）で決めた範囲を、次の画像へ引き継ぐ。
    /// 大きさが同じならそのまま、違えば縦・横それぞれ画像に対する割合で合わせる（隠したい所の位置を保つ）
    /// </summary>
    public static List<MaskRegion> CarryRegions(IEnumerable<MaskRegion> regions, int fromWidth, int fromHeight, int toWidth, int toHeight)
    {
        double sx = (double)toWidth / fromWidth, sy = (double)toHeight / fromHeight;
        return regions.Select(r => r.Scale(sx, sy)).ToList();
    }

    /// <summary>
    /// 保存先: 出力フォルダ\元の名前_mosaic（ぼかしは _blur、ボックスぼかしは _boxblur、塗りつぶしは _fill）.拡張子
    /// （書き出せない形式は .jpg）。既にあれば (2)… を付ける
    /// </summary>
    public static string OutputPathFor(string source, string outputFolder, MaskEffect effect) =>
        ImageSaver.UniquePath(Path.Combine(outputFolder,
            Path.GetFileNameWithoutExtension(source) + SuffixFor(effect) + ImageSaver.ExtensionFor(OutputFormat.Keep, Path.GetExtension(source))));

    public static string SuffixFor(MaskEffect effect) => effect switch
    {
        MaskEffect.Blur => "_blur",
        MaskEffect.BoxBlur => "_boxblur",
        MaskEffect.Fill => "_fill",
        _ => "_mosaic",
    };

    /// <summary>src から読み込み済みの画像に、regions の範囲でかけて保存する（image 自体は変えない。dst が src なら上書き）</summary>
    /// <returns>実際に保存したパス</returns>
    public static string SaveMasked(SKBitmap image, IReadOnlyList<MaskRegion> regions, MaskEffect effect, int level, string src, string dst)
    {
        if (SourceGuard.IsSameFile(src, dst))
        {
            SourceGuard.EnsureReplaceable(src);
            SourceGuard.EnsureKeepsMetadata(src);
        }
        using var masked = image.Copy();
        Apply(masked, regions, effect, EffectSize(image.Width, image.Height, level));
        return SourceGuard.Save(masked, src, dst);
    }


    /// <summary>ファイルを読み、ほかの画像（fromWidth × fromHeight）で決めた範囲を引き継いでかけ、保存する（一括用。dst が src なら上書き）</summary>
    /// <returns>実際に保存したパス</returns>
    public static string MaskCarried(string src, IReadOnlyList<MaskRegion> regions, int fromWidth, int fromHeight, MaskEffect effect, int level, string dst)
    {
        bool overwrite = SourceGuard.IsSameFile(src, dst);
        if (overwrite) SourceGuard.EnsureReplaceable(src);
        using var image = ImageLoader.Load(src);
        if (overwrite) SourceGuard.EnsureKeepsMetadata(src);
        Apply(image, CarryRegions(regions, fromWidth, fromHeight, image.Width, image.Height), effect, EffectSize(image.Width, image.Height, level));
        return SourceGuard.Save(image, src, dst);
    }
}
