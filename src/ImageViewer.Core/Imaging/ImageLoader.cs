// 画像読み込みの窓口。サムネイル・1枚表示・編集機能はすべてここ経由で読む
// - JPEG・PNG・GIF・WebP・BMP・ICO の中身: Skia で読む（SkiaDecoder。JPEG は縮小デコードが効く）
// - QOI・PNM の中身: 自前で読む（SimpleFormats）
// - どちらでも読めなければ・それ以外で WIC にデコーダがある形式（TIFF / HEIC / AVIF / RAW / JPEG XL 等）: WPF の画像 API（中身は WIC）で読む
// 形式は拡張子でなく中身で決める。拡張子を変えた TIFF なども、Skia・自前の形式でなければ WIC で読み直す
// どの経路でも「回転補正済み・sRGB・RGBA・乗算しない」SKBitmap を返す
// Core の中の編集の処理（ファイルを読んで保存するもの）は、まだ ImageSharp の画像を使うので LoadForEdit で読む（#81 の手順7 で消す）
using System.Windows.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Wpf = System.Windows.Media.Imaging;

namespace ImageViewer.Core.Imaging;

public sealed record LoadOptions
{
    /// <summary>長辺の上限（これより大きい画像は縮小して読む）。null なら原寸</summary>
    public int? MaxEdge { get; init; }

    /// <summary>アニメーション画像を先頭フレームだけ読む（サムネイル用。全フレームのデコードを避ける）</summary>
    public bool FirstFrameOnly { get; init; } = true;

    public static readonly LoadOptions Full = new();
    public static LoadOptions Thumbnail(int maxEdge) => new() { MaxEdge = maxEdge };
}

/// <summary>編集した画像を保存するときに書く、元のファイルのメタデータ（いまは撮影情報（EXIF）だけ。#81 の決定）</summary>
/// <param name="Exif">EXIF（TIFF の形）。元のファイルに無ければ null</param>
public sealed record SaveMetadata(byte[]? Exif);

/// <summary>画像の大きさ（回転補正後の見た目どおりの縦横）と形式、あれば撮影情報</summary>
public sealed record ImageHeader(int Width, int Height, string Format)
{
    /// <summary>撮影情報（EXIF）。無ければ null</summary>
    public PhotoInfo? Photo { get; init; }
}

public static class ImageLoader
{
    private static readonly HashSet<string> HeifExtensions = new(
        new[] { ".heic", ".heif", ".hif", ".heics", ".heifs", ".avif", ".avifs", ".avci", ".avcs" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>画像を読む（回転補正済み・sRGB・RGBA・乗算しない。アニメーションは先頭のコマ）。読めなければ例外</summary>
    public static SkiaSharp.SKBitmap Load(string path, LoadOptions? options = null)
    {
        options ??= LoadOptions.Full;
        if (ImageFormats.IsImageSharpFormat(path))
        {
            SkiaSharp.SKBitmap? bitmap;
            try
            {
                bitmap = SkiaDecoder.TryDecode(path, options.MaxEdge) ?? FitTo(SimpleFormats.TryDecode(path), options.MaxEdge);
            }
            catch (Exception ex) when (IsDecodeFailure(ex) && ImageFormats.IsWicFormat(path))
            {
                // Skia が画素を読めない亜種（CMYK の一部など）は WIC に任せる
                return LoadWithWic(path, options);
            }
            // 中身が Skia・自前の形式でないもの（拡張子を .qoi などに変えた TIFF）は、拡張子に関わらず WIC で読み直す
            return bitmap ?? LoadWithWic(path, options);
        }
        if (ImageFormats.IsWicFormat(path)) return LoadWithWic(path, options);
        throw new NotSupportedException($"この形式は読み込めません: {Path.GetExtension(path)}");
    }

    /// <summary>長辺が maxEdge を超えていれば縮める（元は捨てる）</summary>
    private static SkiaSharp.SKBitmap? FitTo(SkiaSharp.SKBitmap? bitmap, int? maxEdge)
    {
        if (bitmap == null || maxEdge is not int edge || Math.Max(bitmap.Width, bitmap.Height) <= edge) return bitmap;
        double scale = (double)edge / Math.Max(bitmap.Width, bitmap.Height);
        using (bitmap)
            return SkiaDecoder.Resize(bitmap, Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)));
    }

    /// <summary>
    /// 編集用に ImageSharp の画像として読む（メタデータは付けない。保存するときに元のファイルから読み直す）。
    /// 移行中（#81）だけ使う。編集の処理を SKBitmap に移したら消す（手順7）
    /// </summary>
    internal static Image<Rgba32> LoadForEdit(string path, LoadOptions? options = null)
    {
        using var bitmap = Load(path, options);
        return ImageSharpBridge.ToImageSharp(bitmap);
    }

    /// <summary>
    /// 画像の大きさと形式を、全体をデコードせずにヘッダーだけ読んで調べる。
    /// EXIF の回転（90° / 270°）があれば縦横を入れ替えた見た目どおりの値。読めなければ null
    /// </summary>
    public static ImageHeader? Identify(string path)
    {
        try
        {
            bool unknownContent = false;
            if (ImageFormats.IsImageSharpFormat(path))
            {
                using (var codec = SkiaDecoder.TryOpen(path))
                {
                    if (codec != null)
                    {
                        int w = codec.Info.Width, h = codec.Info.Height;
                        string skiaFormat = SkiaDecoder.FormatName(codec.EncodedFormat);
                        return (SkiaDecoder.SwapsAxes(codec.EncodedOrigin) ? new ImageHeader(h, w, skiaFormat) : new ImageHeader(w, h, skiaFormat))
                            with { Photo = PhotoInfo.Read(path) };
                    }
                }
                // QOI・PNM（撮影情報は持たない）
                if (SimpleFormats.Identify(path) is var (format, width, height)) return new ImageHeader(width, height, format);
                // 中身が Skia・自前の形式でなければ、拡張子に関わらず WIC で（Load と同じ）
                unknownContent = true;
            }
            if (!unknownContent && !ImageFormats.IsWicFormat(path)) return null;

            using var stream = ImageSource.OpenRead(path);
            var decoder = Wpf.BitmapDecoder.Create(stream,
                Wpf.BitmapCreateOptions.DelayCreation | Wpf.BitmapCreateOptions.IgnoreColorProfile, Wpf.BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            // HEIF 系はデコーダが回転済みの大きさを返す（ImageLoader.Load と同じ扱い）
            ushort orientation = IsHeif(decoder, path) ? (ushort)1 : ReadOrientation(frame);
            string fmt = WicFormat(decoder, path);
            return (Rotated(orientation)
                ? new ImageHeader(frame.PixelHeight, frame.PixelWidth, fmt)
                : new ImageHeader(frame.PixelWidth, frame.PixelHeight, fmt)) with { Photo = ReadWicPhoto(frame) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or InvalidDataException
                                       or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// WIC で読む形式（HEIC・RAW 等）の撮影情報。形式ごとに EXIF の置き場所が違うので、
    /// WIC が共通の形で出してくれる撮影日時・カメラだけを読む（読めなければ null）
    /// </summary>
    private static PhotoInfo? ReadWicPhoto(Wpf.BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not Wpf.BitmapMetadata meta) return null;
            var info = new PhotoInfo
            {
                TakenAt = DateTime.TryParse(meta.DateTaken, out var d) ? d : null,
                Camera = PhotoInfo.CameraName(meta.CameraManufacturer, meta.CameraModel),
            };
            return info.IsEmpty ? null : info;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>EXIF の Orientation 5〜8 は 90° / 270° の回転を含む（縦横が入れ替わる）</summary>
    private static bool Rotated(ushort orientation) => orientation is >= 5 and <= 8;

    private static string FormatFromExtension(string path) => Path.GetExtension(path).TrimStart('.').ToUpperInvariant() switch
    {
        "JPG" or "JPEG" or "JFIF" => "JPEG",
        "TIF" => "TIFF",
        "HEIF" or "HIF" => "HEIC",
        var e => e,
    };

    /// <summary>
    /// WIC が選んだデコーダの形式名。拡張子がそのデコーダのものなら拡張子から（RAW のデコーダは多くの拡張子を持つので、
    /// デコーダの名前より拡張子のほうが正確）、違えば（拡張子を変えた TIFF など）デコーダの最初の拡張子から
    /// </summary>
    private static string WicFormat(Wpf.BitmapDecoder decoder, string path)
    {
        var exts = DecoderExtensions(decoder);
        if (exts is { Length: > 0 } && !exts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return FormatFromExtension(exts[0]);
        return FormatFromExtension(path);
    }

    /// <summary>
    /// HEIF 系（HEIC / AVIF）のデコーダが選ばれたか。拡張子を変えた HEIC も、選ばれたデコーダで判断する。
    /// デコーダの情報を読めなければ拡張子で
    /// </summary>
    private static bool IsHeif(Wpf.BitmapDecoder decoder, string path) =>
        DecoderExtensions(decoder) is { Length: > 0 } exts
            ? exts.Any(HeifExtensions.Contains)
            : HeifExtensions.Contains(Path.GetExtension(path));

    /// <summary>WIC が選んだデコーダの拡張子（".tiff" 形式）。読めなければ null</summary>
    private static string[]? DecoderExtensions(Wpf.BitmapDecoder decoder)
    {
        try
        {
            return decoder.CodecInfo?.FileExtensions?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// 中身が Skia か自前で読む形式か（ヘッダーだけ読む）。拡張子を変えた TIFF などは false（WIC で読むので画素だけになる）。
    /// ファイルを開けなければ拡張子で判断する
    /// </summary>
    public static bool IsBuiltInContent(string path)
    {
        if (!ImageFormats.IsImageSharpFormat(path)) return false;
        try
        {
            using (var codec = SkiaDecoder.TryOpen(path))
                if (codec != null) return true;
            return SimpleFormats.Detect(path) != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// 読めなかった例外か（WIC で読み直す候補）。InvalidDataException は Skia・自前の読み込みが画素を読めなかったとき（CMYK の亜種など）
    /// </summary>
    private static bool IsDecodeFailure(Exception ex) => ex is NotSupportedException or InvalidDataException;

    /// <summary>
    /// 編集した画像を保存するときに書くメタデータ（撮影情報（EXIF））を、元のファイルから読み直す。
    /// 向き・縦横・縮小画像は、保存するときに画像に合わせて直す（ExifBlock.ForSave）。
    /// 画素は読むときに sRGB に変換してあるので、色プロファイル（ICC）は書かない。XMP・IPTC も書かない（#81 の決定）。
    /// 中身が Skia・自前の形式でない（TIFF・HEIC など WIC で読むもの）なら null（撮影情報を残せない。画素だけを保存する）
    /// </summary>
    internal static SaveMetadata? MetadataForSave(string path) =>
        IsBuiltInContent(path) ? new SaveMetadata(ExifBlock.Read(path)) : null;

    // ---- WIC（WPF の画像 API 経由） ----

    private static SkiaSharp.SKBitmap LoadWithWic(string path, LoadOptions options)
    {
        using var stream = ImageSource.OpenRead(path);
        // CacheOption.None: デコードは下の CopyPixels 時に（縮小後のサイズで）行われる。stream はそれまで開いておく
        var decoder = Wpf.BitmapDecoder.Create(stream,
            Wpf.BitmapCreateOptions.PreservePixelFormat | Wpf.BitmapCreateOptions.IgnoreColorProfile,
            Wpf.BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        // HEIF 系は回転をコンテナ（irot / imir）で持ち、Windows のデコーダが適用済みで返す。
        // HEIF の仕様上 EXIF の Orientation は無視すべきもので、適用すると iPhone の写真等が二重に回転する
        ushort orientation = IsHeif(decoder, path) ? (ushort)1 : ReadOrientation(frame);

        Wpf.BitmapSource source = ToSrgb(frame);
        if (options.MaxEdge is int maxEdge && Math.Max(source.PixelWidth, source.PixelHeight) > maxEdge)
        {
            double scale = (double)maxEdge / Math.Max(source.PixelWidth, source.PixelHeight);
            source = new Wpf.TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        if (source.Format != PixelFormats.Bgra32)
            source = new Wpf.FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int w = source.PixelWidth, h = source.PixelHeight;
        // WIC の Bgra32 は乗算しない BGRA。Skia の BGRA で受けて、RGBA に並べ替える（値は変わらない）
        using var bgra = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul));
        source.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h), bgra.GetPixels(), bgra.ByteCount, bgra.RowBytes);
        var rgba = SkiaDecoder.Convert(bgra, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        // EXIF の Orientation（1〜8）は Skia の向きと同じ番号
        return orientation is > 1 and <= 8 ? SkiaDecoder.Orient(rgba, (SkiaSharp.SKEncodedOrigin)orientation) : rgba;
    }

    /// <summary>WIC で読んだときのコマ・ページの数（ヘッダーだけ読む）。WIC で読めない・読めなければ null</summary>
    public static int? WicFrameCount(string path)
    {
        if (!ImageFormats.IsWicFormat(path)) return null;
        try
        {
            using var stream = ImageSource.OpenRead(path);
            var decoder = Wpf.BitmapDecoder.Create(stream,
                Wpf.BitmapCreateOptions.DelayCreation | Wpf.BitmapCreateOptions.IgnoreColorProfile, Wpf.BitmapCacheOption.None);
            return decoder.Frames.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                       or ArgumentException or System.Runtime.InteropServices.COMException or FileFormatException
                                       or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>埋め込みの色プロファイル（iPhone の HEIC は Display P3 等）があれば sRGB に変換する</summary>
    private static Wpf.BitmapSource ToSrgb(Wpf.BitmapFrame frame)
    {
        try
        {
            if (frame.ColorContexts is { Count: > 0 } contexts)
                return new Wpf.ColorConvertedBitmap(frame, contexts[0],
                    new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            // 変換できないプロファイル（CMYK・壊れたもの等）は無変換で表示する
        }
        return frame;
    }

    /// <summary>EXIF の Orientation（1〜8）。読めなければ 1（補正なし）</summary>
    private static ushort ReadOrientation(Wpf.BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is Wpf.BitmapMetadata meta
                && meta.GetQuery("System.Photo.Orientation") is ushort o)
                return o;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException
                                       or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // メタデータを持たない・読めない形式
        }
        return 1;
    }
}
