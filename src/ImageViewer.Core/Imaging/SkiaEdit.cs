// 編集の処理で共通に使う、SKBitmap の画素の扱い（#81 手順7: ImageSharp の処理から移したもの）
// - 画素を直接読み書きする: RGBA・乗算しない画像の行を Rgba の並びとして見る（ImageSharp の Rgba32 と同じ並び）
// - 縮小・切り抜き・回転・ぼかし・モザイク・背景色で塗る
// 画素ごとの処理（補正・枠や文字・塗りつぶし）は、ImageSharp のときと同じ計算のまま、この行の上で行う
using System.Runtime.InteropServices;
using SkiaSharp;

namespace ImageViewer.Core.Imaging;

/// <summary>1 画素（RGBA・乗算しない。メモリ上の並びは R, G, B, A）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Rgba(byte r, byte g, byte b, byte a = 255)
{
    public byte R = r, G = g, B = b, A = a;
}

/// <summary>縮小・拡大の補間のしかた</summary>
internal enum ResizeQuality
{
    /// <summary>線形 + ミップマップ（速い。表示用・プレビュー用。強い縮小でもちらつかない）</summary>
    Fast,
    /// <summary>Mitchell（やわらかい 3 次補間）</summary>
    Smooth,
    /// <summary>Catmull-Rom（くっきりした 3 次補間。ImageSharp の Lanczos3 の代わり）</summary>
    Sharp,
}

internal static class SkiaEdit
{
    /// <summary>RGBA・乗算しない画像か（行を Rgba の並びとして読み書きできるか）</summary>
    public static bool IsRgba(SKBitmap bitmap) =>
        bitmap.ColorType == SKColorType.Rgba8888 && bitmap.AlphaType == SKAlphaType.Unpremul;

    /// <summary>y 行目の画素（IsRgba の画像だけ）</summary>
    public static Span<Rgba> Row(this SKBitmap bitmap, int y) =>
        MemoryMarshal.Cast<byte, Rgba>(bitmap.GetPixelSpan().Slice(y * bitmap.RowBytes, bitmap.Width * 4));

    /// <summary>
    /// 画素を RGBA・乗算しない形で edit に渡して変える（その場で変える）。
    /// 違う形の画像は、写して変えてから書き戻す
    /// </summary>
    public static void Edit(SKBitmap bitmap, Action<SKBitmap> edit)
    {
        if (IsRgba(bitmap))
        {
            edit(bitmap);
            return;
        }
        using var rgba = ToRgba(bitmap);
        edit(rgba);
        using var from = rgba.PeekPixels();
        using var to = bitmap.PeekPixels();
        if (!from.ReadPixels(to)) throw new InvalidOperationException($"画素を書き戻せません（{bitmap.ColorType} / {bitmap.AlphaType}）");
    }

    /// <summary>RGBA・乗算しない写し（色空間はそのまま）</summary>
    public static SKBitmap ToRgba(SKBitmap bitmap) => SkiaDecoder.Convert(bitmap, SKColorType.Rgba8888, SKAlphaType.Unpremul);

    /// <summary>
    /// width × height に縮めた（広げた）写し（RGBA・乗算しない）。乗算済みにしてから補間する（半透明の縁に、透明な画素の色がにじまないように）。
    /// 3 次補間で半分より小さくするときは、先に線形 + ミップマップで 2 倍の大きさまで縮める（3 次補間は縮める率に合わせて広がらないので、
    /// そのままだと細かい模様がちらつく）
    /// </summary>
    public static SKBitmap Resize(SKBitmap source, int width, int height, ResizeQuality quality)
    {
        if (quality != ResizeQuality.Fast && (source.Width > width * 2 || source.Height > height * 2))
        {
            using var halfway = Draw(source, Math.Min(source.Width, width * 2), Math.Min(source.Height, height * 2), SkiaDecoder.Downscale);
            using var halfwayRgba = ToRgba(halfway);
            using var final = Draw(halfwayRgba, width, height, SamplingOf(quality));
            return ToRgba(final);
        }
        using var drawn = Draw(source, width, height, SamplingOf(quality));
        return ToRgba(drawn);
    }

    private static SKSamplingOptions SamplingOf(ResizeQuality quality) => quality switch
    {
        ResizeQuality.Smooth => new SKSamplingOptions(SKCubicResampler.Mitchell),
        ResizeQuality.Sharp => new SKSamplingOptions(SKCubicResampler.CatmullRom),
        _ => SkiaDecoder.Downscale,
    };

    /// <summary>source を width × height の乗算済みの画像に描く</summary>
    private static SKBitmap Draw(SKBitmap source, int width, int height, SKSamplingOptions sampling)
    {
        var premul = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, source.ColorSpace));
        try
        {
            using var image = SKImage.FromBitmap(source);
            using var canvas = new SKCanvas(premul);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(image, new SKRect(0, 0, width, height), sampling, paint);
        }
        catch
        {
            premul.Dispose();
            throw;
        }
        return premul;
    }

    /// <summary>box の範囲の写し（RGBA・乗算しない。box は画像の中にあること）</summary>
    public static SKBitmap Crop(SKBitmap source, SKRectI box)
    {
        using var subset = new SKBitmap();
        if (box.IsEmpty || !source.ExtractSubset(subset, box))
            throw new ArgumentException($"切り抜く範囲が画像の外です（{box}、画像 {source.Width}×{source.Height}）", nameof(box));
        return ToRgba(subset);
    }

    /// <summary>右に 90°・左に 90°・180° 回した写し（画素を並べ替えるだけなので値は変わらない）</summary>
    public static SKBitmap Rotate(SKBitmap source, int clockwiseDegrees)
    {
        // 「この向きで保存されている画像を正立させる」回し方を使う: 右に 90° = 向き 6、左に 90° = 向き 8、180° = 向き 3
        int degrees = ((clockwiseDegrees % 360) + 360) % 360;
        var origin = degrees switch
        {
            90 => SKEncodedOrigin.RightTop,
            270 => SKEncodedOrigin.LeftBottom,
            180 => SKEncodedOrigin.BottomRight,
            _ => SKEncodedOrigin.TopLeft,
        };
        return SkiaDecoder.Orient(ToRgba(source), origin);
    }

    /// <summary>透明な所を color で塗る（その場で変える。color の上に重ねた見え方にする）</summary>
    public static void Flatten(SKBitmap image, SKColor color)
    {
        Edit(image, rgba =>
        {
            for (int y = 0; y < rgba.Height; y++)
                foreach (ref var p in rgba.Row(y))
                {
                    if (p.A == 255) continue;
                    // ImageSharp の BackgroundColor と同じ: 下の色に、画素の色を不透明度で重ねる
                    double a = p.A / 255.0, ba = color.Alpha / 255.0 * (1 - a), outA = a + ba;
                    if (outA <= 0)
                    {
                        p = new Rgba(0, 0, 0, 0);
                        continue;
                    }
                    p = new Rgba(
                        (byte)Math.Round((p.R * a + color.Red * ba) / outA),
                        (byte)Math.Round((p.G * a + color.Green * ba) / outA),
                        (byte)Math.Round((p.B * a + color.Blue * ba) / outA),
                        (byte)Math.Round(outA * 255));
                }
        });
    }

    // ---- モザイク・ぼかし（画像全体にかける。範囲だけにかけるときは、切り出してからかける） ----

    /// <summary>
    /// モザイク（その場で変える）: 左上から cell px ごとのマスに分け、マスの中心の画素の色で塗る
    /// （ImageSharp の Pixelate と同じ決め方。中心が画像の外なら、画像の端の画素）
    /// </summary>
    public static void Pixelate(SKBitmap image, int cell)
    {
        if (cell < 2) return;
        Edit(image, rgba =>
        {
            int w = rgba.Width, h = rgba.Height, offset = cell / 2;
            for (int y = 0; y < h; y += cell)
            {
                int cy = Math.Min(y + offset, h - 1);
                for (int x = 0; x < w; x += cell)
                {
                    var color = rgba.Row(cy)[Math.Min(x + offset, w - 1)];
                    for (int yy = y; yy < Math.Min(y + cell, h); yy++)
                        rgba.Row(yy).Slice(x, Math.Min(cell, w - x)).Fill(color);
                }
            }
        });
    }

    /// <summary>ガウスぼかし（その場で変える）。画像の外は、端の画素が続いているものとして扱う</summary>
    public static void GaussianBlur(SKBitmap image, float sigma)
    {
        if (sigma <= 0) return;
        Edit(image, rgba =>
        {
            using var blurred = new SKBitmap(new SKImageInfo(rgba.Width, rgba.Height, SKColorType.Rgba8888, SKAlphaType.Premul, rgba.ColorSpace));
            using (var source = SKImage.FromBitmap(rgba))
            using (var canvas = new SKCanvas(blurred))
            using (var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp))
            using (var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src })
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawImage(source, 0, 0, SKSamplingOptions.Default, paint);
            }
            using var from = blurred.PeekPixels();
            using var to = rgba.PeekPixels();
            if (!from.ReadPixels(to)) throw new InvalidOperationException("ぼかした画素を書き戻せません");
        });
    }

    /// <summary>
    /// ボックスぼかし（その場で変える）: 横・縦それぞれ前後 radius 画素を同じ重みで平均する（ImageSharp の BoxBlur と同じ）。
    /// 乗算済みの値で平均し（透明な画素の色がにじまないように）、画像の外は端の画素が続いているものとして扱う
    /// </summary>
    public static void BoxBlur(SKBitmap image, int radius)
    {
        if (radius < 1) return;
        Edit(image, rgba =>
        {
            int w = rgba.Width, h = rgba.Height;
            var values = new float[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                var row = rgba.Row(y);
                for (int x = 0; x < w; x++)
                {
                    var p = row[x];
                    float a = p.A / 255f;
                    int i = (y * w + x) * 4;
                    values[i] = p.R * a;
                    values[i + 1] = p.G * a;
                    values[i + 2] = p.B * a;
                    values[i + 3] = p.A;
                }
            }
            var line = new float[Math.Max(w, h) * 4];
            // 横
            for (int y = 0; y < h; y++) Average(values, line, (y * w) * 4, 4, w, radius);
            // 縦
            for (int x = 0; x < w; x++) Average(values, line, x * 4, w * 4, h, radius);
            for (int y = 0; y < h; y++)
            {
                var row = rgba.Row(y);
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    float alpha = values[i + 3];
                    if (alpha <= 0)
                    {
                        row[x] = new Rgba(0, 0, 0, 0);
                        continue;
                    }
                    float k = 255f / alpha; // 乗算済みの値を戻す
                    row[x] = new Rgba(ToByte(values[i] * k), ToByte(values[i + 1] * k), ToByte(values[i + 2] * k), ToByte(alpha));
                }
            }
        });
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>start から step おきに並ぶ count 個の画素（4 つの値）を、前後 radius の平均に置き換える（外は端の値）</summary>
    private static void Average(float[] values, float[] line, int start, int step, int count, int radius)
    {
        for (int i = 0; i < count; i++)
            for (int c = 0; c < 4; c++) line[i * 4 + c] = values[start + i * step + c];
        float size = 2 * radius + 1;
        Span<float> sum = stackalloc float[4];
        sum.Clear();
        for (int j = -radius; j <= radius; j++)
        {
            int at = Math.Clamp(j, 0, count - 1) * 4;
            for (int c = 0; c < 4; c++) sum[c] += line[at + c];
        }
        for (int i = 0; i < count; i++)
        {
            for (int c = 0; c < 4; c++) values[start + i * step + c] = sum[c] / size;
            int enter = Math.Min(i + radius + 1, count - 1) * 4, leave = Math.Max(i - radius, 0) * 4;
            for (int c = 0; c < 4; c++) sum[c] += line[enter + c] - line[leave + c];
        }
    }
}
