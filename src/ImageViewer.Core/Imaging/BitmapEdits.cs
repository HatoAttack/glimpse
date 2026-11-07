// 編集のダイアログで表示用に使う処理（キャンバスの大きさに縮める・透明な所を塗る）。
// ImageSharp から SkiaSharp への移行中（#81）は ImageSharp で処理する（今までの見た目のまま。Skia に移すのは手順7）
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;

namespace ImageViewer.Core.Imaging;

public static class BitmapEdits
{
    /// <summary>width × height に縮めた写し（線形補間。表示用なので速さを優先する）</summary>
    public static SKBitmap Resized(SKBitmap source, int width, int height)
    {
        using var image = ImageSharpBridge.ToImageSharp(source);
        image.Mutate(x => x.Resize(width, height, KnownResamplers.Triangle));
        return ImageSharpBridge.ToSkia(image);
    }

    /// <summary>透明な所を color で塗る（その場で変える。JPEG で保存したときの見え方にする）</summary>
    public static void Flatten(SKBitmap image, SKColor color) =>
        ImageSharpBridge.Edit(image, img => img.Mutate(x => x.BackgroundColor(new Rgba32(color.Red, color.Green, color.Blue, color.Alpha))));
}
