// 元の画像を編集結果で置き換えるときの検査と、編集結果の保存（切り抜き・モザイク・枠や矢印・回転で共通）
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageViewer.Core.Editing;

public static class SourceGuard
{
    public static bool IsSameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 元のファイルを置き換えてよいか（ヘッダーだけ読む）。書き出せない形式・アニメや複数ページの画像・
    /// ページの数を確かめられない画像は NotSupportedException（読むのは先頭のコマだけなので、保存すると残りが消える）
    /// </summary>
    /// <param name="action">メッセージに出す操作の名前（「上書き」「回転」）</param>
    public static void EnsureReplaceable(string path, string action = "上書き")
    {
        if (!ImageSaver.CanWrite(Path.GetExtension(path)))
            throw new NotSupportedException($"この形式は書き出せないので{action}できません: {Path.GetExtension(path)}");
        int? frames = Adjuster.FrameCount(path);
        if (frames > 1) throw new NotSupportedException($"アニメーションや複数ページの画像は{action}できません（保存すると先頭の 1 枚だけになるため）");
        if (frames == null) throw new NotSupportedException($"ページの数を確かめられない画像なので、{action}しませんでした");
    }

    /// <summary>撮影情報（EXIF など）を残せない画像（WIC で読んだもの）は、置き換えると失われるので NotSupportedException</summary>
    public static void EnsureKeepsMetadata(Image image, string action = "上書き")
    {
        if (!Adjuster.KeepsMetadata(image))
            throw new NotSupportedException($"撮影情報（EXIF など）を残して保存できない画像なので、{action}しませんでした");
    }

    /// <summary>
    /// 編集した画像を保存する。dst が src なら元の画像を置き換え、そうでなければ新しいファイルとして保存する
    /// （保存先を決めた後に同じ名前のファイルができていても置き換えず、別の名前にする）
    /// </summary>
    /// <returns>実際に保存したパス</returns>
    public static string Save(Image<Rgba32> image, string src, string dst)
    {
        if (!IsSameFile(src, dst)) return ImageSaver.SaveNew(image, dst);
        ImageSaver.Save(image, dst);
        return dst;
    }
}
