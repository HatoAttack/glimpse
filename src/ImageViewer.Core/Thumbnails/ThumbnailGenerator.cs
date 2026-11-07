// サムネイル 1 枚の生成: シェルのサムネイル → 取れなければ ImageLoader で縮小読み込み
// 画像以外のファイルは、シェルのサムネイル（PDF・動画など）→ 取れなければエクスプローラーと同じアイコン
using System.Drawing;
using System.Drawing.Imaging;
using ImageViewer.Core.Imaging;
using SkiaSharp;

namespace ImageViewer.Core.Thumbnails;

public static class ThumbnailGenerator
{
    // PNG / WEBP 等は縮小デコードが効かず一旦フルサイズで展開するので、同時実行数を絞ってメモリの山を抑える
    private static readonly SemaphoreSlim DecodeGate = new(2);

    /// <summary>長辺 size 以下のサムネイル（描画が速い 32bppPArgb）。読めなければ例外</summary>
    public static Bitmap Generate(string path, int size)
    {
        if (!ImageFormats.IsSupported(path))
        {
            // アイコンはフォルダのタイルと同じく、枠の 6 割の大きさで
            return ShellThumbnail.TryGet(path, size) ?? ShellThumbnail.TryGetIcon(path, size * 6 / 10)
                ?? throw new NotSupportedException("サムネイルもアイコンも取れませんでした");
        }

        // ZIP の中の画像はシェルが扱えないので、いつも自前で読む
        if (!Archives.ArchivePath.IsInside(path) && ShellThumbnail.TryGet(path, size) is Bitmap shell) return shell;

        DecodeGate.Wait();
        try
        {
            using var image = ImageLoader.Load(path, LoadOptions.Thumbnail(size));
            return ToPArgbBitmap(image);
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    /// <summary>SKBitmap を GDI+ の 32bppPArgb（乗算済みアルファ）Bitmap に変換（Skia が BGRA・乗算済みに直して書き込む）</summary>
    public static Bitmap ToPArgbBitmap(SKBitmap image)
    {
        int w = image.Width, h = image.Height;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            // GDI+ の PArgb はメモリ上 BGRA の順で、乗算済み
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var pixmap = image.PeekPixels();
            if (!pixmap.ReadPixels(info, data.Scan0, data.Stride, 0, 0))
                throw new InvalidOperationException($"画素を読み出せません（{image.ColorType} / {image.AlphaType}）");
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }
}
