// 対応画像形式の判定
// 現状は ImageSharp で読める形式のみ。WIC 経由の形式（HEIC / AVIF / RAW 等）は起動時の動的判定で追加予定
namespace ImageViewer.Core.Imaging;

public static class ImageFormats
{
    /// <summary>ImageSharp で読める拡張子</summary>
    public static readonly IReadOnlySet<string> ImageSharpExtensions = new HashSet<string>(
        new[]
        {
            ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".webp", ".bmp",
            ".tif", ".tiff", ".tga", ".ico", ".cur", ".pbm", ".pgm", ".ppm", ".qoi",
        },
        StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string path) =>
        ImageSharpExtensions.Contains(Path.GetExtension(path));

    /// <summary>フォルダ直下の対応画像をファイル名順（大文字小文字無視）で列挙。
    /// FileInfo のサイズ・更新日時は列挙時に取得済みなので追加のディスクアクセスは発生しない</summary>
    public static List<FileInfo> ListImages(string folder, CancellationToken ct = default)
    {
        var list = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            if (IsSupported(file.Name)) list.Add(file);
        }
        list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return list;
    }
}
