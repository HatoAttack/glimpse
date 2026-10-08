// モザイク・ぼかし。画像の中の選んだ範囲（いくつでも）だけにかける。保存先が元のファイルなら上書きする（切り抜きの上書きと同じ条件で）
using ImageViewer.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageViewer.Core.Editing;

public enum MaskEffect { Mosaic, Blur }

public static class Masker
{
    public const int MinLevel = 1, MaxLevel = 10, DefaultLevel = 4;

    /// <summary>ぼかしの強さ（ガウスの σ）の上限。大きすぎると重いだけで見た目は変わらない</summary>
    private const float MaxSigma = 50;

    /// <summary>
    /// 強さ（1〜10）から、モザイクの 1 マスの大きさ（px）を決める。画像の長い辺に対する割合なので、
    /// 大きさの違う画像にも同じ見た目でかかる（ぼかしは、この半分を σ にする）
    /// </summary>
    public static int EffectSize(int width, int height, int level) =>
        Math.Max(2, (int)Math.Round(Math.Max(width, height) * Math.Clamp(level, MinLevel, MaxLevel) / 300.0));

    /// <summary>image の boxes の範囲に、マスの大きさ size でモザイク・ぼかしをかける（範囲の外は変えない）</summary>
    public static void Apply(Image<Rgba32> image, IEnumerable<Rectangle> boxes, MaskEffect effect, int size)
    {
        var bounds = new Rectangle(0, 0, image.Width, image.Height);
        var targets = boxes.Select(b => Rectangle.Intersect(b, bounds)).Where(b => b.Width > 0 && b.Height > 0).ToList();
        if (targets.Count == 0 || size < 2) return;
        image.Mutate(c =>
        {
            foreach (var box in targets)
            {
                if (effect == MaskEffect.Mosaic) c.Pixelate(size, box);
                else c.GaussianBlur(Math.Min(MaxSigma, size / 2f), box);
            }
        });
    }

    /// <summary>画像座標の範囲（実数）を、画像の中の整数の矩形に丸める</summary>
    public static List<Rectangle> ToBoxes(IEnumerable<(double X0, double Y0, double X1, double Y1)> rects, int width, int height) =>
        rects.Select(r => Cropper.ClampBox(r.X0, r.Y0, r.X1, r.Y1, width, height)).Where(b => b.Width > 0 && b.Height > 0).ToList();

    /// <summary>
    /// ほかの画像（fromWidth × fromHeight）で決めた範囲を、次の画像へ引き継ぐ。
    /// 大きさが同じならそのまま、違えば縦・横それぞれ画像に対する割合で合わせる（隠したい所の位置を保つ）
    /// </summary>
    public static List<(double X0, double Y0, double X1, double Y1)> CarryRects(
        IEnumerable<(double X0, double Y0, double X1, double Y1)> rects, int fromWidth, int fromHeight, int toWidth, int toHeight)
    {
        double sx = (double)toWidth / fromWidth, sy = (double)toHeight / fromHeight;
        return rects.Select(r => (r.X0 * sx, r.Y0 * sy, r.X1 * sx, r.Y1 * sy)).ToList();
    }

    /// <summary>保存先: 出力フォルダ\元の名前_mosaic（ぼかしは _blur）.拡張子（書き出せない形式は .jpg）。既にあれば (2)… を付ける</summary>
    public static string OutputPathFor(string source, string outputFolder, MaskEffect effect) =>
        ImageSaver.UniquePath(Path.Combine(outputFolder,
            Path.GetFileNameWithoutExtension(source) + (effect == MaskEffect.Mosaic ? "_mosaic" : "_blur")
            + ImageSaver.ExtensionFor(OutputFormat.Keep, Path.GetExtension(source))));

    /// <summary>src から読み込み済みの画像に、boxes の範囲でかけて保存する（image 自体は変えない。dst が src なら上書き）</summary>
    public static void SaveMasked(Image<Rgba32> image, IReadOnlyList<Rectangle> boxes, MaskEffect effect, int level, string src, string dst)
    {
        if (Cropper.IsSameFile(src, dst))
        {
            Cropper.EnsureOverwritable(src);
            Cropper.EnsureKeepsMetadata(image);
        }
        using var masked = image.Clone();
        Apply(masked, boxes, effect, EffectSize(image.Width, image.Height, level));
        ImageSaver.Save(masked, dst);
    }

    /// <summary>ファイルを読み、ほかの画像（fromWidth × fromHeight）で決めた範囲を引き継いでかけ、保存する（一括用。dst が src なら上書き）</summary>
    public static void MaskCarried(string src, IReadOnlyList<(double X0, double Y0, double X1, double Y1)> rects, int fromWidth, int fromHeight,
        MaskEffect effect, int level, string dst)
    {
        bool overwrite = Cropper.IsSameFile(src, dst);
        if (overwrite) Cropper.EnsureOverwritable(src);
        using var image = ImageLoader.Load(src);
        if (overwrite) Cropper.EnsureKeepsMetadata(image);
        var boxes = ToBoxes(CarryRects(rects, fromWidth, fromHeight, image.Width, image.Height), image.Width, image.Height);
        Apply(image, boxes, effect, EffectSize(image.Width, image.Height, level));
        ImageSaver.Save(image, dst);
    }
}
