// 対応画像形式の判定
// ImageSharp で読める形式（どの PC でも確実）＋ WIC で読める形式（HEIC / AVIF / RAW 等、PC の拡張機能次第）
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

    public static bool IsImageSharpFormat(string path) =>
        ImageSharpExtensions.Contains(Path.GetExtension(path));

    public static bool IsWicFormat(string path) =>
        WicCodecs.DecoderExtensions.Contains(Path.GetExtension(path));

    public static bool IsSupported(string path) => IsImageSharpFormat(path) || IsWicFormat(path);

    /// <summary>フォルダ直下の対応画像をファイル名順（エクスプローラーと同じ比較）で列挙。
    /// FileInfo のサイズ・更新日時は列挙時に取得済みなので追加のディスクアクセスは発生しない</summary>
    public static List<FileInfo> ListImages(string folder, CancellationToken ct = default)
    {
        var list = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            if (IsSupported(file.Name)) list.Add(file);
        }
        list.Sort((a, b) => Ordering.FileSorting.NaturalNameComparer.Compare(a.Name, b.Name));
        return list;
    }
}
