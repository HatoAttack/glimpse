// テスト用の画像。ImageSharp にも Skia にも頼らずに、テストの画像を作り、保存された結果を読んで確かめる
// - 画素は乗算しない RGBA の byte 配列で持つ
// - ファイルの読み書きは WIC（WPF の画像 API）で行う（PNG・JPEG・BMP・GIF・TIFF。WebP は読むだけ）
// - Core の API に渡す画像への変換は ToApi / FromApi の 1 か所だけにする（API の画像の型が変わったら、ここだけ直す）
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>画素の色（乗算しない RGBA）</summary>
readonly record struct Px(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Px Red = new(255, 0, 0), Green = new(0, 255, 0), Blue = new(0, 0, 255),
        White = new(255, 255, 255), Black = new(0, 0, 0), Transparent = new(0, 0, 0, 0);

    /// <summary>各チャンネルの差が tolerance 以内か（JPEG など、少し値が変わるもの用）</summary>
    public bool Near(Px o, int tolerance = 40) =>
        Math.Abs(R - o.R) <= tolerance && Math.Abs(G - o.G) <= tolerance && Math.Abs(B - o.B) <= tolerance && Math.Abs(A - o.A) <= tolerance;

    public override string ToString() => $"({R},{G},{B},{A})";
}

/// <summary>テストの画像に付ける EXIF（null の項目は付けない）</summary>
sealed record TestExif
{
    public ushort? Orientation { get; init; }
    public string? Make { get; init; }
    public string? Model { get; init; }
    public string? Artist { get; init; }
    /// <summary>撮影日時（"2026:09:10 10:21:05" の形）</summary>
    public string? Taken { get; init; }
    public string? Lens { get; init; }
    public (uint Num, uint Den)? FNumber { get; init; }
    public (uint Num, uint Den)? Exposure { get; init; }
    public ushort? Iso { get; init; }
}

sealed class TestImage
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>乗算しない RGBA。1 画素 4 バイト、行の間に隙間は無い</summary>
    public byte[] Rgba { get; }

    public TestImage(int width, int height, Px fill = default)
    {
        Width = width;
        Height = height;
        Rgba = new byte[width * height * 4];
        if (fill != default) Fill(fill);
    }

    private TestImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public Px this[int x, int y]
    {
        get { int i = (y * Width + x) * 4; return new Px(Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]); }
        set { int i = (y * Width + x) * 4; Rgba[i] = value.R; Rgba[i + 1] = value.G; Rgba[i + 2] = value.B; Rgba[i + 3] = value.A; }
    }

    public TestImage Fill(Px color) => Fill(color, 0, 0, Width, Height);

    public TestImage Fill(Px color, int x0, int y0, int w, int h)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(Height, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(Width, x0 + w); x++)
                this[x, y] = color;
        return this;
    }

    public TestImage Clone() => new(Width, Height, (byte[])Rgba.Clone());

    // ---- Core の API との受け渡し（ここだけが API の画像の型を知る） ----

    public Image<Rgba32> ToApi() => SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(Rgba, Width, Height);

    /// <summary>Core の API が返した画像（SKBitmap）。どの形式でも RGBA・乗算しない値に読み出す</summary>
    public static TestImage FromApi(SkiaSharp.SKBitmap bitmap)
    {
        var info = new SkiaSharp.SKImageInfo(bitmap.Width, bitmap.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        var rgba = new byte[info.BytesSize];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var pixmap = bitmap.PeekPixels();
            if (!pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0))
                throw new InvalidOperationException($"画素を読み出せません（{bitmap.ColorType}）");
        }
        finally
        {
            handle.Free();
        }
        return new TestImage(bitmap.Width, bitmap.Height, rgba);
    }

    /// <summary>Core の API が返した画像の frame 番目のコマ</summary>
    public static TestImage FromApi(Image<Rgba32> image, int frame)
    {
        using var one = image.Frames.CloneFrame(frame);
        return FromApi(one);
    }

    public static TestImage FromApi(Image<Rgba32> image)
    {
        var rgba = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(rgba);
        return new TestImage(image.Width, image.Height, rgba);
    }

    /// <summary>コミットしてあるテスト用の画像（Fixtures フォルダ）を dir に写したパス。テストで書き換えても元は変わらない</summary>
    public static string Fixture(string name, string dir)
    {
        string path = Path.Combine(dir, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), path, overwrite: true);
        return path;
    }

    // ---- 保存（WIC） ----

    /// <summary>拡張子の形式で保存する（png・jpg・jpeg・bmp・gif・tif・tiff）。JPEG は exif を付けられる</summary>
    public void Save(string path, TestExif? exif = null, int quality = 95) =>
        File.WriteAllBytes(path, Encode(Path.GetExtension(path), exif, quality));

    /// <summary>ext（".png" など）の形式のファイルの中身を作る（ZIP に入れるときなど）</summary>
    public byte[] Encode(string ext, TestExif? exif = null, int quality = 95)
    {
        ext = ext.ToLowerInvariant();
        if (exif != null && ext is not (".jpg" or ".jpeg"))
            throw new ArgumentException("EXIF を付けられるのは JPEG だけ", nameof(exif));
        BitmapEncoder encoder = ext switch
        {
            ".png" => new PngBitmapEncoder(),
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = quality },
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => throw new ArgumentException($"保存できない形式: {ext}", nameof(ext)),
        };
        // JPEG と BMP は透明を持てないので、不透明として渡す（そのまま渡すと、透明な所が黒くなるエンコーダがある）
        bool opaque = ext is ".jpg" or ".jpeg" or ".bmp";
        encoder.Frames.Add(BitmapFrame.Create(ToBitmapSource(opaque), null, exif == null ? null : JpegMetadata(exif), null));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 複数のコマ・ページを 1 つのファイルに保存する（GIF はコマごとの表示時間 1/100 秒、TIFF はページ）。
    /// GIF の繰り返し再生の印（NETSCAPE2.0）は WPF のエンコーダでは付けられないので付けない（読み込みには関係しない）
    /// </summary>
    public static void SaveFrames(string path, IReadOnlyList<TestImage> frames, IReadOnlyList<int>? gifDelays = null) =>
        File.WriteAllBytes(path, EncodeFrames(Path.GetExtension(path), frames, gifDelays));

    public static byte[] EncodeFrames(string ext, IReadOnlyList<TestImage> frames, IReadOnlyList<int>? gifDelays = null)
    {
        ext = ext.ToLowerInvariant();
        BitmapEncoder encoder = ext switch
        {
            ".gif" => new GifBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => throw new ArgumentException($"複数のコマを保存できない形式: {ext}", nameof(ext)),
        };
        foreach (var frame in frames)
            encoder.Frames.Add(BitmapFrame.Create(frame.ToBitmapSource(opaque: false)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        byte[] bytes = ms.ToArray();
        if (ext == ".gif" && gifDelays != null) PatchGifDelays(bytes, gifDelays);
        return bytes;
    }

    /// <summary>
    /// GIF のコマごとの表示時間を書き込む。WPF のエンコーダはコマごとの制御の欄（GCE）を書くが、表示時間は 0 のままにするので、
    /// 書き出した中身の GCE を前から順に書き換える
    /// </summary>
    private static void PatchGifDelays(byte[] d, IReadOnlyList<int> delays)
    {
        int i = 13, frame = 0;
        if ((d[10] & 0x80) != 0) i += 3 << ((d[10] & 7) + 1);
        while (i < d.Length && d[i] != 0x3B)
        {
            if (d[i] == 0x21)
            {
                if (d[i + 1] == 0xF9 && frame < delays.Count)
                {
                    d[i + 4] = (byte)(delays[frame] & 0xFF);
                    d[i + 5] = (byte)(delays[frame] >> 8);
                    frame++;
                }
                int j = i + 2;
                while (d[j] != 0) j += d[j] + 1;
                i = j + 1;
            }
            else if (d[i] == 0x2C)
            {
                int flags = d[i + 9];
                i += 10;
                if ((flags & 0x80) != 0) i += 3 << ((flags & 7) + 1);
                i++; // LZW の最小コード長
                while (d[i] != 0) i += d[i] + 1;
                i++;
            }
            else throw new InvalidDataException($"GIF の読み取りに失敗（{i} バイト目）");
        }
        if (frame != delays.Count) throw new InvalidDataException($"GIF の GCE が {frame} 個しか無い（コマは {delays.Count}）");
    }

    private static BitmapMetadata JpegMetadata(TestExif exif)
    {
        var meta = new BitmapMetadata("jpg");
        if (exif.Orientation is ushort o) meta.SetQuery("/app1/ifd/{ushort=274}", o);
        if (exif.Make != null) meta.SetQuery("/app1/ifd/{ushort=271}", exif.Make);
        if (exif.Model != null) meta.SetQuery("/app1/ifd/{ushort=272}", exif.Model);
        if (exif.Artist != null) meta.SetQuery("/app1/ifd/{ushort=315}", exif.Artist);
        if (exif.Taken != null) meta.SetQuery("/app1/ifd/exif/{ushort=36867}", exif.Taken);
        if (exif.Lens != null) meta.SetQuery("/app1/ifd/exif/{ushort=42036}", exif.Lens);
        // 有理数は WIC では 64 ビットの整数（下位 32 ビットが分子、上位 32 ビットが分母）
        if (exif.FNumber is var (fn, fd)) meta.SetQuery("/app1/ifd/exif/{ushort=33437}", Rational(fn, fd));
        if (exif.Exposure is var (en, ed)) meta.SetQuery("/app1/ifd/exif/{ushort=33434}", Rational(en, ed));
        if (exif.Iso is ushort iso) meta.SetQuery("/app1/ifd/exif/{ushort=34855}", iso);
        return meta;
    }

    private static ulong Rational(uint num, uint den) => ((ulong)den << 32) | num;

    private BitmapSource ToBitmapSource(bool opaque)
    {
        var bgra = new byte[Rgba.Length];
        for (int i = 0; i < Rgba.Length; i += 4)
        {
            bgra[i] = Rgba[i + 2];
            bgra[i + 1] = Rgba[i + 1];
            bgra[i + 2] = Rgba[i];
            bgra[i + 3] = opaque ? (byte)255 : Rgba[i + 3];
        }
        var source = BitmapSource.Create(Width, Height, 96, 96, opaque ? PixelFormats.Bgr32 : PixelFormats.Bgra32, null, bgra, Width * 4);
        source.Freeze();
        return source;
    }

    // ---- 読み込み（WIC） ----

    /// <summary>
    /// ファイルを読む（frame 番目のコマ・ページ）。EXIF の向きは直さない（ファイルに入っている画素のまま）。
    /// GIF のコマは重ね合わせない（そのコマに入っている画素だけ）
    /// </summary>
    public static TestImage Load(string path, int frame = 0)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[frame];
        if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight;
        var bgra = new byte[w * h * 4];
        source.CopyPixels(bgra, w * 4, 0);
        for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return new TestImage(w, h, bgra);
    }

    /// <summary>
    /// ファイルの縦横（ヘッダーだけ読む。EXIF の向きは反映しない）。読めなければ (-1, -1)。
    /// WebP はヘッダーを自分で読む（WebP のデコーダが入っていない Windows もあるため）
    /// </summary>
    public static (int Width, int Height) SizeOf(string path)
    {
        if (!File.Exists(path)) return (-1, -1);
        if (WebpSize(File.ReadAllBytes(path)) is { } webp) return webp;
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>WebP のヘッダーの縦横（VP8・VP8L・VP8X）。WebP でなければ null</summary>
    private static (int Width, int Height)? WebpSize(byte[] d)
    {
        if (d.Length < 30 || System.Text.Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(d, 8, 4) != "WEBP")
            return null;
        string chunk = System.Text.Encoding.ASCII.GetString(d, 12, 4);
        return chunk switch
        {
            // 拡張形式: 幅 - 1・高さ - 1 が 24 ビットずつ
            "VP8X" => (1 + (d[24] | d[25] << 8 | d[26] << 16), 1 + (d[27] | d[28] << 8 | d[29] << 16)),
            // 可逆: 署名 0x2F の後に 14 ビットずつ（幅 - 1・高さ - 1）
            "VP8L" => (1 + ((d[21] | d[22] << 8) & 0x3FFF), 1 + (((d[22] >> 6) | d[23] << 2 | d[24] << 10) & 0x3FFF)),
            // 非可逆: キーフレームの開始コードの後に 14 ビットずつ
            "VP8 " => ((d[26] | d[27] << 8) & 0x3FFF, (d[28] | d[29] << 8) & 0x3FFF),
            _ => null,
        };
    }

    /// <summary>
    /// ファイルに EXIF が入っているか（中身を直接見る）。JPEG の APP1「Exif」、PNG の eXIf、WebP の EXIF のどれか。
    /// メタデータを消したかどうかを、形式に関わらず確かめるため
    /// </summary>
    public static bool HasExif(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        bool Has(string s) => d.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(s)) >= 0;
        return Has("Exif\0\0") || Has("eXIf") || (WebpSize(d) != null && Has("EXIF"));
    }

    /// <summary>コマ・ページの数</summary>
    public static int FrameCount(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames.Count;
    }

    /// <summary>GIF のコマごとの表示時間（1/100 秒）</summary>
    public static List<int> GifDelays(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return decoder.Frames.Select(f => f.Metadata is BitmapMetadata m && m.GetQuery("/grctlext/Delay") is ushort d ? (int)d : 0).ToList();
    }

    /// <summary>JPEG の EXIF を読む（入っていない項目は null）。EXIF が無ければ null</summary>
    public static TestExif? ReadExif(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        if (frame.Metadata is not BitmapMetadata meta) return null;
        object? Q(string query)
        {
            try { return meta.ContainsQuery(query) ? meta.GetQuery(query) : null; }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException) { return null; }
        }
        if (Q("/app1/ifd") == null) return null;
        return new TestExif
        {
            Orientation = Q("/app1/ifd/{ushort=274}") as ushort?,
            Make = Q("/app1/ifd/{ushort=271}") as string,
            Model = Q("/app1/ifd/{ushort=272}") as string,
            Artist = Q("/app1/ifd/{ushort=315}") as string,
        };
    }
}
