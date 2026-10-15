// 対応画像形式の判定
// ImageSharp で読める形式（どの PC でも確実）＋ Windows 標準の WIC で読む形式（TIFF）
// ＋ WIC で読める形式（HEIC / AVIF / RAW 等、PC の拡張機能次第）
namespace ImageViewer.Core.Imaging;

public static class ImageFormats
{
    /// <summary>ImageSharp で読める拡張子</summary>
    public static readonly IReadOnlySet<string> ImageSharpExtensions = new HashSet<string>(
        new[]
        {
            ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".webp", ".bmp",
            ".tga", ".ico", ".cur", ".pbm", ".pgm", ".ppm", ".qoi",
        },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Windows に標準で入っている WIC のデコーダで読む拡張子（どの PC でも読める）。
    /// TIFF は ImageSharp 3.1 の TIFF デコーダに脆弱性（GHSA-wmxv-xphr-5c9g）があるので、ImageSharp では読まない
    /// </summary>
    public static readonly IReadOnlySet<string> BuiltInWicExtensions = new HashSet<string>(
        new[] { ".tif", ".tiff" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>どの PC でも読める拡張子（ImageSharp ＋ Windows 標準の WIC）</summary>
    public static IEnumerable<string> BuiltInExtensions => ImageSharpExtensions.Concat(BuiltInWicExtensions);

    public static bool IsImageSharpFormat(string path) =>
        ImageSharpExtensions.Contains(Path.GetExtension(path));

    public static bool IsWicFormat(string path)
    {
        string ext = Path.GetExtension(path);
        return BuiltInWicExtensions.Contains(ext) || WicCodecs.DecoderExtensions.Contains(ext);
    }

    public static bool IsSupported(string path) => IsImageSharpFormat(path) || IsWicFormat(path);

    /// <summary>フォルダ直下の対応画像をファイル名順（エクスプローラーと同じ比較）で列挙。
    /// FileInfo のサイズ・更新日時は列挙時に取得済みなので追加のディスクアクセスは発生しない</summary>
    public static List<ImageFile> ListImages(string folder, CancellationToken ct = default)
    {
        var list = new List<ImageFile>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            if (IsSupported(file.Name)) list.Add(ImageFile.From(file));
        }
        list.Sort((a, b) => Ordering.FileSorting.NaturalNameComparer.Compare(a.Name, b.Name));
        return list;
    }
}
