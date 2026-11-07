// ImageSharp から SkiaSharp への移行中（#81）だけ使う、画像の受け渡しの変換。
// 画像は SKBitmap（RGBA・乗算しない。ImageSharp の Rgba32 と同じ並び）で受け渡し、まだ ImageSharp で処理する所とは、ここで変換する。
// 画素の値は変わらない。ImageSharp を外すとき（#81 手順8）に、このファイルごと消す
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;

namespace ImageViewer.Core.Imaging;

internal static class ImageSharpBridge
{
    /// <summary>受け渡しに使う画素の形式（RGBA・乗算しない）</summary>
    public static SKImageInfo Info(int width, int height) =>
        new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

    public static SKBitmap ToSkia(Image<Rgba32> image) => ToSkia(image.Frames.RootFrame);

    public static unsafe SKBitmap ToSkia(ImageFrame<Rgba32> frame)
    {
        var bitmap = new SKBitmap(Info(frame.Width, frame.Height));
        try
        {
            var pixels = new Span<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount);
            frame.CopyPixelDataTo(pixels);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        return bitmap;
    }

    /// <summary>
    /// SKBitmap を ImageSharp の画像にする（どの形式の SKBitmap でも、RGBA・乗算しない値に読み出す）。
    /// metadata があれば、その写しを付ける（保存するときに元のファイルのメタデータを書くため）
    /// </summary>
    public static Image<Rgba32> ToImageSharp(SKBitmap bitmap, ImageMetadata? metadata = null)
    {
        if (bitmap.ColorType == SKColorType.Rgba8888 && bitmap.AlphaType is SKAlphaType.Unpremul or SKAlphaType.Opaque
            && bitmap.RowBytes == bitmap.Width * 4)
            return FromRgba(bitmap.GetPixelSpan(), bitmap.Width, bitmap.Height, metadata);
        using var converted = new SKBitmap(Info(bitmap.Width, bitmap.Height));
        using (var source = bitmap.PeekPixels())
        using (var target = converted.PeekPixels())
            if (!source.ReadPixels(target))
                throw new InvalidOperationException($"画素を読み出せません（{bitmap.ColorType} / {bitmap.AlphaType}）");
        return FromRgba(converted.GetPixelSpan(), converted.Width, converted.Height, metadata);
    }

    /// <summary>同じ画素で、メタデータだけを metadata の写しに置き換えた画像（metadata が null ならメタデータ無し）</summary>
    internal static Image<Rgba32> WithMetadata(Image<Rgba32> image, ImageMetadata? metadata)
    {
        var rgba = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(rgba);
        return FromRgba(rgba, image.Width, image.Height, metadata);
    }

    /// <summary>
    /// 画素を写した画像を作る。metadata があれば、その写しを形式ごとの情報（PNG の色の種類など）も含めて付ける
    /// （ImageMetadata を受け取るコンストラクタは公開されていないので、写した配列を WrapMemory で包む）
    /// </summary>
    private static Image<Rgba32> FromRgba(ReadOnlySpan<byte> rgba, int width, int height, ImageMetadata? metadata)
    {
        if (metadata == null) return SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(rgba, width, height);
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgba32>(rgba).ToArray();
        return SixLabors.ImageSharp.Image.WrapMemory(Configuration.Default, pixels.AsMemory(), width, height, metadata.DeepClone());
    }

    /// <summary>Skia の範囲を ImageSharp の範囲にする</summary>
    internal static Rectangle ToRectangle(SKRectI r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>bitmap を ImageSharp の画像に写して edit で変え、bitmap に書き戻す（その場で変える API 用）</summary>
    public static void Edit(SKBitmap bitmap, Action<Image<Rgba32>> edit)
    {
        using var image = ToImageSharp(bitmap);
        edit(image);
        CopyInto(image, bitmap);
    }

    /// <summary>ImageSharp で処理した画像の画素を、同じ大きさの SKBitmap に書き戻す（その場で変える API 用）</summary>
    public static unsafe void CopyInto(Image<Rgba32> source, SKBitmap target)
    {
        if (source.Width != target.Width || source.Height != target.Height)
            throw new ArgumentException($"大きさが違います（{source.Width}×{source.Height} と {target.Width}×{target.Height}）");
        if (target.ColorType == SKColorType.Rgba8888 && target.AlphaType == SKAlphaType.Unpremul && target.RowBytes == target.Width * 4)
        {
            source.CopyPixelDataTo(new Span<byte>((void*)target.GetPixels(), target.ByteCount));
            return;
        }
        using var rgba = ToSkia(source);
        using var from = rgba.PeekPixels();
        using var to = target.PeekPixels();
        if (!from.ReadPixels(to)) throw new InvalidOperationException($"画素を書き戻せません（{target.ColorType} / {target.AlphaType}）");
    }
}
