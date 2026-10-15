// ImageSharp から SkiaSharp への移行中（#81）だけ使う、画像の受け渡しの変換。
// 画像は SKBitmap（RGBA・乗算しない。ImageSharp の Rgba32 と同じ並び）で受け渡し、まだ ImageSharp で処理する所とは、ここで変換する。
// 画素の値は変わらない。ImageSharp を外すとき（#81 手順8）に、このファイルごと消す
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;

namespace ImageViewer.Core.Imaging;

public static class ImageSharpBridge
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

    /// <summary>SKBitmap を ImageSharp の画像にする（どの形式の SKBitmap でも、RGBA・乗算しない値に読み出す）</summary>
    public static Image<Rgba32> ToImageSharp(SKBitmap bitmap)
    {
        if (bitmap.ColorType == SKColorType.Rgba8888 && bitmap.AlphaType is SKAlphaType.Unpremul or SKAlphaType.Opaque
            && bitmap.RowBytes == bitmap.Width * 4)
            return SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(bitmap.GetPixelSpan(), bitmap.Width, bitmap.Height);
        using var converted = new SKBitmap(Info(bitmap.Width, bitmap.Height));
        using (var source = bitmap.PeekPixels())
        using (var target = converted.PeekPixels())
            if (!source.ReadPixels(target))
                throw new InvalidOperationException($"画素を読み出せません（{bitmap.ColorType} / {bitmap.AlphaType}）");
        return SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(converted.GetPixelSpan(), converted.Width, converted.Height);
    }
}
