// 編集結果の保存。拡張子で形式を決め、一時ファイルに書き切ってから差し替える
// （保存に失敗しても・途中で落ちても、中途半端なファイルや壊れた元画像は残らない）
// - JPEG・PNG・WebP: Skia で書く。撮影情報（EXIF）は、元のファイルから取り出して書き戻す（ExifBlock）
// - GIF・BMP・TIFF: WIC（WPF の画像 API）で書く。QOI: 自前で書く
// - 色プロファイルは付けない（画素は読むときに sRGB に変換してある。#81 の決定）
using System.Windows.Media.Imaging;
using ImageViewer.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Wpf = System.Windows.Media;

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

    /// <summary>書き出せる拡張子（HEIC / AVIF / RAW・PNM などは読めても書けない）</summary>
    private static readonly HashSet<string> WritableExtensions = new(
        new[] { ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".qoi" },
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

    /// <summary>dst の拡張子の形式で保存する。JPEG は透過を扱えないので、白い背景に重ねて書く（image は変えない）</summary>
    /// <param name="overwrite">
    /// false なら、書き終えて差し替える時点で dst があれば保存せずに DestinationExistsException
    /// （事前に無いことを確かめていても、書いている間に別の処理が作ることがあるため、最後の差し替えで確かめる）
    /// </param>
    /// <param name="metadataFrom">
    /// このファイルの撮影情報（EXIF）を読み直して書く（編集した画像の元のファイル）。null なら書かない。
    /// EXIF を書けるのは JPEG・PNG・WebP（ほかの形式に保存すると画素だけになる）
    /// </param>
    public static void Save(SKBitmap image, string dst, bool overwrite = true, string? metadataFrom = null)
    {
        byte[] data = Encode(image, Path.GetExtension(dst), metadataFrom == null ? null : ImageLoader.MetadataForSave(metadataFrom)?.Exif);
        WriteAtomically(dst, data, overwrite);
    }

    /// <summary>ImageSharp の画像を保存する（移行中だけ。#81 の手順7 で消す）。メタデータは SKBitmap 版と同じく metadataFrom から</summary>
    internal static void Save(Image<Rgba32> image, string dst, bool overwrite = true, string? metadataFrom = null)
    {
        using var bitmap = ImageSharpBridge.ToSkia(image);
        Save(bitmap, dst, overwrite, metadataFrom);
    }

    /// <summary>
    /// 拡張子 extension の形式に書いたバイト列。exif（TIFF の形）があれば、画像の大きさに合わせて直して入れる（JPEG・PNG・WebP だけ）
    /// </summary>
    internal static byte[] Encode(SKBitmap image, string extension, byte[]? exif)
    {
        string ext = extension.ToLowerInvariant();
        if (ext == ".webp" && Math.Max(image.Width, image.Height) > WebpMaxEdge)
            throw new NotSupportedException($"WEBP は縦横 {WebpMaxEdge}px までしか保存できません（{image.Width}×{image.Height}）");
        if (ext is ".jpg" or ".jpeg" or ".jfif" && Math.Max(image.Width, image.Height) > JpegMaxEdge)
            throw new NotSupportedException($"JPEG は縦横 {JpegMaxEdge}px までしか保存できません（{image.Width}×{image.Height}）");
        if (!CanWrite(ext)) throw new NotSupportedException($"この形式では保存できません: {ext}");

        byte[]? fixedExif = exif == null ? null : ExifBlock.ForSave(exif, image.Width, image.Height);
        // 色空間を付けずに書く（付けると Skia が sRGB のプロファイルを埋め込む）。画素は RGBA・乗算しない
        using var rgba = SkiaDecoder.Convert(image, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var info = new SKImageInfo(rgba.Width, rgba.Height, SKColorType.Rgba8888, IsOpaque(rgba) ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
        using var pixmap = new SKPixmap(info, rgba.GetPixels(), rgba.RowBytes);
        switch (ext)
        {
            case ".jpg" or ".jpeg" or ".jfif":
            {
                using var flat = FlattenOnWhite(pixmap);
                using var flatPixmap = flat.PeekPixels();
                var jpeg = EncodeWith(flatPixmap.Encode(new SKJpegEncoderOptions(JpegQuality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore)), ext);
                return fixedExif == null ? jpeg : ExifBlock.AddToJpeg(jpeg, fixedExif);
            }
            case ".png":
            {
                var png = EncodeWith(pixmap.Encode(SKPngEncoderOptions.Default), ext);
                return fixedExif == null ? png : ExifBlock.AddToPng(png, fixedExif);
            }
            case ".webp":
            {
                var webp = EncodeWith(pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, WebpQuality)), ext);
                return fixedExif == null ? webp : ExifBlock.AddToWebp(webp, fixedExif, image.Width, image.Height);
            }
            case ".qoi":
                return SimpleFormats.EncodeQoi(rgba);
            default:
                return EncodeWithWic(rgba, ext);
        }
    }

    private static byte[] EncodeWith(SKData? data, string ext)
    {
        using (data)
            return data?.ToArray() ?? throw new InvalidOperationException($"{ext} に書き出せませんでした");
    }

    /// <summary>透明な画素が無いか（無ければ PNG・WebP を不透明として書く。PNG は RGB になって小さくなる）</summary>
    private static bool IsOpaque(SKBitmap rgba)
    {
        var px = rgba.GetPixelSpan();
        for (int i = 3; i < px.Length; i += 4)
            if (px[i] != 255) return false;
        return true;
    }

    /// <summary>白い背景に重ねた不透明な画像（JPEG 用）</summary>
    private static SKBitmap FlattenOnWhite(SKPixmap source)
    {
        var flat = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var image = SKImage.FromPixels(source);
        using var canvas = new SKCanvas(flat);
        canvas.Clear(SKColors.White);
        canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);
        return flat;
    }

    /// <summary>GIF・BMP・TIFF を WIC で書く（GIF は WIC が 256 色に減らす）</summary>
    private static byte[] EncodeWithWic(SKBitmap rgba, string ext)
    {
        // WIC の Bgra32 は乗算しない BGRA。BMP は透明を持たない 24 ビットで書く（32 ビットの透明は、読むソフトによって扱いが違う）
        using var bgra = SkiaDecoder.Convert(rgba, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var source = BitmapSource.Create(bgra.Width, bgra.Height, 96, 96, Wpf.PixelFormats.Bgra32, null,
            bgra.GetPixels(), bgra.ByteCount, bgra.RowBytes);
        BitmapSource frame = ext == ".bmp" ? new FormatConvertedBitmap(source, Wpf.PixelFormats.Bgr24, null, 0) : source;
        BitmapEncoder encoder = ext switch
        {
            ".gif" => new GifBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => throw new NotSupportedException($"この形式では保存できません: {ext}"),
        };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>一時ファイルに書き切ってから dst に差し替える</summary>
    private static void WriteAtomically(string dst, byte[] data, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst))!);
        // 同じ保存先へ同時に書く処理があっても一時ファイルを取り合わないよう、一時ファイルの名前は毎回変える
        string temp = $"{dst}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(data);
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

    /// <summary>
    /// 新しいファイルとして保存する（既にあるファイルは置き換えない）。path が空いていなければ「名前 (2).拡張子」…の空いている名前にする。
    /// 空いているのを確かめてから書き終えるまでの間に別の処理が同じ名前で作ったときも、そのファイルは残して別の名前で保存する
    /// </summary>
    /// <returns>実際に保存したパス</returns>
    public static string SaveNew(SKBitmap image, string path, string? metadataFrom = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            string dst = UniquePath(path);
            try
            {
                Save(image, dst, overwrite: false, metadataFrom);
                return dst;
            }
            catch (DestinationExistsException) when (attempt < 20)
            {
                // 別の処理が先に作った。空いている名前を選び直す
            }
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
