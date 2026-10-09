// モザイク・ぼかし。画像の中の選んだ範囲（いくつでも。四角・楕円・自由な形）だけにかける。
// 保存先が元のファイルなら上書きする（切り抜きの上書きと同じ条件で）
using ImageViewer.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageViewer.Core.Editing;

/// <summary>かけ方。ぼかしはガウス、ボックスぼかしは周りを同じ重みで平均する。塗りつぶしは黒一色（強さは使わない。一番確実に隠せる）</summary>
public enum MaskEffect { Mosaic, Blur, BoxBlur, Fill }

public enum MaskShape { Rectangle, Ellipse, Freehand }

/// <summary>
/// モザイク・ぼかしをかける範囲（画像座標）。X0〜X1 / Y0〜Y1 は外枠。
/// 自由な形（Freehand）の Points は外枠に対する割合（0〜1）なので、外枠を動かす・大きさを変える・引き継ぐだけで形が付いてくる
/// </summary>
public sealed record MaskRegion(MaskShape Shape, double X0, double Y0, double X1, double Y1, IReadOnlyList<(double X, double Y)>? Points = null)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;

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

    /// <summary>縦・横それぞれの倍率で拡大・縮小する（表示用の縮小や、大きさの違う画像への引き継ぎ用）</summary>
    public MaskRegion Scale(double sx, double sy) => WithBounds(X0 * sx, Y0 * sy, X1 * sx, Y1 * sy);

    /// <summary>自由な形の頂点（画像座標）</summary>
    public IEnumerable<(double X, double Y)> Polygon() =>
        (Points ?? Array.Empty<(double X, double Y)>()).Select(p => (X0 + p.X * Width, Y0 + p.Y * Height));

    /// <summary>点が範囲の中か（クリックで選ぶとき用）</summary>
    public bool Contains(double x, double y)
    {
        if (x < X0 || x > X1 || y < Y0 || y > Y1) return false;
        switch (Shape)
        {
            case MaskShape.Ellipse:
                double dx = (x - (X0 + X1) / 2) / (Width / 2), dy = (y - (Y0 + Y1) / 2) / (Height / 2);
                return dx * dx + dy * dy <= 1;
            case MaskShape.Freehand:
                return Crossings(y).Count(cx => cx <= x) % 2 == 1;
            default:
                return true;
        }
    }

    /// <summary>横の線 y が範囲の境目と交わる x（小さい順）。自由な形は偶奇規則で、楕円は左右の 2 点</summary>
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
            {
                var poly = Polygon().ToList();
                for (int i = 0; i < poly.Count; i++)
                {
                    var a = poly[i];
                    var b = poly[(i + 1) % poly.Count];
                    if ((a.Y <= y && y < b.Y) || (b.Y <= y && y < a.Y))
                        xs.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                }
                xs.Sort();
                break;
            }
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
}

public static class Masker
{
    public const int MinLevel = 1, MaxLevel = 10, DefaultLevel = 4;

    /// <summary>ぼかしの強さ（ガウスの σ）の上限。大きすぎると重いだけで見た目は変わらない</summary>
    private const float MaxSigma = 50;

    /// <summary>ボックスぼかしの半径の上限（ガウスの σ の上限と同じくらいのぼけ方まで）</summary>
    private const int MaxBoxRadius = 100;

    /// <summary>塗りつぶしの色</summary>
    private static readonly Rgba32 FillColor = new(0, 0, 0);

    /// <summary>
    /// 強さ（1〜10）から、モザイクの 1 マスの大きさ（px）を決める。画像の長い辺に対する割合なので、
    /// 大きさの違う画像にも同じ見た目でかかる（ぼかしはこの半分を σ に、ボックスぼかしはこの半分を半径にする）
    /// </summary>
    public static int EffectSize(int width, int height, int level) =>
        Math.Max(2, (int)Math.Round(Math.Max(width, height) * Math.Clamp(level, MinLevel, MaxLevel) / 300.0));

    /// <summary>image の boxes（四角）の範囲に、マスの大きさ size でモザイク・ぼかしをかける（範囲の外は変えない）</summary>
    public static void Apply(Image<Rgba32> image, IEnumerable<Rectangle> boxes, MaskEffect effect, int size) =>
        Apply(image, boxes.Select(b => MaskRegion.Rect(b.Left, b.Top, b.Right, b.Bottom)), effect, size);

    /// <summary>image の regions の範囲に、マスの大きさ size でモザイク・ぼかしをかける（範囲の外は変えない）</summary>
    public static void Apply(Image<Rgba32> image, IEnumerable<MaskRegion> regions, MaskEffect effect, int size)
    {
        if (size < 2 && effect != MaskEffect.Fill) return;
        foreach (var region in regions)
        {
            var box = Cropper.ClampBox(region.X0, region.Y0, region.X1, region.Y1, image.Width, image.Height);
            if (box.Width <= 0 || box.Height <= 0) continue;
            if (effect == MaskEffect.Fill)
            {
                FillRegion(image, region, box);
                continue;
            }
            if (region.Shape == MaskShape.Rectangle)
            {
                image.Mutate(c => Effect(c, box, effect, size));
                continue;
            }
            // 四角以外: 外枠を切り出してかけてから、形の中の画素だけを戻す
            using var part = image.Clone(c => c.Crop(box));
            part.Mutate(c => Effect(c, new Rectangle(0, 0, box.Width, box.Height), effect, size));
            image.ProcessPixelRows(part, (dst, src) =>
            {
                for (int y = 0; y < box.Height; y++)
                {
                    var xs = region.Crossings(box.Top + y + 0.5); // 画素の中心で判定する
                    if (xs.Count < 2) continue;
                    var dstRow = dst.GetRowSpan(box.Top + y);
                    var srcRow = src.GetRowSpan(y);
                    for (int k = 0; k + 1 < xs.Count; k += 2)
                    {
                        // 中心（x + 0.5）が xs[k]〜xs[k+1] に入る画素
                        int from = Math.Max(box.Left, (int)Math.Ceiling(xs[k] - 0.5));
                        int to = Math.Min(box.Right, (int)Math.Ceiling(xs[k + 1] - 0.5));
                        for (int x = from; x < to; x++) dstRow[x] = srcRow[x - box.Left];
                    }
                }
            });
        }
    }

    private static void Effect(IImageProcessingContext c, Rectangle box, MaskEffect effect, int size)
    {
        // ImageSharp のモザイクはマスが、ぼかしは差し渡し（半径 × 2 + 1）が範囲の幅・高さより大きいと例外になるので、
        // 小さい範囲（ドラッグし始めの数 px など）では範囲に収まるまで弱める
        int shortSide = Math.Min(box.Width, box.Height);
        int maxRadius = (shortSide - 1) / 2;
        switch (effect)
        {
            case MaskEffect.Mosaic:
                int cell = Math.Min(size, shortSide);
                if (cell >= 2) c.Pixelate(cell, box);
                break;
            case MaskEffect.BoxBlur:
                int radius = Math.Min(Math.Clamp(size / 2, 1, MaxBoxRadius), maxRadius);
                if (radius >= 1) c.BoxBlur(radius, box);
                break;
            default:
                // ガウスの半径は ceil(σ × 3)
                float sigma = Math.Min(Math.Min(MaxSigma, size / 2f), maxRadius / 3f);
                if (sigma > 0 && maxRadius >= 1) c.GaussianBlur(sigma, box);
                break;
        }
    }

    /// <summary>範囲の形の中を塗りつぶす（四角も楕円・自由な形も、行ごとに境目との交点の間を塗る）</summary>
    private static void FillRegion(Image<Rgba32> image, MaskRegion region, Rectangle box)
    {
        image.ProcessPixelRows(rows =>
        {
            for (int y = box.Top; y < box.Bottom; y++)
            {
                var xs = region.Crossings(y + 0.5);
                var row = rows.GetRowSpan(y);
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int from = Math.Max(box.Left, (int)Math.Ceiling(xs[k] - 0.5));
                    int to = Math.Min(box.Right, (int)Math.Ceiling(xs[k + 1] - 0.5));
                    for (int x = from; x < to; x++) row[x] = FillColor;
                }
            }
        });
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
    public static string SaveMasked(Image<Rgba32> image, IReadOnlyList<MaskRegion> regions, MaskEffect effect, int level, string src, string dst)
    {
        if (Cropper.IsSameFile(src, dst))
        {
            Cropper.EnsureOverwritable(src);
            Cropper.EnsureKeepsMetadata(image);
        }
        using var masked = image.Clone();
        Apply(masked, regions, effect, EffectSize(image.Width, image.Height, level));
        return Cropper.SaveEdited(masked, src, dst);
    }

    /// <summary>ファイルを読み、ほかの画像（fromWidth × fromHeight）で決めた範囲を引き継いでかけ、保存する（一括用。dst が src なら上書き）</summary>
    /// <returns>実際に保存したパス</returns>
    public static string MaskCarried(string src, IReadOnlyList<MaskRegion> regions, int fromWidth, int fromHeight, MaskEffect effect, int level, string dst)
    {
        bool overwrite = Cropper.IsSameFile(src, dst);
        if (overwrite) Cropper.EnsureOverwritable(src);
        using var image = ImageLoader.Load(src);
        if (overwrite) Cropper.EnsureKeepsMetadata(image);
        Apply(image, CarryRegions(regions, fromWidth, fromHeight, image.Width, image.Height), effect, EffectSize(image.Width, image.Height, level));
        return Cropper.SaveEdited(image, src, dst);
    }
}
