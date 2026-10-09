// 枠・矢印。画像の特定の所を指し示すために、四角の枠（角の丸み・太さ）と矢印（太さ・先端の大きさ）を描く。影も付けられる。
// 形は「点から形の縁までの距離」で表して画素ごとに塗る（縁は距離でなめらかに、影は同じ形をずらしてぼかす）。
// 表示用の縮小した画像にも同じ処理で描けるので、見た目と保存したものが合う
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageViewer.Core.Editing;

public enum AnnotationKind { Frame, Arrow }

/// <summary>
/// 枠か矢印 1 つ（画像座標）。枠は (X0, Y0)〜(X1, Y1) が線の中心を通る四角、矢印は (X0, Y0) から (X1, Y1) へ向かう（先端が X1, Y1）。
/// Thickness は線の太さ（px）、CornerRadius は枠の角の丸み（px。短い辺の半分まで）、HeadSize は矢印の先端の大きさ（太さの何倍か）
/// </summary>
public sealed record Annotation(AnnotationKind Kind, double X0, double Y0, double X1, double Y1,
    Rgba32 Color, double Thickness, double CornerRadius, double HeadSize, bool Shadow)
{
    public double Width => Math.Abs(X1 - X0);
    public double Height => Math.Abs(Y1 - Y0);
    public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));

    /// <summary>位置も太さも同じ倍率で拡大・縮小する（表示用の縮小した画像に描くとき用）</summary>
    public Annotation Scale(double s) => this with
    {
        X0 = X0 * s, Y0 = Y0 * s, X1 = X1 * s, Y1 = Y1 * s, Thickness = Thickness * s, CornerRadius = CornerRadius * s,
    };

    public Annotation Offset(double dx, double dy) => this with { X0 = X0 + dx, Y0 = Y0 + dy, X1 = X1 + dx, Y1 = Y1 + dy };

    /// <summary>点から形の縁までの距離（px。形の中は負）。クリックで選ぶときにも使う</summary>
    public double Distance(double x, double y) => new AnnotationShape(this).Distance(x, y);
}

/// <summary>描くために前もって計算した形</summary>
internal readonly struct AnnotationShape
{
    private readonly AnnotationKind _kind;
    // 枠: 中心と、外側・内側の四角（半分の大きさと角の丸み）。内側が無い（線が太くて埋まる）なら _hole は false
    private readonly double _cx, _cy, _ohx, _ohy, _or, _ihx, _ihy, _ir;
    private readonly bool _hole;
    // 矢印: 始点・向き（単位ベクトル）・軸（始点からの長さ）・軸の太さの半分・先端の三角（根元の位置と幅の半分）
    private readonly double _sx, _sy, _ux, _uy, _length, _shaftEnd, _half, _headBase, _headHalf;
    private readonly bool _shaft;

    /// <summary>形の外枠からはみ出す長さ（線の太さ・先端の幅）</summary>
    public double Reach { get; }

    public AnnotationShape(Annotation a)
    {
        this = default;
        _kind = a.Kind;
        double t = Math.Max(0, a.Thickness);
        if (a.Kind == AnnotationKind.Frame)
        {
            double hx = a.Width / 2, hy = a.Height / 2;
            double r = Math.Clamp(a.CornerRadius, 0, Math.Min(hx, hy));
            _cx = (a.X0 + a.X1) / 2;
            _cy = (a.Y0 + a.Y1) / 2;
            _ohx = hx + t / 2;
            _ohy = hy + t / 2;
            _or = r > 0 ? r + t / 2 : 0; // 丸みが 0 なら外側の角も丸めない
            _ihx = hx - t / 2;
            _ihy = hy - t / 2;
            _ir = Math.Max(0, r - t / 2);
            _hole = _ihx > 0 && _ihy > 0;
            Reach = t / 2;
        }
        else
        {
            _sx = a.X0;
            _sy = a.Y0;
            _length = a.Length;
            if (_length > 0)
            {
                _ux = (a.X1 - a.X0) / _length;
                _uy = (a.Y1 - a.Y0) / _length;
            }
            // 先端は長さも幅も「太さ × HeadSize」。矢印が短ければ矢印の長さに収まるまで小さくして、軸は描かない
            double head = Math.Min(Math.Max(0, a.HeadSize) * t, _length);
            _headBase = _length - head;
            _headHalf = head / 2;
            _half = t / 2;
            _shaft = _headBase > 0;
            _shaftEnd = _length - head / 2; // 先端の三角に半分もぐらせる（継ぎ目にすき間を作らない）
            Reach = Math.Max(_half, _headHalf);
        }
    }

    public double Distance(double x, double y)
    {
        if (_kind == AnnotationKind.Frame)
        {
            double px = x - _cx, py = y - _cy;
            double outer = RoundedBox(px, py, _ohx, _ohy, _or);
            return _hole ? Math.Max(outer, -RoundedBox(px, py, _ihx, _ihy, _ir)) : outer;
        }
        if (_length <= 0) return double.MaxValue;
        // 矢印に沿った座標（u: 始点から先端へ、v: 横）
        double dx = x - _sx, dy = y - _sy;
        double u = dx * _ux + dy * _uy, v = Math.Abs(dy * _ux - dx * _uy);
        double d = Triangle(u, v);
        if (_shaft) d = Math.Min(d, RoundedBox(u - _shaftEnd / 2, v, _shaftEnd / 2, _half, 0));
        return d;
    }

    /// <summary>中心が原点の角丸の四角までの距離（hx, hy は半分の大きさ）</summary>
    private static double RoundedBox(double px, double py, double hx, double hy, double r)
    {
        double qx = Math.Abs(px) - (hx - r), qy = Math.Abs(py) - (hy - r);
        double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
    }

    /// <summary>先端の三角（(根元, ±幅の半分) と (先, 0)）までの距離。v は 0 以上（上下対称なので半分だけ見る）</summary>
    private double Triangle(double u, double v)
    {
        double head = _length - _headBase;
        if (head <= 0) return Math.Sqrt((u - _length) * (u - _length) + v * v);
        // 斜めの辺: (根元, 幅の半分) → (先, 0)
        double ex = head, ey = -_headHalf;
        double wx = u - _headBase, wy = v - _headHalf;
        double k = Math.Clamp((wx * ex + wy * ey) / (ex * ex + ey * ey), 0, 1);
        double sx = wx - ex * k, sy = wy - ey * k;
        double slant = Math.Sqrt(sx * sx + sy * sy);
        // 根元の辺: u = 根元、v は 0〜幅の半分
        double bx = u - _headBase, by = Math.Max(v - _headHalf, 0);
        double baseEdge = Math.Sqrt(bx * bx + by * by);
        bool inside = u >= _headBase && ex * wy - ey * wx <= 0;
        double dist = Math.Min(slant, baseEdge);
        return inside ? -dist : dist;
    }
}

public static class Annotator
{
    public const double MinHeadSize = 2, MaxHeadSize = 8, DefaultHeadSize = 4;

    /// <summary>影の濃さ（0〜1）</summary>
    private const double ShadowAlpha = 0.5;

    /// <summary>太さの初めの値（px）。画像の長い辺に対する割合で、大きい画像でも細すぎないように</summary>
    public static int DefaultThickness(int width, int height) => Math.Max(2, (int)Math.Round(Math.Max(width, height) / 200.0));

    /// <summary>影のずれ（右下へ）とぼかしの幅（px）。線の太さに合わせる</summary>
    public static (double Offset, double Blur) ShadowOf(double thickness) => (Math.Max(1, thickness * 0.4), Math.Max(1.5, thickness * 0.6));

    /// <summary>image に枠・矢印を順に描く（後のものほど上）</summary>
    public static void Draw(Image<Rgba32> image, IEnumerable<Annotation> items)
    {
        foreach (var a in items)
        {
            if (a.Thickness <= 0 || a.Color.A == 0) continue;
            if (a.Kind == AnnotationKind.Arrow && a.Length <= 0) continue;
            var shape = new AnnotationShape(a);
            var (offset, blur) = a.Shadow ? ShadowOf(a.Thickness) : (0, 0);
            double margin = shape.Reach + 1 + offset + blur;
            int left = Math.Max(0, (int)Math.Floor(Math.Min(a.X0, a.X1) - margin)), right = Math.Min(image.Width, (int)Math.Ceiling(Math.Max(a.X0, a.X1) + margin));
            int top = Math.Max(0, (int)Math.Floor(Math.Min(a.Y0, a.Y1) - margin)), bottom = Math.Min(image.Height, (int)Math.Ceiling(Math.Max(a.Y0, a.Y1) + margin));
            if (left >= right || top >= bottom) continue;
            var color = a.Color;
            bool shadow = a.Shadow;
            double opacity = color.A / 255.0;
            image.ProcessPixelRows(rows =>
            {
                for (int y = top; y < bottom; y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (int x = left; x < right; x++)
                    {
                        double px = x + 0.5, py = y + 0.5; // 画素の中心
                        if (shadow)
                        {
                            double sd = shape.Distance(px - offset, py - offset);
                            if (sd < blur)
                            {
                                double k = Math.Clamp((sd + blur) / (2 * blur), 0, 1);
                                Blend(ref row[x], 0, 0, 0, ShadowAlpha * opacity * (1 - k * k * (3 - 2 * k)));
                            }
                        }
                        double d = shape.Distance(px, py);
                        if (d < 0.5) Blend(ref row[x], color.R, color.G, color.B, opacity * Math.Min(1, 0.5 - d));
                    }
                }
            });
        }
    }

    /// <summary>色を不透明度 alpha で重ねる（下が透けている画像でも正しく重なるように）</summary>
    private static void Blend(ref Rgba32 px, byte r, byte g, byte b, double alpha)
    {
        if (alpha <= 0) return;
        double da = px.A / 255.0 * (1 - alpha), outA = alpha + da;
        px = new Rgba32(
            (byte)Math.Round((r * alpha + px.R * da) / outA),
            (byte)Math.Round((g * alpha + px.G * da) / outA),
            (byte)Math.Round((b * alpha + px.B * da) / outA),
            (byte)Math.Round(outA * 255));
    }

    /// <summary>保存先: 出力フォルダ\元の名前_mark.拡張子（書き出せない形式は .jpg）。既にあれば (2)… を付ける</summary>
    public static string OutputPathFor(string source, string outputFolder) =>
        ImageSaver.UniquePath(Path.Combine(outputFolder,
            Path.GetFileNameWithoutExtension(source) + "_mark" + ImageSaver.ExtensionFor(OutputFormat.Keep, Path.GetExtension(source))));

    /// <summary>src から読み込み済みの画像に枠・矢印を描いて保存する（image 自体は変えない。dst が src なら上書き）</summary>
    /// <returns>実際に保存したパス</returns>
    public static string SaveAnnotated(Image<Rgba32> image, IReadOnlyList<Annotation> items, string src, string dst)
    {
        if (Cropper.IsSameFile(src, dst))
        {
            Cropper.EnsureOverwritable(src);
            Cropper.EnsureKeepsMetadata(image);
        }
        using var drawn = image.Clone();
        Draw(drawn, items);
        return Cropper.SaveEdited(drawn, src, dst);
    }
}
