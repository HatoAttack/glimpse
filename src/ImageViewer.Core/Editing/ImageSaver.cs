// 編集結果の保存。拡張子で形式を決め、一時ファイルに書き切ってから差し替える
// （保存に失敗しても・途中で落ちても、中途半端なファイルや壊れた元画像は残らない）
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageViewer.Core.Editing;

/// <summary>出力形式。Keep は元の拡張子のまま（書き出せない形式なら JPG）</summary>
public enum OutputFormat { Keep, Jpeg, Png, Webp }

public static class ImageSaver
{
    public const int JpegQuality = 90;
    public const int WebpQuality = 90;

    /// <summary>WEBP で保存できる最大の縦横</summary>
    public const int WebpMaxEdge = 16383;

    /// <summary>書き出せる拡張子（HEIC / AVIF / RAW などは読めても書けない）</summary>
    private static readonly HashSet<string> WritableExtensions = new(
        new[] { ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".tga", ".qoi" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>JPEG で保存できる最大の縦横</summary>
    public const int JpegMaxEdge = 65535;

    /// <summary>保存先の拡張子の形式で保存できる最大の縦横（上限が無ければ int.MaxValue）</summary>
    public static int MaxEdgeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webp" => WebpMaxEdge,
        ".jpg" or ".jpeg" or ".jfif" => JpegMaxEdge,
        _ => int.MaxValue,
    };

    public static bool CanWrite(string extension) => WritableExtensions.Contains(extension);

    /// <summary>出力形式に対応する拡張子。Keep なら元の拡張子（書き出せない形式なら .jpg）</summary>
    public static string ExtensionFor(OutputFormat format, string sourceExtension) => format switch
    {
        OutputFormat.Jpeg => ".jpg",
        OutputFormat.Png => ".png",
        OutputFormat.Webp => ".webp",
        _ => CanWrite(sourceExtension) ? sourceExtension : ".jpg",
    };

    /// <summary>
    /// dst の拡張子の形式で保存する。JPEG は透過を扱えないので image 自体を白背景に合成してから書く
    /// （呼び出し側の画像が変わる点に注意）
    /// </summary>
    public static void Save(Image<Rgba32> image, string dst)
    {
        string ext = Path.GetExtension(dst).ToLowerInvariant();
        if (ext == ".webp" && Math.Max(image.Width, image.Height) > WebpMaxEdge)
            throw new NotSupportedException($"WEBP は縦横 {WebpMaxEdge}px までしか保存できません（{image.Width}×{image.Height}）");

        IImageEncoder encoder;
        switch (ext)
        {
            case ".jpg" or ".jpeg" or ".jfif":
                image.Mutate(x => x.BackgroundColor(Color.White));
                encoder = new JpegEncoder { Quality = JpegQuality };
                break;
            case ".webp":
                encoder = new WebpEncoder { Quality = WebpQuality };
                break;
            case ".png":
                encoder = new PngEncoder();
                break;
            default:
                if (!CanWrite(ext) || !Configuration.Default.ImageFormatsManager.TryFindFormatByFileExtension(ext.TrimStart('.'), out var format))
                    throw new NotSupportedException($"この形式では保存できません: {ext}");
                encoder = Configuration.Default.ImageFormatsManager.GetEncoder(format);
                break;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst))!);
        string temp = dst + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                image.Save(stream, encoder);
            File.Move(temp, dst, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>path が既にあれば「名前 (2).拡張子」「名前 (3).拡張子」…の空いている名前を返す</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
