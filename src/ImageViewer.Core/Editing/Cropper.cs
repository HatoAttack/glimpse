// 切り抜き（image-sizechange の切り抜きタブから移植）
using ImageViewer.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageViewer.Core.Editing;

public static class Cropper
{
    /// <summary>アスペクト比のプリセット（表示名 → 幅/高さ。null は自由）。縦向きは「縦横を入れ替え」で</summary>
    public static readonly (string Name, double? Ratio)[] AspectPresets =
    {
        ("自由", null),
        ("1:1", 1.0),
        ("4:3", 4.0 / 3),
        ("3:2", 3.0 / 2),
        ("16:9", 16.0 / 9),
    };

    /// <summary>入れ替えを反映した比（4:3 + 入れ替え → 3:4）</summary>
    public static double? Flip(double? aspect, bool flip) => aspect is double a && flip ? 1 / a : aspect;

    /// <summary>画像の中央に収まる最大の、指定の比の矩形（画像座標）</summary>
    public static (double X, double Y, double W, double H) CenterRect(int imageWidth, int imageHeight, double aspect)
    {
        double w = imageWidth, h = imageWidth / aspect;
        if (h > imageHeight)
        {
            h = imageHeight;
            w = imageHeight * aspect;
        }
        return ((imageWidth - w) / 2, (imageHeight - h) / 2, w, h);
    }

    /// <summary>実数の矩形を画像の範囲内の整数の矩形に丸める</summary>
    public static Rectangle ClampBox(double x0, double y0, double x1, double y1, int imageWidth, int imageHeight)
    {
        int ix0 = Math.Max(0, (int)Math.Round(x0));
        int iy0 = Math.Max(0, (int)Math.Round(y0));
        int ix1 = Math.Min(imageWidth, (int)Math.Round(x1));
        int iy1 = Math.Min(imageHeight, (int)Math.Round(y1));
        return Rectangle.FromLTRB(ix0, iy0, ix1, iy1);
    }

    /// <summary>切り抜いた画像の保存先: 出力フォルダ\元の名前_crop.拡張子（書き出せない形式は .jpg）。既にあれば (2)… を付ける</summary>
    public static string OutputPathFor(string source, string outputFolder) =>
        ImageSaver.UniquePath(Path.Combine(outputFolder,
            Path.GetFileNameWithoutExtension(source) + "_crop" + ImageSaver.ExtensionFor(OutputFormat.Keep, Path.GetExtension(source))));

    /// <summary>読み込み済みの画像の box の範囲を保存する</summary>
    public static void SaveCrop(Image<Rgba32> image, Rectangle box, string dst)
    {
        using var cropped = image.Clone(x => x.Crop(box));
        ImageSaver.Save(cropped, dst);
    }

    /// <summary>ファイルを読み、中央を指定の比で切り抜いて保存する（一括用）</summary>
    public static void CropCenter(string src, double aspect, string dst)
    {
        using var image = ImageLoader.Load(src);
        var (x, y, w, h) = CenterRect(image.Width, image.Height, aspect);
        var box = ClampBox(x, y, x + w, y + h, image.Width, image.Height);
        image.Mutate(c => c.Crop(box));
        ImageSaver.Save(image, dst);
    }
}
