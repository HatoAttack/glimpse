// Windows シェル（エクスプローラーと同じ仕組み）からサムネイルを取得する
// thumbcache に残っていれば一瞬で返り、HEIC 等も拡張機能があれば取れる。EXIF の回転も反映済み
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ImageViewer.Core.Thumbnails;

public static class ShellThumbnail
{
    /// <summary>長辺 size 以下のサムネイル。サムネイルハンドラが無い・失敗したときは null（アイコンは返さない）</summary>
    public static Bitmap? TryGet(string path, int size) => GetImage(path, size, SIIGBF_THUMBNAILONLY);

    /// <summary>エクスプローラーと同じアイコン（フォルダのタイル用）。取れなければ null</summary>
    public static Bitmap? TryGetIcon(string path, int size) => GetImage(path, size, SIIGBF_ICONONLY);

    private static Bitmap? GetImage(string path, int size, int flags)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbmp);
            return hr == 0 && hbmp != IntPtr.Zero ? FromHBitmap(hbmp) : null;
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// シェルの HBITMAP（32bpp の DIB セクション）を透過を保ったまま Bitmap にする。
    /// Image.FromHbitmap は透過を捨てるので使わない。ハンドラによって
    /// 「上下逆」「アルファが全部 0」「乗算済みでない」ことがあるので、それぞれ補正する
    /// </summary>
    private static Bitmap FromHBitmap(IntPtr hbmp)
    {
        if (GetObject(hbmp, Marshal.SizeOf<DIBSECTION>(), out DIBSECTION ds) == 0
            || ds.dsBm.bmBitsPixel != 32 || ds.dsBm.bmBits == IntPtr.Zero)
            return Image.FromHbitmap(hbmp); // DIB でない・32bpp でない: 透過なしで妥協

        int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, srcStride = ds.dsBm.bmWidthBytes;
        bool bottomUp = ds.dsBmih.biHeight > 0;
        var pixels = new byte[w * 4 * h];
        for (int y = 0; y < h; y++)
        {
            int srcRow = bottomUp ? h - 1 - y : y;
            Marshal.Copy(ds.dsBm.bmBits + srcRow * srcStride, pixels, y * w * 4, w * 4);
        }

        bool allTransparent = true, premultiplied = true;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a != 0) allTransparent = false;
            if (pixels[i] > a || pixels[i + 1] > a || pixels[i + 2] > a) premultiplied = false;
        }
        if (allTransparent)
        {
            // アルファを使わないハンドラ（JPEG 等）: 不透明として扱う
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            premultiplied = true;
        }

        var bmp = new Bitmap(w, h, premultiplied ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(pixels, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    // ---- Win32 / COM 定義 ----

    private const int SIIGBF_ICONONLY = 0x4, SIIGBF_THUMBNAILONLY = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out DIBSECTION ds);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);
}
