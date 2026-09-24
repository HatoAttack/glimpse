// 画像読み込みの窓口。サムネイル・1枚表示・編集機能はすべてここ経由で読む
// - ImageSharp の形式: ImageSharp で読む（JPEG は縮小デコードが効く）。読めなければ WIC で再挑戦
// - それ以外で WIC にデコーダがある形式（HEIC / AVIF / RAW / JPEG XL 等）: WPF の画像 API（中身は WIC）で読む
// どちらの経路でも「回転補正済み・sRGB・Rgba32」の画像を返す
using System.Windows.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
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

    public static Image<Rgba32> Load(string path, LoadOptions? options = null)
    {
        options ??= LoadOptions.Full;
        if (ImageFormats.IsImageSharpFormat(path))
        {
            try
            {
                return LoadWithImageSharp(path, options);
            }
            catch (Exception ex) when (IsDecodeFailure(ex) && ImageFormats.IsWicFormat(path))
            {
                // ImageSharp が対応しない亜種（CMYK の一部など）は WIC に任せる
            }
        }
        if (ImageFormats.IsWicFormat(path)) return LoadWithWic(path, options);
        throw new NotSupportedException($"この形式は読み込めません: {Path.GetExtension(path)}");
    }

    /// <summary>
    /// 画像の大きさと形式を、全体をデコードせずにヘッダーだけ読んで調べる。
    /// EXIF の回転（90° / 270°）があれば縦横を入れ替えた見た目どおりの値。読めなければ null
    /// </summary>
    public static ImageHeader? Identify(string path)
    {
        try
        {
            if (ImageFormats.IsImageSharpFormat(path))
            {
                try
                {
                    var info = Image.Identify(path);
                    ushort o = info.Metadata.ExifProfile?.TryGetValue(ExifTag.Orientation, out var v) == true ? v.Value : (ushort)1;
                    string format = info.Metadata.DecodedImageFormat?.Name ?? FormatFromExtension(path);
                    var photo = PhotoInfo.FromExif(info.Metadata.ExifProfile);
                    return (Rotated(o) ? new ImageHeader(info.Height, info.Width, format) : new ImageHeader(info.Width, info.Height, format))
                        with { Photo = photo };
                }
                catch (Exception ex) when (IsDecodeFailure(ex) && ImageFormats.IsWicFormat(path))
                {
                    // ImageSharp で読めない亜種は WIC で
                }
            }
            if (!ImageFormats.IsWicFormat(path)) return null;

            using var stream = File.OpenRead(path);
            var decoder = Wpf.BitmapDecoder.Create(stream,
                Wpf.BitmapCreateOptions.DelayCreation | Wpf.BitmapCreateOptions.IgnoreColorProfile, Wpf.BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            // HEIF 系はデコーダが回転済みの大きさを返す（ImageLoader.Load と同じ扱い）
            ushort orientation = HeifExtensions.Contains(Path.GetExtension(path)) ? (ushort)1 : ReadOrientation(frame);
            string fmt = FormatFromExtension(path);
            return (Rotated(orientation)
                ? new ImageHeader(frame.PixelHeight, frame.PixelWidth, fmt)
                : new ImageHeader(frame.PixelWidth, frame.PixelHeight, fmt)) with { Photo = ReadWicPhoto(frame) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or UnknownImageFormatException or InvalidImageContentException or FileFormatException
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

    private static bool IsDecodeFailure(Exception ex) =>
        ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException;

    // ---- ImageSharp ----

    private static Image<Rgba32> LoadWithImageSharp(string path, LoadOptions options)
    {
        var decoderOptions = new DecoderOptions { MaxFrames = options.FirstFrameOnly ? 1 : uint.MaxValue };
        if (options.MaxEdge is int maxEdge)
        {
            // TargetSize は小さい画像を拡大してしまうので、上限を超えるときだけ指定する
            var info = Image.Identify(path);
            if (Math.Max(info.Width, info.Height) > maxEdge)
                decoderOptions = new DecoderOptions { MaxFrames = decoderOptions.MaxFrames, TargetSize = new Size(maxEdge, maxEdge) };
        }
        var image = Image.Load<Rgba32>(decoderOptions, path);
        image.Mutate(x => x.AutoOrient());
        // JPEG の縮小デコードは 1/2・1/4・1/8 単位なので、上限を超えた分はここで合わせる
        if (options.MaxEdge is int edge && Math.Max(image.Width, image.Height) > edge)
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(edge, edge), Mode = ResizeMode.Max }));
        return image;
    }

    // ---- WIC（WPF の画像 API 経由） ----

    private static Image<Rgba32> LoadWithWic(string path, LoadOptions options)
    {
        using var stream = File.OpenRead(path);
        // CacheOption.None: デコードは下の CopyPixels 時に（縮小後のサイズで）行われる。stream はそれまで開いておく
        var decoder = Wpf.BitmapDecoder.Create(stream,
            Wpf.BitmapCreateOptions.PreservePixelFormat | Wpf.BitmapCreateOptions.IgnoreColorProfile,
            Wpf.BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        // HEIF 系は回転をコンテナ（irot / imir）で持ち、Windows のデコーダが適用済みで返す。
        // HEIF の仕様上 EXIF の Orientation は無視すべきもので、適用すると iPhone の写真等が二重に回転する
        ushort orientation = HeifExtensions.Contains(Path.GetExtension(path)) ? (ushort)1 : ReadOrientation(frame);

        Wpf.BitmapSource source = ToSrgb(frame);
        if (options.MaxEdge is int maxEdge && Math.Max(source.PixelWidth, source.PixelHeight) > maxEdge)
        {
            double scale = (double)maxEdge / Math.Max(source.PixelWidth, source.PixelHeight);
            source = new Wpf.TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        if (source.Format != PixelFormats.Bgra32)
            source = new Wpf.FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        source.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]); // BGRA → RGBA

        var image = Image.LoadPixelData<Rgba32>(pixels, w, h);
        if (orientation is > 1 and <= 8)
        {
            // 回転・反転の補正は ImageSharp の AutoOrient に任せる（8通りすべて対応済み）
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);
            image.Mutate(x => x.AutoOrient());
        }
        image.Metadata.ExifProfile = null;
        return image;
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
