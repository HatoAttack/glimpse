// 回転（90° 単位）。画素を回して元のファイルに上書きする（どのソフトでも同じ向きに見えるように）。
// JPEG / WEBP は保存し直すので、保存の画質の設定で圧縮し直す。EXIF の向きは回した後の画像に合わせて「そのまま」にする
using ImageViewer.Core.Imaging;
using SixLabors.ImageSharp.Processing;

namespace ImageViewer.Core.Editing;

public enum RotateDirection { Right90, Left90, Half }

public static class Rotator
{
    /// <summary>
    /// 画像を回して上書きする。アニメや複数ページの画像・書き出せない形式（HEIC / RAW など）・
    /// 撮影情報（EXIF）を残せない画像（WIC で読んだ JPEG の亜種など）は NotSupportedException（元のまま）
    /// </summary>
    public static void RotateFile(string path, RotateDirection direction)
    {
        SourceGuard.EnsureReplaceable(path, "回転");
        using var image = ImageLoader.Load(path); // EXIF の向きは反映済み（向きの値は「そのまま」になっている）
        SourceGuard.EnsureKeepsMetadata(image, "回転");
        image.Mutate(x => x.Rotate(direction switch
        {
            RotateDirection.Right90 => RotateMode.Rotate90,
            RotateDirection.Left90 => RotateMode.Rotate270,
            _ => RotateMode.Rotate180,
        }));
        ImageSaver.Save(image, path);
    }

    /// <summary>選んだ画像を順に回す（重い処理なので呼び出し側で別スレッドへ）。失敗は「名前: 理由」で返す</summary>
    public static ConvertResult RotateFiles(IReadOnlyList<string> paths, RotateDirection direction,
        IProgress<ConvertProgress>? progress = null, CancellationToken ct = default) =>
        BatchRunner.Run(paths, Path.GetFileName, path => RotateFile(path, direction), skippedBefore: 0, progress, ct);
}
