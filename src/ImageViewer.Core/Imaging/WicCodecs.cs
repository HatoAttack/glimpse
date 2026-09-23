// この PC の WIC（Windows Imaging Component）に登録されているデコーダの列挙
// HEIC / AVIF / RAW / JPEG XL などは Store の拡張機能として入るため、PC ごとに対応状況が違う。
// レジストリには Store 版のコーデックが載らないので、WIC の COM API で列挙する
using System.Runtime.InteropServices;

namespace ImageViewer.Core.Imaging;

public sealed record WicDecoderInfo(string FriendlyName, IReadOnlyList<string> Extensions);

public static class WicCodecs
{
    private static readonly Lazy<IReadOnlyList<WicDecoderInfo>> _decoders = new(EnumerateDecoders);
    private static readonly Lazy<IReadOnlySet<string>> _extensions = new(() =>
        new HashSet<string>(_decoders.Value.SelectMany(d => d.Extensions), StringComparer.OrdinalIgnoreCase));

    /// <summary>登録済みデコーダ一覧（初回アクセス時に一度だけ列挙）</summary>
    public static IReadOnlyList<WicDecoderInfo> Decoders => _decoders.Value;

    /// <summary>WIC で読める拡張子（".heic" 形式、大文字小文字無視）</summary>
    public static IReadOnlySet<string> DecoderExtensions => _extensions.Value;

    private static IReadOnlyList<WicDecoderInfo> EnumerateDecoders()
    {
        var result = new List<WicDecoderInfo>();
        try
        {
            var factory = (IWICImagingFactory)new WICImagingFactory();
            factory.CreateComponentEnumerator(WICDecoder, 0, out var enumerator);
            var buffer = new object[1];
            while (enumerator.Next(1, buffer, out uint fetched) == 0 && fetched == 1)
            {
                if (buffer[0] is IWICBitmapCodecInfo info)
                {
                    string name = ReadString(info.GetFriendlyName);
                    var exts = ReadString(info.GetFileExtensions)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(e => e.ToLowerInvariant())
                        .ToList();
                    result.Add(new WicDecoderInfo(name, exts));
                }
                Marshal.ReleaseComObject(buffer[0]);
            }
            Marshal.ReleaseComObject(enumerator);
            Marshal.ReleaseComObject(factory);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            // WIC が使えない環境（通常の Windows ではまず無い）では空のまま＝ImageSharp のみで動く
            System.Diagnostics.Debug.WriteLine($"WIC デコーダの列挙に失敗: {ex.Message}");
        }
        return result;
    }

    private delegate void StringGetter(uint cch, char[]? buffer, out uint actual);

    /// <summary>WIC の「長さを問い合わせてから取得する」形式の文字列取得</summary>
    private static string ReadString(StringGetter getter)
    {
        getter(0, null, out uint len);
        if (len == 0) return "";
        var buf = new char[len];
        getter(len, buf, out _);
        return new string(buf, 0, (int)len - 1); // 末尾の NUL を除く
    }

    // ---- COM 定義（使うメソッドより前の vtable は順番合わせのためのダミー） ----

    private const uint WICDecoder = 0x1;

    [ComImport, Guid("cacaf262-9370-4615-a13b-9f5539da4c0a")]
    private class WICImagingFactory { }

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        void _CreateDecoderFromFilename();
        void _CreateDecoderFromStream();
        void _CreateDecoderFromFileHandle();
        void _CreateComponentInfo();
        void _CreateDecoder();
        void _CreateEncoder();
        void _CreatePalette();
        void _CreateFormatConverter();
        void _CreateBitmapScaler();
        void _CreateBitmapClipper();
        void _CreateBitmapFlipRotator();
        void _CreateStream();
        void _CreateColorContext();
        void _CreateColorTransformer();
        void _CreateBitmap();
        void _CreateBitmapFromSource();
        void _CreateBitmapFromSourceRect();
        void _CreateBitmapFromMemory();
        void _CreateBitmapFromHBITMAP();
        void _CreateBitmapFromHICON();
        void CreateComponentEnumerator(uint componentTypes, uint options, out IEnumUnknown enumerator);
    }

    [ComImport, Guid("00000100-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumUnknown
    {
        [PreserveSig]
        int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.IUnknown, SizeParamIndex = 0)] object[] rgelt, out uint fetched);
    }

    [ComImport, Guid("E87A44C4-B76E-4c47-8B09-298EB12A2714"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapCodecInfo
    {
        // IWICComponentInfo
        void _GetComponentType();
        void _GetCLSID();
        void _GetSigningStatus();
        void _GetAuthor();
        void _GetVendorGUID();
        void _GetVersion();
        void _GetSpecVersion();
        void GetFriendlyName(uint cch, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] char[]? buffer, out uint actual);
        // IWICBitmapCodecInfo
        void _GetContainerFormat();
        void _GetPixelFormats();
        void _GetColorManagementVersion();
        void _GetDeviceManufacturer();
        void _GetDeviceModels();
        void _GetMimeTypes();
        void GetFileExtensions(uint cch, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] char[]? buffer, out uint actual);
    }
}
