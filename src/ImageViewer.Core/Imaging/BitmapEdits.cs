// 編集のダイアログで表示用に使う処理（キャンバスの大きさに縮める・透明な所を塗る）
using SkiaSharp;

namespace ImageViewer.Core.Imaging;

public static class BitmapEdits
{
    /// <summary>width × height に縮めた写し（線形 + ミップマップ。表示用なので速さを優先する）</summary>
    public static SKBitmap Resized(SKBitmap source, int width, int height) => SkiaEdit.Resize(source, width, height, ResizeQuality.Fast);

    /// <summary>透明な所を color で塗る（その場で変える。JPEG で保存したときの見え方にする）</summary>
    public static void Flatten(SKBitmap image, SKColor color) => SkiaEdit.Flatten(image, color);
}
