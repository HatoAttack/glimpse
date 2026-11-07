// EXIF（撮影情報）をファイルから取り出し、保存するファイルに書き戻す（#81 の決定: 残すのは EXIF だけ。XMP・IPTC は残さない）
// - 取り出す: JPEG の APP1・PNG の eXIf・WebP の EXIF から、TIFF の形の EXIF（"II*\0" / "MM\0*" で始まるもの）をそのまま取り出す
// - 直す: 向きを「そのまま」（1）にし（画素は回転を直してあるので）、縦横の値を保存する画像の大きさにし、
//   埋め込みの縮小画像（IFD1）への参照を外す（編集前の絵のままなので、エクスプローラーなどが古い絵を出さないように）
// - 書き戻す: Skia が書いた JPEG・PNG・WebP に、EXIF の入れ物を足す（Skia のエンコーダはメタデータを書かない）
// 中身の値は変えずにバイト列のまま扱うので、メーカー独自の情報（MakerNote）の位置もずれない
using System.Buffers.Binary;
using System.Text;

namespace ImageViewer.Core.Imaging;

internal static class ExifBlock
{
    /// <summary>JPEG の APP1 で EXIF の前に付く印</summary>
    private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();

    /// <summary>JPEG の 1 つの区切り（APP1）に入る最大の長さ（長さの 2 バイトと印を除く）</summary>
    private const int MaxJpegExif = 65535 - 2 - 6;

    private const ushort TagOrientation = 0x0112, TagExifIfd = 0x8769, TagPixelX = 0xA002, TagPixelY = 0xA003;

    // ---- 取り出す ----

    /// <summary>ファイルの EXIF（TIFF の形）。JPEG・PNG・WebP 以外・EXIF が無い・読めなければ null</summary>
    public static byte[]? Read(string path)
    {
        try
        {
            using var stream = ImageSource.OpenRead(path);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            return null;
        }
    }

    public static byte[]? Read(Stream stream)
    {
        var head = new byte[12];
        int n = ReadUpTo(stream, head);
        if (n >= 2 && head[0] == 0xFF && head[1] == 0xD8) return ReadJpeg(stream, head, n);
        if (n >= 8 && head.AsSpan(0, 8).SequenceEqual(PngSignature)) return ReadPng(stream, head, n);
        if (n >= 12 && head.AsSpan(0, 4).SequenceEqual("RIFF"u8) && head.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return ReadWebp(stream);
        return null;
    }

    private static byte[]? ReadJpeg(Stream stream, byte[] head, int n)
    {
        // 先頭の 12 バイトは読んでしまったので、残りを続けて読む
        var input = new PrefixedStream(head.AsSpan(2, n - 2).ToArray(), stream);
        var two = new byte[2];
        while (true)
        {
            int b = input.ReadByte();
            if (b < 0) return null;
            if (b != 0xFF) continue; // 区切りの前の埋め草
            int marker;
            do marker = input.ReadByte(); while (marker == 0xFF);
            if (marker < 0 || marker == 0xD9 || marker == 0xDA) return null; // 画像の終わり・画素のデータの始まり（EXIF はその前にある）
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) continue; // 長さを持たない区切り
            if (!ReadExactly(input, two)) return null;
            int length = BinaryPrimitives.ReadUInt16BigEndian(two) - 2;
            if (length < 0) return null;
            if (marker == 0xE1 && length > ExifHeader.Length)
            {
                var payload = new byte[length];
                if (!ReadExactly(input, payload)) return null;
                if (payload.AsSpan(0, ExifHeader.Length).SequenceEqual(ExifHeader)) return Tiff(payload.AsSpan(ExifHeader.Length));
                continue; // XMP など、ほかの APP1
            }
            if (!Skip(input, length)) return null;
        }
    }

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static byte[]? ReadPng(Stream stream, byte[] head, int n)
    {
        var input = new PrefixedStream(head.AsSpan(8, n - 8).ToArray(), stream);
        var header = new byte[8];
        while (ReadExactly(input, header))
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            if (length > int.MaxValue - 4) return null;
            if (type == "eXIf")
            {
                var data = new byte[length];
                return ReadExactly(input, data) ? Tiff(data) : null;
            }
            if (type == "IEND" || !Skip(input, (long)length + 4)) return null; // データと CRC を飛ばす
        }
        return null;
    }

    private static byte[]? ReadWebp(Stream stream)
    {
        var header = new byte[8];
        while (ReadExactly(stream, header))
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (length > int.MaxValue - 1) return null;
            if (header.AsSpan(0, 4).SequenceEqual("EXIF"u8))
            {
                var data = new byte[length];
                if (!ReadExactly(stream, data)) return null;
                // 前に "Exif\0\0" を付けて書くソフトもある
                return data.AsSpan().StartsWith(ExifHeader) ? Tiff(data.AsSpan(ExifHeader.Length)) : Tiff(data);
            }
            if (!Skip(stream, length + (length & 1))) return null; // 奇数の長さは 1 バイト詰める
        }
        return null;
    }

    /// <summary>TIFF の形（"II*\0" / "MM\0*" で始まる）なら写しを返す</summary>
    private static byte[]? Tiff(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && (data.StartsWith("II*\0"u8) || data.StartsWith("MM\0*"u8)) ? data.ToArray() : null;

    // ---- 直す ----

    /// <summary>
    /// 保存する画像（width × height、回転を直したもの）に合わせた EXIF の写し: 向きを 1 に、縦横の値を合わせ、縮小画像を外す。
    /// 形がおかしくて直せなければ null（EXIF を書かない）
    /// </summary>
    public static byte[]? ForSave(byte[] tiff, int width, int height)
    {
        var data = (byte[])tiff.Clone();
        try
        {
            bool little = data[0] == (byte)'I';
            int ifd0 = (int)ReadU32(data, 4, little);
            int entries = ReadU16(data, ifd0, little);
            int exifIfd = -1;
            for (int i = 0; i < entries; i++)
            {
                int e = ifd0 + 2 + i * 12;
                ushort tag = ReadU16(data, e, little);
                if (tag == TagOrientation) WriteShortOrLong(data, e, 1, little);
                else if (tag == TagExifIfd) exifIfd = (int)ReadU32(data, e + 8, little);
            }
            // IFD0 の次（IFD1 = 縮小画像）への参照を外す
            WriteU32(data, ifd0 + 2 + entries * 12, 0, little);
            if (exifIfd > 0)
            {
                int count = ReadU16(data, exifIfd, little);
                for (int i = 0; i < count; i++)
                {
                    int e = exifIfd + 2 + i * 12;
                    ushort tag = ReadU16(data, e, little);
                    if (tag == TagPixelX) WriteShortOrLong(data, e, (uint)width, little);
                    else if (tag == TagPixelY) WriteShortOrLong(data, e, (uint)height, little);
                }
            }
            return data;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>項目の値を書き換える（型が SHORT・LONG で、値が 1 つだけ・項目の中に収まっているときだけ）</summary>
    private static void WriteShortOrLong(byte[] data, int entry, uint value, bool little)
    {
        ushort type = ReadU16(data, entry + 2, little);
        if (ReadU32(data, entry + 4, little) != 1) return;
        if (type == 3 && value <= ushort.MaxValue)
        {
            Span<byte> field = data.AsSpan(entry + 8, 4);
            field.Clear();
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(field, (ushort)value);
            else BinaryPrimitives.WriteUInt16BigEndian(field, (ushort)value);
        }
        else if (type == 4) WriteU32(data, entry + 8, value, little);
    }

    private static ushort ReadU16(byte[] d, int at, bool little)
    {
        Check(d, at, 2);
        return little ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at));
    }

    private static uint ReadU32(byte[] d, int at, bool little)
    {
        Check(d, at, 4);
        return little ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at));
    }

    private static void WriteU32(byte[] d, int at, uint value, bool little)
    {
        Check(d, at, 4);
        if (little) BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), value);
        else BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(at), value);
    }

    private static void Check(byte[] d, int at, int size)
    {
        if (at < 0 || at > d.Length - size) throw new ArgumentOutOfRangeException(nameof(at), "EXIF の形がおかしい");
    }

    // ---- 書き戻す ----

    /// <summary>JPEG の先頭（JFIF の APP0 があればその後）に EXIF の APP1 を入れる。長すぎる EXIF は入れない</summary>
    public static byte[] AddToJpeg(byte[] jpeg, byte[] tiff)
    {
        if (tiff.Length > MaxJpegExif || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return jpeg;
        int at = 2;
        if (jpeg[2] == 0xFF && jpeg[3] == 0xE0 && jpeg.Length >= 6) at = 4 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(4));
        if (at > jpeg.Length) return jpeg;
        using var output = new MemoryStream(jpeg.Length + tiff.Length + 10);
        output.Write(jpeg, 0, at);
        Span<byte> segment = stackalloc byte[4];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment[2..], (ushort)(2 + ExifHeader.Length + tiff.Length));
        output.Write(segment);
        output.Write(ExifHeader);
        output.Write(tiff);
        output.Write(jpeg, at, jpeg.Length - at);
        return output.ToArray();
    }

    /// <summary>PNG の最初の画素のデータ（IDAT）の前に eXIf を入れる</summary>
    public static byte[] AddToPng(byte[] png, byte[] tiff)
    {
        int at = 8;
        while (at + 8 <= png.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            if (png.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8)) break;
            at += 12 + (int)length;
        }
        if (at + 8 > png.Length) return png;
        using var output = new MemoryStream(png.Length + tiff.Length + 12);
        output.Write(png, 0, at);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)tiff.Length);
        output.Write(number);
        var typeAndData = new byte[4 + tiff.Length];
        "eXIf"u8.CopyTo(typeAndData);
        tiff.CopyTo(typeAndData, 4);
        output.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typeAndData));
        output.Write(number);
        output.Write(png, at, png.Length - at);
        return output.ToArray();
    }

    /// <summary>
    /// WebP に EXIF を足す。Skia が書く単純な形（VP8 / VP8L だけ）は、拡張の形（VP8X）にしてから足す
    /// </summary>
    public static byte[] AddToWebp(byte[] webp, byte[] tiff, int width, int height)
    {
        if (webp.Length < 20 || !webp.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !webp.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return webp;
        using var output = new MemoryStream(webp.Length + tiff.Length + 32);
        output.Write(webp, 0, 12);
        var first = webp.AsSpan(12, 4);
        if (first.SequenceEqual("VP8X"u8))
        {
            output.Write(webp, 12, webp.Length - 12);
            output.Position = 20;
            int flags = output.ReadByte();
            output.Position = 20;
            output.WriteByte((byte)(flags | 0x08)); // EXIF あり
            output.Position = output.Length;
        }
        else
        {
            bool alpha = false;
            if (first.SequenceEqual("VP8L"u8) && webp.Length >= 25 && webp[20] == 0x2F)
                alpha = ((BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(21)) >> 28) & 1) == 1;
            Span<byte> vp8x = stackalloc byte[18];
            "VP8X"u8.CopyTo(vp8x);
            BinaryPrimitives.WriteUInt32LittleEndian(vp8x[4..], 10);
            vp8x[8] = (byte)(0x08 | (alpha ? 0x10 : 0)); // EXIF あり・透明あり
            WriteU24(vp8x[12..], width - 1);
            WriteU24(vp8x[15..], height - 1);
            output.Write(vp8x);
            output.Write(webp, 12, webp.Length - 12);
        }
        Span<byte> chunk = stackalloc byte[8];
        "EXIF"u8.CopyTo(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk[4..], (uint)tiff.Length);
        output.Write(chunk);
        output.Write(tiff);
        if ((tiff.Length & 1) == 1) output.WriteByte(0);
        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        return result;
    }

    private static void WriteU24(Span<byte> to, int value)
    {
        to[0] = (byte)value;
        to[1] = (byte)(value >> 8);
        to[2] = (byte)(value >> 16);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>PNG の区切りの CRC（種類とデータに対して）</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    // ---- 読むための小さな道具 ----

    private static int ReadUpTo(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer) => ReadUpTo(stream, buffer) == buffer.Length;

    private static bool Skip(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            if (stream.Position + count > stream.Length) return false;
            stream.Seek(count, SeekOrigin.Current);
            return true;
        }
        var buffer = new byte[(int)Math.Min(count, 81920)];
        while (count > 0)
        {
            int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n == 0) return false;
            count -= n;
        }
        return true;
    }

    /// <summary>先に読んでしまった数バイトを、元のストリームの前に付けて読み直せるようにする</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private int _at;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at < prefix.Length)
            {
                int n = Math.Min(count, prefix.Length - _at);
                Array.Copy(prefix, _at, buffer, offset, n);
                _at += n;
                return n;
            }
            return inner.Read(buffer, offset, count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position - (prefix.Length - _at);
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin != SeekOrigin.Current) throw new NotSupportedException();
            // 先に読んだ分を使い切ってから、元のストリームを進める
            int fromPrefix = (int)Math.Min(offset, prefix.Length - _at);
            _at += fromPrefix;
            return inner.Seek(offset - fromPrefix, SeekOrigin.Current) - (prefix.Length - _at);
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
