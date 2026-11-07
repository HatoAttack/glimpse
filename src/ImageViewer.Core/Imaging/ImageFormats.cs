// 対応画像形式の判定
// Skia と自前で読める形式（どの PC でも確実）＋ Windows 標準の WIC で読む形式（TIFF）
// ＋ WIC で読める形式（HEIC / AVIF / RAW 等、PC の拡張機能次第）
// （名前の ImageSharp は、#81 で ImageSharp から移るまで読んでいた形式の意味。手順8 で名前を変える）
namespace ImageViewer.Core.Imaging;

public static class ImageFormats
{
    /// <summary>Skia（JPEG・PNG・GIF・WebP・BMP・ICO）と自前（QOI・PNM）で読む拡張子。TGA は #81 で対応をやめた</summary>
    public static readonly IReadOnlySet<string> ImageSharpExtensions = new HashSet<string>(
        new[]
        {
            ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".webp", ".bmp",
            ".ico", ".cur", ".pbm", ".pgm", ".ppm", ".qoi",
        },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Windows に標準で入っている WIC のデコーダで読む拡張子（どの PC でも読める。Skia には TIFF のデコーダが無い）
    /// </summary>
    public static readonly IReadOnlySet<string> BuiltInWicExtensions = new HashSet<string>(
        new[] { ".tif", ".tiff" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>どの PC でも読める拡張子（Skia・自前 ＋ Windows 標準の WIC）</summary>
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
