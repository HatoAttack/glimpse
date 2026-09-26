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

/// <summary>上書きしない保存で、書き終えたときには保存先に別のファイルができていた</summary>
public sealed class DestinationExistsException(string path) : IOException($"同名のファイルができていたので保存しませんでした: {Path.GetFileName(path)}")
{
    public string Destination { get; } = path;
}

/// <summary>出力形式。Keep は元の拡張子のまま（書き出せない形式なら JPG）</summary>
public enum OutputFormat { Keep, Jpeg, Png, Webp }

public static class ImageSaver
{
    public const int DefaultQuality = 90;
    public const int MinQuality = 1;
    public const int MaxQuality = 100;

    private static int _jpegQuality = DefaultQuality, _webpQuality = DefaultQuality;

    /// <summary>JPEG で保存するときの画質（1〜100。範囲外は丸める）。起動時と設定を変えたときにアプリが入れる</summary>
    public static int JpegQuality
    {
        get => _jpegQuality;
        set => _jpegQuality = Math.Clamp(value, MinQuality, MaxQuality);
    }

    /// <summary>WEBP で保存するときの画質（1〜100。範囲外は丸める）</summary>
    public static int WebpQuality
    {
        get => _webpQuality;
        set => _webpQuality = Math.Clamp(value, MinQuality, MaxQuality);
    }

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
    /// <param name="overwrite">
    /// false なら、書き終えて差し替える時点で dst があれば保存せずに DestinationExistsException
    /// （事前に無いことを確かめていても、書いている間に別の処理が作ることがあるため、最後の差し替えで確かめる）
    /// </param>
    public static void Save(Image<Rgba32> image, string dst, bool overwrite = true)
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
        // 同じ保存先へ同時に書く処理があっても一時ファイルを取り合わないよう、一時ファイルの名前は毎回変える
        string temp = $"{dst}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                image.Save(stream, encoder);
            try
            {
                File.Move(temp, dst, overwrite);
            }
            catch (IOException) when (!overwrite && File.Exists(dst))
            {
                throw new DestinationExistsException(dst);
            }
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
