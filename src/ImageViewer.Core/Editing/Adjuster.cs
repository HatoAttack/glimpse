// 色調補正: 色温度 → レベル補正（黒点・白点・ガンマ）→ 明るさ → コントラスト → 彩度 の順に、画像全体へかける
// 1 枚表示のプレビュー（BGRA の画素の並び）と保存（ImageSharp の画像）の両方で同じ計算を使う
using ImageViewer.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageViewer.Core.Editing;

/// <summary>読んだ形式のせいで EXIF などのメタデータを残して保存できない（HEIC / RAW などを WIC で読んだとき）</summary>
public sealed class MetadataLossException(string path) : NotSupportedException($"撮影情報（EXIF など）を残して保存できない画像です: {Path.GetFileName(path)}")
{
    public string ImagePath { get; } = path;
}

/// <summary>補正の値。既定値（new()）なら何も変えない</summary>
public sealed record AdjustOptions
{
    public const int MinAmount = -100, MaxAmount = 100;
    public const double MinGamma = 0.1, MaxGamma = 10;

    /// <summary>明るさ（-100〜100）。黒と白は動かさず、中間の明るさを上げ下げする</summary>
    public int Brightness { get; init; }

    /// <summary>コントラスト（-100〜100）。中間の灰色を中心に広げる / 狭める</summary>
    public int Contrast { get; init; }

    /// <summary>彩度（-100〜100）。-100 で白黒</summary>
    public int Saturation { get; init; }

    /// <summary>色温度（-100〜100）。+ で暖かい色み（赤みを足し青みを引く）、- で冷たい色み</summary>
    public int Temperature { get; init; }

    /// <summary>レベル補正の黒点（0〜254）。これより暗いところは黒になる</summary>
    public int BlackPoint { get; init; }

    /// <summary>レベル補正の白点（1〜255、黒点より大きい）。これより明るいところは白になる</summary>
    public int WhitePoint { get; init; } = 255;

    /// <summary>レベル補正のガンマ（0.1〜10）。1 より大きいと中間が明るくなる</summary>
    public double Gamma { get; init; } = 1;

    /// <summary>何も変えない値か</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsIdentity => Normalize() == new AdjustOptions();

    /// <summary>範囲外の値を丸めたもの（設定ファイルを手で書き換えられても壊れないように）</summary>
    public AdjustOptions Normalize()
    {
        int black = Math.Clamp(BlackPoint, 0, 254);
        return this with
        {
            Brightness = Math.Clamp(Brightness, MinAmount, MaxAmount),
            Contrast = Math.Clamp(Contrast, MinAmount, MaxAmount),
            Saturation = Math.Clamp(Saturation, MinAmount, MaxAmount),
            Temperature = Math.Clamp(Temperature, MinAmount, MaxAmount),
            BlackPoint = black,
            WhitePoint = Math.Clamp(WhitePoint, black + 1, 255),
            Gamma = double.IsFinite(Gamma) ? Math.Round(Math.Clamp(Gamma, MinGamma, MaxGamma), 2) : 1,
        };
    }
}

public static class Adjuster
{
    /// <summary>自動補正で黒・白とみなす画素の割合（両端のわずかな点に引っぱられないように）</summary>
    public const double AutoClip = 0.005;

    /// <summary>色温度 ±100 での赤・青の倍率の変わり幅（+100 で赤 1.3 倍・青 0.7 倍）</summary>
    public const double TemperatureStrength = 0.3;

    /// <summary>自動補正のガンマの範囲（強くかけすぎない）</summary>
    public const double AutoMinGamma = 0.5, AutoMaxGamma = 2;

    /// <summary>
    /// 明るさ・コントラスト・レベル補正を 1 つにまとめた表（入力 0〜255 → 出力 0〜255）。
    /// 3 つとも R・G・B に同じようにかかるので、画素ごとの計算は表を引くだけになる
    /// </summary>
    public static byte[] BuildToneTable(AdjustOptions options)
    {
        var o = options.Normalize();
        double range = o.WhitePoint - o.BlackPoint;
        double k = o.Brightness / 100.0;
        // 0 → 1 倍、100 → 4 倍、-100 → 1/4 倍（中間の灰色を中心に）
        double contrast = Math.Pow(2, o.Contrast / 50.0);
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double x = Math.Clamp((i - o.BlackPoint) / range, 0, 1);
            x = Math.Pow(x, 1 / o.Gamma);
            // x + k·x(1−x): 0 と 1 は動かさず、|k| ≤ 1 なら明るさの順は入れ替わらない
            x += k * x * (1 - x);
            x = (x - 0.5) * contrast + 0.5;
            table[i] = (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
        }
        return table;
    }

    /// <summary>
    /// R・G・B それぞれの表。色温度（赤と青の倍率）をかけてから、明るさ・コントラスト・レベル補正の表を引く
    /// </summary>
    public static (byte[] R, byte[] G, byte[] B) BuildChannelTables(AdjustOptions options)
    {
        var o = options.Normalize();
        var tone = BuildToneTable(o);
        if (o.Temperature == 0) return (tone, tone, tone);
        double t = o.Temperature / 100.0 * TemperatureStrength;
        return (Scaled(tone, 1 + t), tone, Scaled(tone, 1 - t));

        static byte[] Scaled(byte[] tone, double gain)
        {
            var table = new byte[256];
            for (int i = 0; i < 256; i++) table[i] = tone[(int)Math.Round(Math.Clamp(i * gain, 0, 255))];
            return table;
        }
    }

    /// <summary>画像に補正をかける（透明度はそのまま）</summary>
    public static void Apply(Image<Rgba32> image, AdjustOptions options)
    {
        if (options.IsIdentity) return;
        var (rt, gt, bt) = BuildChannelTables(options);
        int saturation = SaturationFactor(options);
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                foreach (ref var p in rows.GetRowSpan(y))
                    AdjustPixel(ref p.R, ref p.G, ref p.B, rt, gt, bt, saturation);
            }
        });
    }

    /// <summary>
    /// BGRA（1 画素 4 バイト、Windows のビットマップの並び）の画素に補正をかける。
    /// stride は 1 行のバイト数（行の終わりに詰め物があってもよい）
    /// </summary>
    public static void ApplyBgra(Span<byte> pixels, int width, int height, int stride, AdjustOptions options)
    {
        if (options.IsIdentity) return;
        var (rt, gt, bt) = BuildChannelTables(options);
        int saturation = SaturationFactor(options);
        for (int y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            for (int x = 0; x < row.Length; x += 4)
                AdjustPixel(ref row[x + 2], ref row[x + 1], ref row[x], rt, gt, bt, saturation);
        }
    }

    /// <summary>
    /// ファイルを読み、補正をかけて保存する（回転は反映する）。アニメや複数ページの画像（コマが 2 つ以上）は NotSupportedException。ImageSharp で読めた画像は EXIF などのメタデータを残す。
    /// WIC で読んだ画像（HEIC / AVIF / RAW や、ImageSharp で読めない JPEG の亜種）は画素だけなので残せない
    /// </summary>
    /// <param name="allowMetadataLoss">
    /// false なら、メタデータを残せないときは保存せずに MetadataLossException（ユーザーに確かめてから true で呼び直す）
    /// </param>
    public static void ApplyToFile(string src, string dst, AdjustOptions options, bool overwrite = true, bool allowMetadataLoss = false)
    {
        // 読むのは先頭のコマだけなので、アニメや複数ページの TIFF を保存すると残りが消えてしまう。受けない
        int? frames = FrameCount(src);
        if (frames > 1) throw new NotSupportedException(MultiFrameMessage);
        // 数えられなかった画像は、ページが消えるかもしれないので上書きしない（別のファイルに書くならよい）
        if (frames == null && string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("ページの数を確かめられない画像なので、上書きしませんでした");
        using var image = ImageLoader.Load(src); // 回転補正済み
        if (!allowMetadataLoss && !KeepsMetadata(image)) throw new MetadataLossException(src);
        Apply(image, options);
        ImageSaver.Save(image, dst, overwrite);
    }

    public const string MultiFrameMessage = "アニメーションや複数ページの画像は補正できません（保存すると先頭の 1 枚だけになるため）";

    /// <summary>
    /// コマ・ページの数（ヘッダーだけ読む）。GIF / WEBP のアニメ、複数ページの TIFF など。
    /// ImageSharp で数えられなければ WIC で数える（ImageSharp で読めない TIFF の亜種・HEIC など）。どちらでも数えられなければ null
    /// </summary>
    public static int? FrameCount(string path)
    {
        if (ImageFormats.IsImageSharpFormat(path))
        {
            try
            {
                return Math.Max(1, Image.Identify(path).FrameMetadataCollection.Count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                           or UnknownImageFormatException or InvalidImageContentException)
            {
                // WIC で数え直す
            }
        }
        return ImageLoader.WicFrameCount(path);
    }

    /// <summary>読み込んだ画像がメタデータを持っているか（ImageSharp で読めたか。WIC で読んだものは画素だけ）</summary>
    public static bool KeepsMetadata(Image image) => image.Metadata.DecodedImageFormat != null;

    /// <summary>
    /// 自動補正の値（レベル補正だけを決め、ほかは 0）。明るさの分布の両端 AutoClip ずつを黒・白にし、
    /// 真ん中の明るさが中間の灰色になるようにガンマを決める（AutoMinGamma〜AutoMaxGamma の範囲で）
    /// </summary>
    public static AdjustOptions Auto(Image<Rgba32> image)
    {
        var histogram = new long[256];
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                foreach (ref var p in rows.GetRowSpan(y))
                    if (p.A != 0) histogram[Luma(p.R, p.G, p.B)]++;
            }
        });
        return AutoFromHistogram(histogram);
    }

    /// <summary>BGRA の画素から自動補正の値を決める（Auto と同じ決め方）</summary>
    public static AdjustOptions AutoBgra(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var histogram = new long[256];
        for (int y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            for (int x = 0; x < row.Length; x += 4)
                if (row[x + 3] != 0) histogram[Luma(row[x + 2], row[x + 1], row[x])]++;
        }
        return AutoFromHistogram(histogram);
    }

    /// <summary>明るさの分布（256 段階）から自動補正の値を決める</summary>
    public static AdjustOptions AutoFromHistogram(IReadOnlyList<long> histogram)
    {
        long total = histogram.Sum();
        if (total == 0) return new AdjustOptions();
        long clip = (long)(total * AutoClip);
        int black = Percentile(histogram, clip);
        int white = Percentile(histogram, total - 1 - clip);
        if (white - black < 8) return new AdjustOptions(); // ほぼ一色の画像は伸ばすと荒れるだけなので触らない

        double median = (Percentile(histogram, total / 2) - black) / (double)(white - black);
        double gamma = median is > 0 and < 1 ? Math.Log(median) / Math.Log(0.5) : 1;
        return new AdjustOptions
        {
            BlackPoint = black,
            WhitePoint = white,
            Gamma = Math.Clamp(gamma, AutoMinGamma, AutoMaxGamma),
        }.Normalize();
    }

    /// <summary>小さい方から数えて rank 番目（0 始まり）の画素の明るさ</summary>
    private static int Percentile(IReadOnlyList<long> histogram, long rank)
    {
        long seen = 0;
        for (int i = 0; i < 256; i++)
        {
            seen += histogram[i];
            if (seen > rank) return i;
        }
        return 255;
    }

    /// <summary>彩度の倍率（1024 = 1 倍）。-100 → 0 倍（白黒）、100 → 2 倍</summary>
    private static int SaturationFactor(AdjustOptions options) =>
        (int)Math.Round((1 + options.Normalize().Saturation / 100.0) * 1024);

    /// <summary>明るさ（BT.709 の重み。整数で計算）</summary>
    private static int Luma(int r, int g, int b) => (r * 218 + g * 732 + b * 74 + 512) >> 10;

    private static void AdjustPixel(ref byte r, ref byte g, ref byte b, byte[] rt, byte[] gt, byte[] bt, int saturation)
    {
        int tr = rt[r], tg = gt[g], tb = bt[b];
        if (saturation != 1024)
        {
            // 明るさを保ったまま、灰色からの離れ具合を倍率で変える
            int l = Luma(tr, tg, tb);
            tr = l + (((tr - l) * saturation) >> 10);
            tg = l + (((tg - l) * saturation) >> 10);
            tb = l + (((tb - l) * saturation) >> 10);
        }
        r = (byte)Math.Clamp(tr, 0, 255);
        g = (byte)Math.Clamp(tg, 0, 255);
        b = (byte)Math.Clamp(tb, 0, 255);
    }
}
