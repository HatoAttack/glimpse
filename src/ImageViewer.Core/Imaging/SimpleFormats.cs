// 仕様の小さな形式の、自前の読み書き（#81 の決定。Skia にも WIC にも無い形式）
// - QOI（https://qoiformat.org/qoi-specification.pdf）: 読み書き
// - PNM（PBM・PGM・PPM。P1〜P6）: 読むだけ（今までも書き出してはいない）
// 返す画像は ImageLoader と同じく RGBA・乗算しない SKBitmap
using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace ImageViewer.Core.Imaging;

internal static class SimpleFormats
{
    /// <summary>中身が QOI か PNM なら、その名前（"QOI" / "PNM"）。どちらでもなければ null（先頭の数バイトだけ見る）</summary>
    public static string? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 14 && head.StartsWith("qoif"u8)) return "QOI";
        if (head.Length >= 3 && head[0] == (byte)'P' && head[1] is >= (byte)'1' and <= (byte)'6' && IsSpace(head[2])) return "PNM";
        return null;
    }

    public static string? Detect(string path)
    {
        using var stream = ImageSource.OpenRead(path);
        var head = new byte[14];
        int n = 0;
        while (n < head.Length)
        {
            int read = stream.Read(head, n, head.Length - n);
            if (read == 0) break;
            n += read;
        }
        return Detect(head.AsSpan(0, n));
    }

    /// <summary>大きさだけを読む（形式の名前と縦横）。QOI でも PNM でもなければ null</summary>
    public static (string Format, int Width, int Height)? Identify(string path)
    {
        string? format = Detect(path);
        if (format == null) return null;
        var data = ReadHead(path, format == "QOI" ? 14 : 4096);
        if (format == "QOI")
            return ("QOI", (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)), int.MaxValue),
                (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)), int.MaxValue));
        var header = PnmHeader(data, out _);
        return (PnmName(header.Kind), header.Width, header.Height);
    }

    /// <summary>QOI か PNM なら読む。どちらでもなければ null。形がおかしければ InvalidDataException</summary>
    public static SKBitmap? TryDecode(string path)
    {
        string? format = Detect(path);
        if (format == null) return null;
        byte[] data;
        using (var stream = ImageSource.OpenRead(path))
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            data = memory.ToArray();
        }
        try
        {
            return format == "QOI" ? DecodeQoi(data) : DecodePnm(data);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException or FormatException)
        {
            throw new InvalidDataException($"{format} として読めません: {Path.GetFileName(path)}", ex);
        }
    }

    private static byte[] ReadHead(string path, int max)
    {
        using var stream = ImageSource.OpenRead(path);
        var buffer = new byte[max];
        int n = 0;
        while (n < max)
        {
            int read = stream.Read(buffer, n, max - n);
            if (read == 0) break;
            n += read;
        }
        return buffer.AsSpan(0, n).ToArray();
    }

    /// <summary>読める大きさの上限（壊れたヘッダーで巨大な画像を作ろうとしないように）</summary>
    private const long MaxPixels = 400_000_000;

    private static SKBitmap NewBitmap(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new InvalidDataException($"画像の大きさがおかしい（{width}×{height}）");
        return new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    }

    // ---- QOI ----

    private const byte OpIndex = 0x00, OpDiff = 0x40, OpLuma = 0x80, OpRun = 0xC0, OpRgb = 0xFE, OpRgba = 0xFF, Mask2 = 0xC0;

    private static int Hash(byte r, byte g, byte b, byte a) => (r * 3 + g * 5 + b * 7 + a * 11) % 64;

    public static SKBitmap DecodeQoi(byte[] data)
    {
        if (data.Length < 14 + 8 || !data.AsSpan().StartsWith("qoif"u8)) throw new InvalidDataException("QOI ではありません");
        uint w = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)), h = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        if (w > int.MaxValue || h > int.MaxValue) throw new InvalidDataException("画像の大きさがおかしい");
        var bitmap = NewBitmap((int)w, (int)h);
        try
        {
            var px = bitmap.GetPixelSpan();
            Span<byte> index = stackalloc byte[64 * 4];
            index.Clear();
            byte r = 0, g = 0, b = 0, a = 255;
            int p = 14, run = 0, end = data.Length - 8; // 最後の 8 バイトは終わりの印
            for (int o = 0; o < px.Length; o += 4)
            {
                if (run > 0) run--;
                else if (p < end)
                {
                    byte op = data[p++];
                    if (op == OpRgb)
                    {
                        r = data[p++];
                        g = data[p++];
                        b = data[p++];
                    }
                    else if (op == OpRgba)
                    {
                        r = data[p++];
                        g = data[p++];
                        b = data[p++];
                        a = data[p++];
                    }
                    else if ((op & Mask2) == OpIndex)
                    {
                        int i = (op & 0x3F) * 4;
                        r = index[i];
                        g = index[i + 1];
                        b = index[i + 2];
                        a = index[i + 3];
                    }
                    else if ((op & Mask2) == OpDiff)
                    {
                        r += (byte)(((op >> 4) & 3) - 2);
                        g += (byte)(((op >> 2) & 3) - 2);
                        b += (byte)((op & 3) - 2);
                    }
                    else if ((op & Mask2) == OpLuma)
                    {
                        byte next = data[p++];
                        int vg = (op & 0x3F) - 32;
                        g += (byte)vg;
                        r += (byte)(vg - 8 + ((next >> 4) & 0x0F));
                        b += (byte)(vg - 8 + (next & 0x0F));
                    }
                    else run = op & 0x3F; // OpRun（この画素の分を除いた残りの数）
                    int k = Hash(r, g, b, a) * 4;
                    index[k] = r;
                    index[k + 1] = g;
                    index[k + 2] = b;
                    index[k + 3] = a;
                }
                // データが途中で終わっていたら、残りは最後の色のまま（読めた所までの絵にする）
                px[o] = r;
                px[o + 1] = g;
                px[o + 2] = b;
                px[o + 3] = a;
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        return bitmap;
    }

    /// <summary>QOI で書く（RGBA。透明が無ければ RGB と記録する。色空間は sRGB）</summary>
    public static byte[] EncodeQoi(SKBitmap image)
    {
        using var rgba = image.ColorType == SKColorType.Rgba8888 && image.AlphaType is SKAlphaType.Unpremul or SKAlphaType.Opaque && image.RowBytes == image.Width * 4
            ? null : SkiaDecoder.Convert(image, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var px = (rgba ?? image).GetPixelSpan();
        bool opaque = true;
        for (int i = 3; i < px.Length; i += 4)
            if (px[i] != 255)
            {
                opaque = false;
                break;
            }

        using var output = new MemoryStream(14 + px.Length / 2 + 8);
        Span<byte> header = stackalloc byte[14];
        "qoif"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)image.Height);
        header[12] = (byte)(opaque ? 3 : 4);
        header[13] = 0; // sRGB（直線でない）
        output.Write(header);

        Span<byte> index = stackalloc byte[64 * 4];
        index.Clear();
        byte pr = 0, pg = 0, pb = 0, pa = 255;
        int run = 0;
        for (int o = 0; o < px.Length; o += 4)
        {
            byte r = px[o], g = px[o + 1], b = px[o + 2], a = px[o + 3];
            if (r == pr && g == pg && b == pb && a == pa)
            {
                run++;
                if (run == 62 || o + 4 == px.Length)
                {
                    output.WriteByte((byte)(OpRun | (run - 1)));
                    run = 0;
                }
                continue;
            }
            if (run > 0)
            {
                output.WriteByte((byte)(OpRun | (run - 1)));
                run = 0;
            }
            int k = Hash(r, g, b, a) * 4;
            if (index[k] == r && index[k + 1] == g && index[k + 2] == b && index[k + 3] == a)
                output.WriteByte((byte)(OpIndex | (k / 4)));
            else
            {
                index[k] = r;
                index[k + 1] = g;
                index[k + 2] = b;
                index[k + 3] = a;
                if (a == pa)
                {
                    int vr = (sbyte)(byte)(r - pr), vg = (sbyte)(byte)(g - pg), vb = (sbyte)(byte)(b - pb);
                    int vgr = vr - vg, vgb = vb - vg;
                    if (vr is > -3 and < 2 && vg is > -3 and < 2 && vb is > -3 and < 2)
                        output.WriteByte((byte)(OpDiff | ((vr + 2) << 4) | ((vg + 2) << 2) | (vb + 2)));
                    else if (vgr is > -9 and < 8 && vg is > -33 and < 32 && vgb is > -9 and < 8)
                    {
                        output.WriteByte((byte)(OpLuma | (vg + 32)));
                        output.WriteByte((byte)(((vgr + 8) << 4) | (vgb + 8)));
                    }
                    else
                    {
                        output.WriteByte(OpRgb);
                        output.WriteByte(r);
                        output.WriteByte(g);
                        output.WriteByte(b);
                    }
                }
                else
                {
                    output.WriteByte(OpRgba);
                    output.WriteByte(r);
                    output.WriteByte(g);
                    output.WriteByte(b);
                    output.WriteByte(a);
                }
            }
            (pr, pg, pb, pa) = (r, g, b, a);
        }
        output.Write(stackalloc byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });
        return output.ToArray();
    }

    // ---- PNM（P1〜P6） ----

    private readonly record struct PnmInfo(char Kind, int Width, int Height, int MaxValue);

    private static string PnmName(char kind) => kind switch
    {
        '1' or '4' => "PBM",
        '2' or '5' => "PGM",
        _ => "PPM",
    };

    private static bool IsSpace(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0B or 0x0C;

    /// <summary>ヘッダーを読む。dataStart は画素のデータの始まり（バイナリの形では、最後の値の後の空白 1 つを飛ばした所）</summary>
    private static PnmInfo PnmHeader(byte[] data, out int dataStart)
    {
        if (data.Length < 3 || data[0] != (byte)'P' || data[1] is < (byte)'1' or > (byte)'6') throw new InvalidDataException("PNM ではありません");
        char kind = (char)data[1];
        int p = 2;
        int width = NextNumber(data, ref p), height = NextNumber(data, ref p);
        int max = kind is '1' or '4' ? 1 : NextNumber(data, ref p);
        if (max is < 1 or > 65535) throw new InvalidDataException($"PNM の最大値がおかしい（{max}）");
        if (kind is >= '4' and <= '6')
        {
            if (p >= data.Length || !IsSpace(data[p])) throw new InvalidDataException("PNM のヘッダーの後に空白がありません");
            p++;
        }
        dataStart = p;
        return new PnmInfo(kind, width, height, max);
    }

    /// <summary>次の数（空白とコメント（# から行末まで）を飛ばす）</summary>
    private static int NextNumber(byte[] data, ref int p)
    {
        while (p < data.Length)
        {
            if (data[p] == (byte)'#')
                while (p < data.Length && data[p] != (byte)'\n' && data[p] != (byte)'\r') p++;
            else if (IsSpace(data[p])) p++;
            else break;
        }
        long value = 0;
        int start = p;
        while (p < data.Length && data[p] is >= (byte)'0' and <= (byte)'9')
        {
            value = value * 10 + (data[p] - '0');
            if (value > int.MaxValue) throw new InvalidDataException("PNM の数が大きすぎる");
            p++;
        }
        if (p == start) throw new InvalidDataException("PNM の数がありません");
        return (int)value;
    }

    public static SKBitmap DecodePnm(byte[] data)
    {
        var info = PnmHeader(data, out int p);
        var bitmap = NewBitmap(info.Width, info.Height);
        try
        {
            var px = bitmap.GetPixelSpan();
            int count = info.Width * info.Height;
            int channels = info.Kind is '3' or '6' ? 3 : 1;
            bool wide = info.MaxValue > 255;
            byte Scale(int v) => (byte)Math.Clamp((v * 255 + info.MaxValue / 2) / info.MaxValue, 0, 255);

            if (info.Kind == '4')
            {
                // 1 ビットの白黒（1 が黒）。行の終わりはバイトの区切りまで詰める
                int rowBytes = (info.Width + 7) / 8;
                for (int y = 0; y < info.Height; y++)
                    for (int x = 0; x < info.Width; x++)
                    {
                        int at = p + y * rowBytes + x / 8;
                        bool black = at < data.Length && ((data[at] >> (7 - x % 8)) & 1) == 1;
                        Put(px, y * info.Width + x, black ? (byte)0 : (byte)255);
                    }
                return bitmap;
            }

            var sample = new int[channels];
            for (int i = 0; i < count; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    if (info.Kind is '1' or '2' or '3')
                    {
                        if (info.Kind == '1')
                        {
                            // P1 は 0 / 1 が空白なしで続くこともある
                            while (p < data.Length && (IsSpace(data[p]) || data[p] == (byte)'#'))
                                if (data[p] == (byte)'#') while (p < data.Length && data[p] != (byte)'\n') p++;
                                else p++;
                            sample[c] = p < data.Length && data[p++] == (byte)'1' ? 0 : 1; // 1 が黒
                        }
                        else sample[c] = p < data.Length ? NextNumber(data, ref p) : 0;
                    }
                    else if (wide)
                    {
                        sample[c] = p + 1 < data.Length ? (data[p] << 8) | data[p + 1] : 0;
                        p += 2;
                    }
                    else sample[c] = p < data.Length ? data[p++] : 0;
                }
                int o = i * 4;
                if (info.Kind == '1') Put(px, i, (byte)(sample[0] * 255));
                else if (channels == 1) Put(px, i, Scale(sample[0]));
                else
                {
                    px[o] = Scale(sample[0]);
                    px[o + 1] = Scale(sample[1]);
                    px[o + 2] = Scale(sample[2]);
                    px[o + 3] = 255;
                }
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        return bitmap;
    }

    private static void Put(Span<byte> px, int i, byte gray)
    {
        int o = i * 4;
        px[o] = px[o + 1] = px[o + 2] = gray;
        px[o + 3] = 255;
    }
}
