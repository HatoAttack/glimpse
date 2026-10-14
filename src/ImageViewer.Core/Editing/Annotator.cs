// 枠・矢印・文字。画像の特定の所を指し示すために、四角の枠（角の丸み・太さ）と矢印（太さ・先端の大きさ）、
// 文字（そのまま / 帯 / 吹き出し）と番号（丸の中の数字）を描く。影も付けられる。
// 形は「点から形の縁までの距離」で表して画素ごとに塗る（縁は距離でなめらかに、影は同じ形をずらしてぼかす）。
// 表示用の縮小した画像にも同じ処理で描けるので、見た目と保存したものが合う。文字の形は AnnotationText
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageViewer.Core.Editing;

public enum AnnotationKind { Frame, Arrow, Text, Number }

/// <summary>文字を本体の 左 / 中央 / 右 のどこに寄せるか（複数行なら行も同じ側にそろえる。縦はいつも中央）</summary>
public enum TextLineAlign { Center, Left, Right }

/// <summary>文字の後ろ: 何も置かない / 角丸の四角（帯）/ 帯にしっぽを付けた吹き出し</summary>
public enum TextBackground { None, Box, Balloon }

/// <summary>
/// 枠か矢印 1 つ（画像座標）。枠は (X0, Y0)〜(X1, Y1) が線の中心を通る四角、矢印は (X0, Y0) から (X1, Y1) へ向かう（先端が X1, Y1）。
/// Thickness は線の太さ（px）、CornerRadius は枠の角の丸み（px。短い辺の半分まで）、HeadSize は矢印の先端の大きさ（太さの何倍か）。
/// 文字と番号は (X0, Y0) が本体（文字を囲む四角・番号の丸）の左上。番号の大きさは数字と FontSize から決まる。
/// 文字の本体は BoxWidth × BoxHeight（文字が収まる大きさより小さくはならない。0 なら文字にぴったり）。
/// 帯・吹き出しの縁は本体の外側の端から内へ Thickness の太さで、文字はその内側に置く（縁を太くしても文字に被らない）。
/// 吹き出しは (X1, Y1) がしっぽの先。帯と吹き出しは Color が線、Fill が中の色、TextColor が文字の色。番号は Color が丸の色
/// </summary>
public sealed record Annotation(AnnotationKind Kind, double X0, double Y0, double X1, double Y1,
    Rgba32 Color, double Thickness, double CornerRadius, double HeadSize, bool Shadow)
{
    public double Width => Math.Abs(X1 - X0);
    public double Height => Math.Abs(Y1 - Y0);
    public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));

    /// <summary>枠の傾き（度。時計回りが正）。(X0, Y0)〜(X1, Y1) は傾ける前の四角で、その中心を軸に回す。枠のほかは使わない</summary>
    public double Angle { get; init; }

    /// <summary>
    /// 枠の左右の辺の倒れ（度。-MaxSkew〜MaxSkew。正なら上の辺が右へずれる平行四辺形）。上下の辺は横のまま、幅（Width）と高さ（Height）も変えない。
    /// 線の太さはどの辺も同じ。倒してから Angle だけ回す。枠のほかは使わない
    /// </summary>
    public double Skew { get; init; }

    /// <summary>左右の辺の倒れの tan（縦に 1 進むと横にどれだけずれるか）</summary>
    public double Shear => Kind == AnnotationKind.Frame ? Math.Tan(Math.Clamp(Skew, -Annotator.MaxSkew, Annotator.MaxSkew) * Math.PI / 180) : 0;

    /// <summary>傾けた枠か</summary>
    public bool Rotated => Kind == AnnotationKind.Frame && Angle % 360 != 0;

    /// <summary>枠の中心</summary>
    public (double X, double Y) Center => ((X0 + X1) / 2, (Y0 + Y1) / 2);

    /// <summary>傾ける前の枠での位置（中心からの横 u・縦 v）を、画像の位置にする</summary>
    public (double X, double Y) FromLocal(double u, double v)
    {
        double rad = Angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        var (cx, cy) = Center;
        return (cx + u * cos - v * sin, cy + u * sin + v * cos);
    }

    /// <summary>枠の四隅（倒して傾けた後。左上・右上・右下・左下の順）</summary>
    public (double X, double Y)[] Corners
    {
        get
        {
            double hx = Width / 2, hy = Height / 2, s = Shear * hy; // 上の辺は右へ s、下の辺は左へ s ずれる
            return new[] { FromLocal(-hx + s, -hy), FromLocal(hx + s, -hy), FromLocal(hx - s, hy), FromLocal(-hx - s, hy) };
        }
    }

    /// <summary>文字（番号は数字）。改行で行を分ける</summary>
    public string Text { get; init; } = "";

    /// <summary>文字の大きさ（px）</summary>
    public double FontSize { get; init; }

    public TextBackground Background { get; init; }

    /// <summary>帯・吹き出しの中の色</summary>
    public Rgba32 Fill { get; init; } = new(255, 255, 255);

    /// <summary>文字の色（指定しなければ Color と同じ）。番号の数字には使わない（丸の色に合わせて白か黒）</summary>
    public Rgba32? TextColor { get; init; }

    /// <summary>書体の名前（空なら初めの書体。その PC に無い書体も初めの書体で描く）</summary>
    public string FontName { get; init; } = "";

    /// <summary>文字を本体のどちらに寄せるか</summary>
    public TextLineAlign Align { get; init; }

    /// <summary>文字の本体の大きさ（px）。文字が収まる大きさより小さければ、収まる大きさになる</summary>
    public double BoxWidth { get; init; }
    public double BoxHeight { get; init; }

    /// <summary>帯・吹き出しの縁から文字までの余白（左右）。文字の大きさに比例させる</summary>
    public double PaddingX => Kind == AnnotationKind.Text && Background != TextBackground.None ? FontSize * 0.5 : 0;

    /// <summary>帯・吹き出しの縁の太さ（縁が無いものは 0）</summary>
    public double BorderWidth => Kind == AnnotationKind.Text && Background != TextBackground.None ? Math.Max(0, Thickness) : 0;

    /// <summary>文字だけの大きさ（余白を含めない）</summary>
    public (double Width, double Height) TextSize
    {
        get
        {
            var (w, h) = AnnotationText.UnitSize(Text, FontName, Align);
            return (w * FontSize, h * FontSize);
        }
    }

    /// <summary>文字が収まる本体の大きさ（これより小さくはできない）</summary>
    public (double Width, double Height) MinBody
    {
        get
        {
            var (cw, ch) = TextSize;
            // 縁の分も足す（縁を太くすると、文字に被らずに本体が広がる）
            return (cw + (PaddingX + BorderWidth) * 2, ch + (PaddingX > 0 ? FontSize * 0.3 : 0) + BorderWidth * 2);
        }
    }

    public bool IsText => Kind is AnnotationKind.Text or AnnotationKind.Number;

    /// <summary>しっぽがあるか（先が本体の中にあるときは描かれない）</summary>
    public bool HasTail => Kind == AnnotationKind.Text && Background == TextBackground.Balloon;

    /// <summary>文字・番号の本体（縁も含めた外側の四角。番号は丸を囲む正方形）</summary>
    public (double X, double Y, double Width, double Height) Body
    {
        get
        {
            if (Kind == AnnotationKind.Number)
            {
                var (cw, ch) = TextSize;
                double d = Math.Max(ch, cw + FontSize * 0.6); // 桁が増えたら丸を大きくする
                return (X0, Y0, d, d);
            }
            var (minWidth, minHeight) = MinBody;
            return (X0, Y0, Math.Max(BoxWidth, minWidth), Math.Max(BoxHeight, minHeight));
        }
    }

    /// <summary>形が収まる四角（線の太さは含めない。倒した・傾けた枠は四隅が収まる四角）</summary>
    public (double Left, double Top, double Right, double Bottom) Bounds
    {
        get
        {
            if (Rotated || Shear != 0)
            {
                var c = Corners;
                return (c.Min(p => p.X), c.Min(p => p.Y), c.Max(p => p.X), c.Max(p => p.Y));
            }
            if (!IsText) return (Math.Min(X0, X1), Math.Min(Y0, Y1), Math.Max(X0, X1), Math.Max(Y0, Y1));
            var (x, y, w, h) = Body;
            return HasTail ? (Math.Min(x, X1), Math.Min(y, Y1), Math.Max(x + w, X1), Math.Max(y + h, Y1)) : (x, y, x + w, y + h);
        }
    }

    /// <summary>位置も太さも同じ倍率で拡大・縮小する（表示用の縮小した画像に描くとき用）</summary>
    public Annotation Scale(double s) => this with
    {
        X0 = X0 * s, Y0 = Y0 * s, X1 = X1 * s, Y1 = Y1 * s, Thickness = Thickness * s, CornerRadius = CornerRadius * s, FontSize = FontSize * s,
        BoxWidth = BoxWidth * s, BoxHeight = BoxHeight * s,
    };

    /// <summary>文字・番号の本体だけを動かす（吹き出しのしっぽの先は、指している所に残す）</summary>
    public Annotation MoveBody(double dx, double dy) => this with { X0 = X0 + dx, Y0 = Y0 + dy };

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
    // 傾けた枠: 点を傾ける前の向きへ戻すための cos・sin。倒した枠: 左右の辺の倒れの tan（0 なら四角）
    private readonly double _cos, _sin, _k;
    // 矢印: 始点・向き（単位ベクトル）・軸（始点からの長さ）・軸の太さの半分・先端の三角（根元の位置と幅の半分）
    private readonly double _sx, _sy, _ux, _uy, _length, _shaftEnd, _half, _headBase, _headHalf;
    private readonly bool _shaft;
    // 文字・番号: 本体は枠の外側（_cx 〜 _or）を使う。吹き出しはしっぽの三角（根元の 2 点と先）を足す
    private readonly double _ax, _ay, _bx, _by, _tx, _ty;
    private readonly bool _tail;

    /// <summary>形の外枠からはみ出す長さ（線の太さ・先端の幅）</summary>
    public double Reach { get; }

    public AnnotationShape(Annotation a)
    {
        this = default;
        _kind = a.Kind;
        double t = Math.Max(0, a.Thickness);
        if (a.IsText)
        {
            var (x, y, w, h) = a.Body;
            double hx = w / 2, hy = h / 2;
            _cx = x + hx;
            _cy = y + hy;
            _ohx = hx;
            _ohy = hy;
            bool boxed = a.Kind == AnnotationKind.Text && a.Background != TextBackground.None;
            _or = a.Kind == AnnotationKind.Number ? hx : boxed ? Math.Clamp(a.CornerRadius, 0, Math.Min(hx, hy)) : 0;
            if (!a.HasTail || RoundedBox(a.X1 - _cx, a.Y1 - _cy, hx, hy, _or) <= 0) return;
            // しっぽ: 本体の中の「先にいちばん近い所」から先へ向かう三角。根元は本体に隠れるよう、縁から内側へ入れる
            // 太さは縁の分を足す（縁が太くても、しっぽの中の色が残るように）
            double half = Math.Min(a.FontSize * 0.35 + a.BorderWidth, Math.Min(hx, hy) * 0.6), inset = half + _or * 0.3;
            double rx = Math.Max(0, hx - inset), ry = Math.Max(0, hy - inset);
            double px = _cx + Math.Clamp(a.X1 - _cx, -rx, rx), py = _cy + Math.Clamp(a.Y1 - _cy, -ry, ry);
            double length = Math.Sqrt((a.X1 - px) * (a.X1 - px) + (a.Y1 - py) * (a.Y1 - py));
            if (length <= 0 || half <= 0) return;
            double nx = -(a.Y1 - py) / length * half, ny = (a.X1 - px) / length * half;
            (_ax, _ay, _bx, _by, _tx, _ty) = (px + nx, py + ny, px - nx, py - ny, a.X1, a.Y1);
            _tail = true;
        }
        else if (a.Kind == AnnotationKind.Frame)
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
            double rad = a.Angle * Math.PI / 180;
            (_cos, _sin) = (Math.Cos(rad), Math.Sin(rad));
            Reach = t / 2;
            _k = a.Shear;
            if (_k == 0) return;
            // 倒した枠: 辺を垂直に d だけ外へ出すと、上下の辺は d、左右の辺は横に d × stretch だけ広がる。
            // 角を丸めるときは、丸みの分だけ内へ縮めた平行四辺形から丸みの分だけ離れた所を縁にする（_ohx 〜 _ihy は縮めた後の半分の大きさ）
            double stretch = Math.Sqrt(1 + _k * _k);
            r = Math.Clamp(a.CornerRadius, 0, Math.Min(hy, hx / stretch));
            _or = r > 0 ? r + t / 2 : 0;
            _ohx = hx - (_or - t / 2) * stretch;
            _ohy = hy - (_or - t / 2);
            _ir = Math.Max(0, r - t / 2);
            _ihx = hx - (_ir + t / 2) * stretch;
            _ihy = hy - (_ir + t / 2);
            _hole = hx - t / 2 * stretch > 0 && hy - t / 2 > 0;
            // 尖った角は線の外側が角の二等分線の向きに伸びる（いちばん鋭い角 90° - 倒れ の半分で決まる）
            double sharp = (Math.PI / 2 - Math.Atan(Math.Abs(_k))) / 2;
            Reach = t / 2 / Math.Sin(sharp);
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
        if (_kind is AnnotationKind.Text or AnnotationKind.Number)
        {
            double body = RoundedBox(x - _cx, y - _cy, _ohx, _ohy, _or);
            return _tail ? Math.Min(body, Tail(x, y)) : body;
        }
        if (_kind == AnnotationKind.Frame)
        {
            double ox = x - _cx, oy = y - _cy;
            double px = ox * _cos + oy * _sin, py = -ox * _sin + oy * _cos; // 傾ける前の向きで測る
            if (_k != 0)
            {
                double edge = Parallelogram(px, py, _ohx, _ohy, _k) - _or;
                return _hole ? Math.Max(edge, -(Parallelogram(px, py, _ihx, _ihy, _k) - _ir)) : edge;
            }
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

    /// <summary>
    /// 中心が原点の平行四辺形までの距離（上下の辺は v = ±hy で横幅の半分が hx、上の辺が右へ k × hy ずれる）。
    /// 大きさ 0 の（点や線になった）ものでも距離は測れる
    /// </summary>
    private static double Parallelogram(double px, double py, double hx, double hy, double k)
    {
        double s = k * hy;
        double ax = -hx + s, ay = -hy, bx = hx + s, by = -hy, cx = hx - s, cy = hy, dx = -hx - s, dy = hy;
        double best = double.MaxValue;
        bool inside = false;
        Edge(ax, ay, bx, by);
        Edge(bx, by, cx, cy);
        Edge(cx, cy, dx, dy);
        Edge(dx, dy, ax, ay);
        double dist = Math.Sqrt(best);
        return inside ? -dist : dist;

        // 辺 (x0, y0)→(x1, y1) までの距離の 2 乗を best に、点から横へ伸ばした線が辺を横切るかを inside に入れる
        void Edge(double x0, double y0, double x1, double y1)
        {
            double ex = x1 - x0, ey = y1 - y0, wx = px - x0, wy = py - y0, len = ex * ex + ey * ey;
            double k2 = len > 0 ? Math.Clamp((wx * ex + wy * ey) / len, 0, 1) : 0;
            double qx = wx - ex * k2, qy = wy - ey * k2;
            best = Math.Min(best, qx * qx + qy * qy);
            if ((y0 > py) != (y1 > py) && px < x0 + (py - y0) * ex / ey) inside = !inside;
        }
    }

    /// <summary>しっぽの三角（根元の 2 点と先）までの距離</summary>
    private double Tail(double x, double y)
    {
        double e0x = _bx - _ax, e0y = _by - _ay, e1x = _tx - _bx, e1y = _ty - _by, e2x = _ax - _tx, e2y = _ay - _ty;
        double v0x = x - _ax, v0y = y - _ay, v1x = x - _bx, v1y = y - _by, v2x = x - _tx, v2y = y - _ty;
        double sign = Math.Sign(e0x * e2y - e0y * e2x);
        var (d0, s0) = Edge(v0x, v0y, e0x, e0y);
        var (d1, s1) = Edge(v1x, v1y, e1x, e1y);
        var (d2, s2) = Edge(v2x, v2y, e2x, e2y);
        double dist = Math.Sqrt(Math.Min(d0, Math.Min(d1, d2)));
        return Math.Min(sign * s0, Math.Min(sign * s1, sign * s2)) > 0 ? -dist : dist;

        // 辺（始点からの向き e）までの距離の 2 乗と、辺のどちら側か
        static (double Squared, double Side) Edge(double vx, double vy, double ex, double ey)
        {
            double k = Math.Clamp((vx * ex + vy * ey) / (ex * ex + ey * ey), 0, 1);
            double qx = vx - ex * k, qy = vy - ey * k;
            return (qx * qx + qy * qy, vx * ey - vy * ex);
        }
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

    /// <summary>枠の左右の辺を倒せるいちばん大きな角度（度）</summary>
    public const double MaxSkew = 75;

    /// <summary>影の濃さ（0〜1）</summary>
    private const double ShadowAlpha = 0.5;

    /// <summary>太さの初めの値（px）。画像の長い辺に対する割合で、大きい画像でも細すぎないように</summary>
    public static int DefaultThickness(int width, int height) => Math.Max(2, (int)Math.Round(Math.Max(width, height) / 200.0));

    /// <summary>その PC にある書体の名前（文字・番号の書体を選ぶ一覧用）</summary>
    public static IReadOnlyList<string> FontNames() => AnnotationText.FontNames();

    /// <summary>書体を指定しないときに使われる書体の名前</summary>
    public static string DefaultFontName => AnnotationText.DefaultFontName;

    /// <summary>文字の大きさの初めの値（px）。画像の長い辺に対する割合で、大きい画像でも小さすぎないように</summary>
    public static int DefaultFontSize(int width, int height) => Math.Max(14, (int)Math.Round(Math.Max(width, height) / 40.0));

    /// <summary>影のずれ（右下へ）とぼかしの幅（px）。線の太さに合わせる</summary>
    public static (double Offset, double Blur) ShadowOf(double thickness) => (Math.Max(1, thickness * 0.4), Math.Max(1.5, thickness * 0.6));

    /// <summary>image に枠・矢印・文字を順に描く（後のものほど上）</summary>
    public static void Draw(Image<Rgba32> image, IEnumerable<Annotation> items)
    {
        foreach (var a in items)
        {
            if (a.Color.A == 0) continue;
            if (a.IsText)
            {
                DrawText(image, a);
                continue;
            }
            if (a.Thickness <= 0) continue;
            if (a.Kind == AnnotationKind.Arrow && a.Length <= 0) continue;
            FillShape(image, a, a.Thickness, a.Color, null, 0);
        }
    }

    /// <summary>文字・番号: 帯・吹き出し・丸を塗ってから、その上に文字を描く</summary>
    private static void DrawText(Image<Rgba32> image, Annotation a)
    {
        if (a.FontSize < 1) return;
        var (x, y, w, h) = a.Body;
        bool number = a.Kind == AnnotationKind.Number, bare = !number && a.Background == TextBackground.None;
        if (number)
        {
            FillShape(image, a, a.FontSize / 8, a.Color, null, 0);
        }
        else if (!bare)
        {
            double border = Math.Max(0, a.Thickness);
            double basis = Math.Max(border, a.FontSize / 8);
            if (border > 0) FillShape(image, a, basis, a.Color, a.Fill, border);
            else FillShape(image, a, basis, a.Fill, null, 0);
        }
        // 帯や丸が無い文字は、文字の形そのものに影を落とす
        // 本体が文字より広ければ、左・右に寄せる（余白は空ける）
        double half = a.TextSize.Width / 2;
        double pad = a.PaddingX + a.BorderWidth;
        double centerX = number || a.Align == TextLineAlign.Center ? x + w / 2
            : a.Align == TextLineAlign.Left ? x + pad + half : x + w - pad - half;
        AnnotationText.Render(image, a.Text, a.FontName, a.Align, a.FontSize, centerX, y + h / 2, number ? ContrastOf(a.Color) : a.TextColor ?? a.Color,
            bare && a.Shadow ? ShadowOf(a.FontSize / 8) : null, ShadowAlpha);
    }

    /// <summary>色の上に載せて読める文字の色（明るい色には黒、暗い色には白）</summary>
    private static Rgba32 ContrastOf(Rgba32 c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.6 ? new Rgba32(24, 24, 24, c.A) : new Rgba32(255, 255, 255, c.A);

    /// <summary>
    /// 形を color で塗る（a.Shadow なら影も。影の大きさは shadowBasis から決める）。
    /// inner があれば、縁から inset だけ内側を inner で塗る（color が線、inner が中の色になる）
    /// </summary>
    private static void FillShape(Image<Rgba32> image, Annotation a, double shadowBasis, Rgba32 color, Rgba32? inner, double inset)
    {
        var shape = new AnnotationShape(a);
        bool shadow = a.Shadow;
        var (offset, blur) = shadow ? ShadowOf(shadowBasis) : (0, 0);
        double margin = shape.Reach + 1 + offset + blur;
        var (l, t, r, b) = a.Bounds;
        int left = Math.Max(0, (int)Math.Floor(l - margin)), right = Math.Min(image.Width, (int)Math.Ceiling(r + margin));
        int top = Math.Max(0, (int)Math.Floor(t - margin)), bottom = Math.Min(image.Height, (int)Math.Ceiling(b + margin));
        if (left >= right || top >= bottom) return;
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
                    if (d >= 0.5) continue;
                    Blend(ref row[x], color.R, color.G, color.B, opacity * Math.Min(1, 0.5 - d));
                    if (inner is { } fill && d + inset < 0.5) Blend(ref row[x], fill.R, fill.G, fill.B, fill.A / 255.0 * Math.Min(1, 0.5 - d - inset));
                }
            }
        });
    }

    /// <summary>色を不透明度 alpha で重ねる（下が透けている画像でも正しく重なるように）</summary>
    internal static void Blend(ref Rgba32 px, byte r, byte g, byte b, double alpha)
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

    /// <summary>src から読み込み済みの画像に枠・矢印・文字を描いて保存する（image 自体は変えない。dst が src なら上書き）</summary>
    /// <returns>実際に保存したパス</returns>
    public static string SaveAnnotated(Image<Rgba32> image, IReadOnlyList<Annotation> items, string src, string dst)
    {
        if (SourceGuard.IsSameFile(src, dst))
        {
            SourceGuard.EnsureReplaceable(src);
            SourceGuard.EnsureKeepsMetadata(image);
        }
        using var drawn = image.Clone();
        Draw(drawn, items);
        return SourceGuard.Save(drawn, src, dst);
    }
}
