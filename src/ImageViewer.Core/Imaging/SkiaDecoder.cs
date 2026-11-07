// Skia（SKCodec）での画像の読み込み: JPEG・PNG・GIF・WebP・BMP・ICO
// - 中身で形式を決める（拡張子は見ない）。Skia の形式でなければ TryOpen が null を返し、呼び出し側が ImageSharp・WIC で読む
// - 埋め込みの色プロファイル（Display P3 など）は sRGB に変換して読む（#81 の決定。プロファイルが無ければ sRGB とみなす）
// - EXIF の向きは、画素を直接並べ替えて直す（キャンバスに描くより速い）
// - 返す画像は RGBA・乗算しない（ImageSharp の Rgba32 と同じ並び）。縮小と重ね合わせは乗算済みで行う
using SkiaSharp;

namespace ImageViewer.Core.Imaging;

internal static class SkiaDecoder
{
    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();

    /// <summary>縮小に使う補間（線形 + ミップマップ。#81 の試作で ImageSharp の Lanczos3 との差が最も小さかった）</summary>
    internal static readonly SKSamplingOptions Downscale = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>
    /// ファイルを Skia で開く（ヘッダーだけ読む）。Skia の形式でなければ null。
    /// 返した SKCodec がファイルを開いたまま持つので、使い終わったら Dispose する
    /// </summary>
    public static SKCodec? TryOpen(string path)
    {
        Stream stream = ImageSource.OpenRead(path);
        try
        {
            if (!stream.CanSeek)
            {
                // ZIP の中の画像など。Skia は形式を調べるときに先頭へ戻るので、メモリに読んでおく
                var memory = new MemoryStream();
                stream.CopyTo(memory);
                stream.Dispose();
                memory.Position = 0;
                stream = memory;
            }
            var codec = SKCodec.Create(new SKManagedStream(stream, disposeManagedStream: true));
            if (codec == null) stream.Dispose();
            return codec;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Skia で読める中身なら読む（回転補正済み・sRGB・RGBA・乗算しない。アニメーションは先頭のコマ）。Skia の形式でなければ null</summary>
    /// <param name="maxEdge">長辺の上限（これより大きければ縮めて読む）。null なら原寸</param>
    public static SKBitmap? TryDecode(string path, int? maxEdge)
    {
        using var codec = TryOpen(path);
        return codec == null ? null : Decode(codec, maxEdge);
    }

    /// <summary>先頭のコマを読む（回転補正済み・sRGB・RGBA・乗算しない）。読めなければ InvalidDataException</summary>
    public static SKBitmap Decode(SKCodec codec, int? maxEdge)
    {
        var origin = codec.EncodedOrigin;
        int width = codec.Info.Width, height = codec.Info.Height;
        // 上限は長辺で比べるので、回転の前の縦横で決めてよい
        var size = new SKSizeI(width, height);
        if (maxEdge is int edge && Math.Max(width, height) > edge) size = ScaledSize(codec, edge);

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, Srgb);
        var bitmap = new SKBitmap(info);
        try
        {
            bitmap.Erase(SKColors.Transparent);
            Check(codec.GetPixels(info, bitmap.GetPixels()), "画像");
            bitmap = Orient(bitmap, origin);
            if (maxEdge is int limit && Math.Max(bitmap.Width, bitmap.Height) > limit)
            {
                // JPEG の縮小デコードは 1/8 単位なので、上限を超えた分はここで合わせる
                double scale = (double)limit / Math.Max(bitmap.Width, bitmap.Height);
                var fitted = Resize(bitmap, RoundSize(bitmap.Width, scale), RoundSize(bitmap.Height, scale));
                bitmap.Dispose();
                bitmap = fitted;
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 縮小デコードできる大きさのうち、長辺が edge 以上で最も小さいもの（JPEG は 1/8〜8/8。ほかの形式は原寸しか返さない）
    /// </summary>
    private static SKSizeI ScaledSize(SKCodec codec, int edge)
    {
        var full = codec.Info.Size;
        for (int eighths = 1; eighths < 8; eighths++)
        {
            var size = codec.GetScaledDimensions(eighths / 8f);
            if (Math.Max(size.Width, size.Height) >= edge && size.Width <= full.Width && size.Height <= full.Height) return size;
        }
        return full;
    }

    private static int RoundSize(int length, double scale) => Math.Max(1, (int)Math.Round(length * scale));

    /// <summary>読めた所までなら受け入れる（途中で切れたファイルは、読めた所までの絵を返す。ImageSharp も同じだった）</summary>
    internal static void Check(SKCodecResult result, string what)
    {
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput or SKCodecResult.ErrorInInput) return;
        throw new InvalidDataException($"{what}を読めません（{result}）");
    }

    /// <summary>EXIF の向き 5〜8 は 90° / 270° の回転を含む（縦横が入れ替わる）</summary>
    internal static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// EXIF の向きを直した画像を返す（元の bitmap は捨てる。向きが「そのまま」なら bitmap をそのまま返す）。
    /// 1 画素 4 バイトの画像だけ
    /// </summary>
    internal static unsafe SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return bitmap;
        if (bitmap.BytesPerPixel != 4) throw new ArgumentException($"1 画素 4 バイトの画像だけ回せます（{bitmap.ColorType}）", nameof(bitmap));
        int w = bitmap.Width, h = bitmap.Height;
        bool swap = SwapsAxes(origin);
        var result = new SKBitmap(new SKImageInfo(swap ? h : w, swap ? w : h, bitmap.ColorType, bitmap.AlphaType, bitmap.ColorSpace));
        try
        {
            uint* s = (uint*)bitmap.GetPixels(), d = (uint*)result.GetPixels();
            int sStride = bitmap.RowBytes / 4, dStride = result.RowBytes / 4;
            Parallel.For(0, h, y =>
            {
                uint* row = s + (long)y * sStride;
                for (int x = 0; x < w; x++)
                {
                    (int dx, int dy) = origin switch
                    {
                        SKEncodedOrigin.TopRight => (w - 1 - x, y),
                        SKEncodedOrigin.BottomRight => (w - 1 - x, h - 1 - y),
                        SKEncodedOrigin.BottomLeft => (x, h - 1 - y),
                        SKEncodedOrigin.LeftTop => (y, x),
                        SKEncodedOrigin.RightTop => (h - 1 - y, x),
                        SKEncodedOrigin.RightBottom => (h - 1 - y, w - 1 - x),
                        _ => (y, w - 1 - x), // LeftBottom
                    };
                    d[(long)dy * dStride + dx] = row[x];
                }
            });
        }
        catch
        {
            result.Dispose();
            throw;
        }
        bitmap.Dispose();
        return result;
    }

    /// <summary>
    /// width × height に縮めた（広げた）写しを、元と同じ画素の形式で返す。
    /// 乗算済みにしてから補間する（半透明の縁に、透明な画素の色がにじまないように）
    /// </summary>
    internal static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        using var premul = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, source.ColorSpace));
        using (var image = SKImage.FromBitmap(source))
        using (var canvas = new SKCanvas(premul))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(image, new SKRect(0, 0, width, height), Downscale, paint);
        }
        return Convert(premul, source.ColorType, source.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Unpremul : source.AlphaType);
    }

    /// <summary>画素の形式を変えた写し（色空間はそのまま）</summary>
    internal static SKBitmap Convert(SKBitmap source, SKColorType colorType, SKAlphaType alphaType)
    {
        var result = new SKBitmap(new SKImageInfo(source.Width, source.Height, colorType, alphaType, source.ColorSpace));
        try
        {
            using var from = source.PeekPixels();
            using var to = result.PeekPixels();
            if (!from.ReadPixels(to)) throw new InvalidOperationException($"画素を変換できません（{source.ColorType} / {source.AlphaType} → {colorType} / {alphaType}）");
        }
        catch
        {
            result.Dispose();
            throw;
        }
        return result;
    }

    /// <summary>Skia が読んだ形式の名前（詳細パネルに出す）</summary>
    public static string FormatName(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => "JPEG",
        SKEncodedImageFormat.Png => "PNG",
        SKEncodedImageFormat.Gif => "GIF",
        SKEncodedImageFormat.Webp => "WEBP",
        SKEncodedImageFormat.Bmp => "BMP",
        SKEncodedImageFormat.Ico => "ICO",
        SKEncodedImageFormat.Wbmp => "WBMP",
        var other => other.ToString().ToUpperInvariant(),
    };

    // ---- アニメーション（GIF・WebP） ----

    /// <summary>
    /// 先頭から last 番目までのコマを、重ね合わせ済みの絵として読む（原寸・乗算済み・sRGB。向きは直さない）。
    /// どのコマを下に敷くか（前のコマ・背景に戻す・前の状態に戻す）は Skia が決める（RequiredFrame）。
    /// 下に敷いたコマに、そのコマの差分を描き足す
    /// </summary>
    public static List<SKBitmap> DecodeFrames(SKCodec codec, int last, CancellationToken ct = default)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul, Srgb);
        var frameInfos = codec.FrameInfo;
        var frames = new List<SKBitmap>(last + 1);
        try
        {
            for (int i = 0; i <= last; i++)
            {
                ct.ThrowIfCancellationRequested();
                var bitmap = new SKBitmap(info);
                frames.Add(bitmap);
                int required = i < frameInfos.Length ? frameInfos[i].RequiredFrame : -1;
                if (required >= 0 && required < i) frames[required].GetPixelSpan().CopyTo(bitmap.GetPixelSpan());
                else
                {
                    bitmap.Erase(SKColors.Transparent);
                    required = -1;
                }
                Check(codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(i, required)), $"{i + 1} コマ目");
            }
        }
        catch
        {
            foreach (var f in frames) f.Dispose();
            throw;
        }
        return frames;
    }
}
