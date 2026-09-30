// 切り抜き（image-sizechange の切り抜きタブから移植）。保存先が元のファイルなら上書きする（回転・補正の上書きと同じ条件で）
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

    /// <summary>
    /// 前の画像の切り抜き枠を、次の画像へ引き継ぐ（画像座標 x0, y0, x1, y1）。
    /// 大きさが同じならそのまま。違えば、枠の中心は画像に対する割合で、枠の大きさは縦横同じ倍率で合わせる
    /// （倍率は縦・横の比の小さい方なので、アスペクト比が保たれて画像からはみ出さない）。最後に画像の中へ収める
    /// </summary>
    public static (double X0, double Y0, double X1, double Y1) CarryRect(
        (double X0, double Y0, double X1, double Y1) rect, int fromWidth, int fromHeight, int toWidth, int toHeight)
    {
        double w = rect.X1 - rect.X0, h = rect.Y1 - rect.Y0;
        double cx = (rect.X0 + rect.X1) / 2, cy = (rect.Y0 + rect.Y1) / 2;
        if (fromWidth != toWidth || fromHeight != toHeight)
        {
            double scale = Math.Min((double)toWidth / fromWidth, (double)toHeight / fromHeight);
            w *= scale;
            h *= scale;
            cx = cx / fromWidth * toWidth;
            cy = cy / fromHeight * toHeight;
        }
        // 大きすぎれば（前の枠が画像いっぱいだった など）縦横同じ倍率で縮めてから、はみ出さないように寄せる
        double fit = Math.Min(1, Math.Min(toWidth / w, toHeight / h));
        w *= fit;
        h *= fit;
        double x0 = Math.Clamp(cx - w / 2, 0, toWidth - w), y0 = Math.Clamp(cy - h / 2, 0, toHeight - h);
        return (x0, y0, x0 + w, y0 + h);
    }

    /// <summary>ファイルを読み、ほかの画像（fromWidth × fromHeight）で決めた枠を引き継いで切り抜き、保存する（一括用。dst が src なら上書き）</summary>
    public static void CropCarried(string src, (double X0, double Y0, double X1, double Y1) rect, int fromWidth, int fromHeight, string dst)
    {
        bool overwrite = IsSameFile(src, dst);
        if (overwrite) EnsureOverwritable(src);
        using var image = ImageLoader.Load(src);
        if (overwrite) EnsureKeepsMetadata(image);
        var (x0, y0, x1, y1) = CarryRect(rect, fromWidth, fromHeight, image.Width, image.Height);
        var box = ClampBox(x0, y0, x1, y1, image.Width, image.Height);
        image.Mutate(c => c.Crop(box));
        ImageSaver.Save(image, dst);
    }

    private static bool IsSameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 元のファイルに上書きしてよいか（ヘッダーだけ読む）。書き出せない形式・アニメや複数ページの画像・
    /// ページの数を確かめられない画像は NotSupportedException（保存すると失われるものがあるため）
    /// </summary>
    public static void EnsureOverwritable(string path)
    {
        if (!ImageSaver.CanWrite(Path.GetExtension(path)))
            throw new NotSupportedException($"この形式は書き出せないので上書きできません: {Path.GetExtension(path)}");
        int? frames = Adjuster.FrameCount(path);
        if (frames > 1) throw new NotSupportedException("アニメーションや複数ページの画像は上書きできません（保存すると先頭の 1 枚だけになるため）");
        if (frames == null) throw new NotSupportedException("ページの数を確かめられない画像なので、上書きしませんでした");
    }

    /// <summary>撮影情報（EXIF など）を残せない画像（WIC で読んだもの）は、上書きすると失われるので NotSupportedException</summary>
    private static void EnsureKeepsMetadata(Image image)
    {
        if (!Adjuster.KeepsMetadata(image))
            throw new NotSupportedException("撮影情報（EXIF など）を残して保存できない画像なので、上書きしませんでした");
    }

    /// <summary>切り抜いた画像の保存先: 出力フォルダ\元の名前_crop.拡張子（書き出せない形式は .jpg）。既にあれば (2)… を付ける</summary>
    public static string OutputPathFor(string source, string outputFolder) =>
        ImageSaver.UniquePath(Path.Combine(outputFolder,
            Path.GetFileNameWithoutExtension(source) + "_crop" + ImageSaver.ExtensionFor(OutputFormat.Keep, Path.GetExtension(source))));

    /// <summary>src から読み込み済みの画像の box の範囲を保存する（dst が src なら上書き）</summary>
    public static void SaveCrop(Image<Rgba32> image, Rectangle box, string src, string dst)
    {
        if (IsSameFile(src, dst))
        {
            EnsureOverwritable(src);
            EnsureKeepsMetadata(image);
        }
        using var cropped = image.Clone(x => x.Crop(box));
        ImageSaver.Save(cropped, dst);
    }

    /// <summary>ファイルを読み、中央を指定の比で切り抜いて保存する（一括用。dst が src なら上書き）</summary>
    public static void CropCenter(string src, double aspect, string dst)
    {
        bool overwrite = IsSameFile(src, dst);
        if (overwrite) EnsureOverwritable(src);
        using var image = ImageLoader.Load(src);
        if (overwrite) EnsureKeepsMetadata(image);
        var (x, y, w, h) = CenterRect(image.Width, image.Height, aspect);
        var box = ClampBox(x, y, x + w, y + h, image.Width, image.Height);
        image.Mutate(c => c.Crop(box));
        ImageSaver.Save(image, dst);
    }
}
