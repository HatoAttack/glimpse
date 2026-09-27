// ZIP の中のファイル名の文字コード
// UTF-8 の印が付いていない名前は、日本語の Windows で作った ZIP だと Shift_JIS（CP932）のことが多い。
// macOS などは印を付けずに UTF-8 で書くこともあるので、UTF-8 として正しければ UTF-8、そうでなければ OS の OEM コードページ
// （日本語の Windows なら CP932、英語なら ZIP の仕様どおり CP437）で読む
using System.Globalization;
using System.Text;

namespace ImageViewer.Core.Archives;

internal sealed class ZipNameEncoding : Encoding
{
    public static readonly ZipNameEncoding Instance = new();

    private static readonly Encoding Utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
    private static readonly Encoding Legacy = CreateLegacy();

    private static Encoding CreateLegacy()
    {
        try
        {
            RegisterProvider(CodePagesEncodingProvider.Instance);
            return GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Latin1;
        }
    }

    private static Encoding Pick(byte[] bytes, int index, int count)
    {
        try
        {
            Utf8.GetCharCount(bytes, index, count);
            return Utf8;
        }
        catch (DecoderFallbackException)
        {
            return Legacy;
        }
    }

    public override string GetString(byte[] bytes, int index, int count) => Pick(bytes, index, count).GetString(bytes, index, count);
    public override int GetCharCount(byte[] bytes, int index, int count) => Pick(bytes, index, count).GetCharCount(bytes, index, count);

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
        Pick(bytes, byteIndex, byteCount).GetChars(bytes, byteIndex, byteCount, chars, charIndex);

    public override int GetMaxCharCount(int byteCount) => byteCount + 1;

    // 書き込みはしない（読むだけ）が、Encoding として必要なので UTF-8 で
    public override int GetByteCount(char[] chars, int index, int count) => Utf8.GetByteCount(chars, index, count);

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
        Utf8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);

    public override int GetMaxByteCount(int charCount) => Utf8.GetMaxByteCount(charCount);
}
