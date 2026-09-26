// サムネイル 1 枚の生成: シェルのサムネイル → 取れなければ ImageLoader で縮小読み込み
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ImageViewer.Core.Imaging;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageViewer.Core.Thumbnails;

public static class ThumbnailGenerator
{
    // PNG / WEBP 等は縮小デコードが効かず一旦フルサイズで展開するので、同時実行数を絞ってメモリの山を抑える
    private static readonly SemaphoreSlim DecodeGate = new(2);

    /// <summary>長辺 size 以下のサムネイル（描画が速い 32bppPArgb）。読めなければ例外</summary>
    public static Bitmap Generate(string path, int size)
    {
        if (ShellThumbnail.TryGet(path, size) is Bitmap shell) return shell;

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

    /// <summary>ImageSharp の画像を GDI+ の 32bppPArgb（乗算済みアルファ）Bitmap に変換</summary>
    public static Bitmap ToPArgbBitmap(SixLabors.ImageSharp.Image<Rgba32> image) => ToPArgbBitmap(image.Frames.RootFrame);

    /// <summary>1 コマ分を変換（アニメのコマ用）</summary>
    public static Bitmap ToPArgbBitmap(SixLabors.ImageSharp.ImageFrame<Rgba32> image)
    {
        int w = image.Width, h = image.Height;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[w * 4];
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < h; y++)
                {
                    var span = accessor.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        var p = span[x];
                        int i = x * 4;
                        row[i] = (byte)(p.B * p.A / 255);
                        row[i + 1] = (byte)(p.G * p.A / 255);
                        row[i + 2] = (byte)(p.R * p.A / 255);
                        row[i + 3] = p.A;
                    }
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                }
            });
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }
}
