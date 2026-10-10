// 枠・矢印・文字の「文字」の部分。文字の形（輪郭）を GDI+ で作って塗る。
// 輪郭は大きさに比例するので、表示用の縮小した画像にも同じ形で描ける（見た目と保存したものが合う）
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageViewer.Core.Editing;

internal static class AnnotationText
{
    /// <summary>大きさを測るときの文字の大きさ（px）。測った値はこれで割って、文字の大きさ 1px あたりにして覚える</summary>
    private const float UnitEm = 100;
    private const int MaxCached = 200;

    // GDI+ の部品は同時に使えないので、表示（UI）と保存（別のスレッド）が重ならないようにする
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (double Width, double Height, double InkCenter)> Sizes = new();
    private static FontFamily? _family;
    private static FontStyle _style;

    /// <summary>説明用の画像で読みやすい、太めのゴシック体（無ければ、ある物で）</summary>
    private static FontFamily Family
    {
        get
        {
            if (_family != null) return _family;
            foreach (string name in new[] { "Yu Gothic UI", "Meiryo UI", "Meiryo", "Segoe UI" })
            {
                try
                {
                    _family = new FontFamily(name);
                    break;
                }
                catch (ArgumentException)
                {
                    // その PC に無い書体
                }
            }
            _family ??= FontFamily.GenericSansSerif;
            _style = _family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
            return _family;
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>文字の輪郭。1 行目の上端が y = 0、各行は x = 0 を中心にそろえる</summary>
    private static GraphicsPath BuildPath(string text, float emSize)
    {
        var path = new GraphicsPath(FillMode.Winding);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.Alignment = StringAlignment.Center;
        format.FormatFlags |= StringFormatFlags.NoWrap;
        path.AddString(text, Family, (int)_style, emSize, new PointF(0, 0), format);
        return path;
    }

    private static (double Width, double Height, double InkCenter) Measure(string text)
    {
        if (Sizes.TryGetValue(text, out var size)) return size;
        var family = Family;
        double lineHeight = (double)family.GetLineSpacing(_style) / family.GetEmHeight(_style);
        int lines = text.Count(ch => ch == '\n') + 1;
        double width = 1, center = 0; // 空（空白だけ）でも、つかめるように 1 文字分の幅を持たせる
        using (var path = BuildPath(text, UnitEm))
        {
            if (path.PointCount > 0)
            {
                var bounds = path.GetBounds();
                width = Math.Max(bounds.Width / UnitEm, 0.3);
                center = (bounds.Left + bounds.Right) / 2 / UnitEm;
            }
        }
        if (Sizes.Count >= MaxCached) Sizes.Clear();
        return Sizes[text] = (width, lines * lineHeight, center);
    }

    /// <summary>文字の大きさ 1px あたりの、文字全体の幅と高さ（幅は見えている形の幅、高さは行の高さ × 行数）</summary>
    public static (double Width, double Height) UnitSize(string text)
    {
        lock (Gate)
        {
            var (width, height, _) = Measure(Normalize(text));
            return (width, height);
        }
    }

    /// <summary>(centerX, centerY) を中心に文字を描く。shadow があれば、右下へずらしてぼかした影を先に落とす</summary>
    public static void Render(SixLabors.ImageSharp.Image<Rgba32> image, string text, double fontSize, double centerX, double centerY,
        Rgba32 color, (double Offset, double Blur)? shadow, double shadowAlpha)
    {
        text = Normalize(text);
        if (string.IsNullOrWhiteSpace(text) || fontSize < 1 || color.A == 0) return;
        int shift = shadow is { } s ? Math.Max(1, (int)Math.Round(s.Offset)) : 0;
        int blur = shadow is { } b ? Math.Max(1, (int)Math.Round(b.Blur / 2)) : 0;
        int left, top, width, height;
        byte[] mask;
        lock (Gate)
        {
            var (_, unitHeight, inkCenter) = Measure(text);
            using var path = BuildPath(text, (float)fontSize);
            using (var move = new Matrix())
            {
                move.Translate((float)(centerX - inkCenter * fontSize), (float)(centerY - unitHeight * fontSize / 2));
                path.Transform(move);
            }
            if (path.PointCount == 0) return;
            var bounds = path.GetBounds();
            int pad = 2 + shift + blur * 2; // 影がはみ出す分
            left = Math.Max(0, (int)Math.Floor(bounds.Left) - pad);
            top = Math.Max(0, (int)Math.Floor(bounds.Top) - pad);
            width = Math.Min(image.Width, (int)Math.Ceiling(bounds.Right) + pad) - left;
            height = Math.Min(image.Height, (int)Math.Ceiling(bounds.Bottom) + pad) - top;
            if (width <= 0 || height <= 0) return;

            // 文字の形を白で塗って、不透明度を「その画素をどれだけ覆うか」として取り出す
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half; // 画素 (x, y) が x〜x+1 を覆う（ほかの形の描き方と合わせる）
                g.TranslateTransform(-left, -top);
                g.FillPath(Brushes.White, path);
            }
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                mask = new byte[width * height];
                var row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (int x = 0; x < width; x++) mask[y * width + x] = row[x * 4 + 3];
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        float[]? soft = null;
        if (shadow != null)
        {
            soft = new float[mask.Length];
            for (int i = 0; i < mask.Length; i++) soft[i] = mask[i] / 255f;
            BoxBlur(soft, width, height, blur);
            BoxBlur(soft, width, height, blur);
        }
        double opacity = color.A / 255.0;
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = rows.GetRowSpan(top + y);
                for (int x = 0; x < width; x++)
                {
                    if (soft != null && x >= shift && y >= shift)
                    {
                        // 細い線の影はぼかすと薄くなりすぎるので、少し濃くする
                        double v = Math.Min(1, soft[(y - shift) * width + x - shift] * 1.6);
                        Annotator.Blend(ref row[left + x], 0, 0, 0, shadowAlpha * opacity * v);
                    }
                    byte m = mask[y * width + x];
                    if (m > 0) Annotator.Blend(ref row[left + x], color.R, color.G, color.B, opacity * m / 255.0);
                }
            }
        });
    }

    /// <summary>横・縦に前後 radius 画素の平均を取る（2 回かけると、なだらかなぼかしに近くなる）</summary>
    private static void BoxBlur(float[] values, int width, int height, int radius)
    {
        var line = new float[Math.Max(width, height)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) line[x] = values[y * width + x];
            Average(line, width, radius, (x, v) => values[y * width + x] = v);
        }
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++) line[y] = values[y * width + x];
            Average(line, height, radius, (y, v) => values[y * width + x] = v);
        }
    }

    private static void Average(float[] line, int count, int radius, Action<int, float> set)
    {
        float sum = 0;
        for (int i = 0; i <= Math.Min(radius, count - 1); i++) sum += line[i];
        for (int i = 0; i < count; i++)
        {
            set(i, sum / (2 * radius + 1));
            int enter = i + radius + 1, leave = i - radius;
            if (enter < count) sum += line[enter];
            if (leave >= 0) sum -= line[leave];
        }
    }
}
