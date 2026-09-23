// ImageLoader の動作確認
// HEIC / JPEG XL のテスト画像は WinRT のエンコーダで作る（その PC に拡張機能が無ければ SKIP）
using ImageViewer.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Image = SixLabors.ImageSharp.Image;

static class LoaderTests
{
    static readonly Rgba32 Red = new(255, 0, 0), Blue = new(0, 0, 255);

    public static async Task RunAsync(Action<bool, string> check, string dir)
    {
        // ---- WIC デコーダの列挙 ----
        var exts = WicCodecs.DecoderExtensions;
        check(exts.Contains(".jpg") && exts.Contains(".png"), $"WIC デコーダ列挙（{WicCodecs.Decoders.Count} 個、標準の JPEG / PNG を含む）");
        Console.WriteLine("    この PC の WIC 拡張形式: " + string.Join(" ",
            new[] { ".heic", ".avif", ".jxl", ".cr2", ".nef", ".arw", ".dng" }.Where(exts.Contains)));

        // ---- ImageSharp 経路 ----
        string jpg = Path.Combine(dir, "wide.jpg");
        using (var img = SplitImage(2000, 1000)) img.SaveAsJpeg(jpg);
        using (var img = ImageLoader.Load(jpg, LoadOptions.Thumbnail(500)))
            check(img.Width == 500 && img.Height == 250 && IsLeftRedRightBlue(img), $"JPEG 縮小読み込み 2000x1000 → {img.Width}x{img.Height}");
        using (var img = ImageLoader.Load(jpg))
            check(img.Width == 2000 && img.Height == 1000, "JPEG 原寸読み込み");

        string small = Path.Combine(dir, "small.png");
        using (var img = SplitImage(300, 100)) img.SaveAsPng(small);
        using (var img = ImageLoader.Load(small, LoadOptions.Thumbnail(500)))
            check(img.Width == 300 && img.Height == 100, "上限より小さい画像は拡大しない");

        // Orientation=6（時計回り 90° で正立）: 元画像の左側が上に来る
        string rotated = Path.Combine(dir, "rotated.jpg");
        using (var img = SplitImage(200, 100))
        {
            img.Metadata.ExifProfile = new ExifProfile();
            img.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
            img.SaveAsJpeg(rotated);
        }
        using (var img = ImageLoader.Load(rotated))
            check(img.Width == 100 && img.Height == 200 && Near(img[50, 20], Red) && Near(img[50, 180], Blue),
                $"JPEG の EXIF 回転補正 200x100 → {img.Width}x{img.Height}");

        string gif = Path.Combine(dir, "anim.gif");
        using (var img = new Image<Rgba32>(40, 40, Red))
        {
            img.Frames.AddFrame(new Image<Rgba32>(40, 40, Blue).Frames.RootFrame);
            img.Frames.AddFrame(new Image<Rgba32>(40, 40, Red).Frames.RootFrame);
            img.SaveAsGif(gif, new GifEncoder());
        }
        using (var img = ImageLoader.Load(gif))
            check(img.Frames.Count == 1, "アニメ GIF: 既定は先頭フレームのみ");
        using (var img = ImageLoader.Load(gif, new LoadOptions { FirstFrameOnly = false }))
            check(img.Frames.Count == 3, $"アニメ GIF: 全フレーム指定で {img.Frames.Count} フレーム");

        // ---- WIC 経路 ----
        await WicCaseAsync(check, dir, ".heic", orientation: null);
        await WicCaseAsync(check, dir, ".heic", orientation: 6);
        await WicCaseAsync(check, dir, ".jxl", orientation: null);

        // ---- 異常系 ----
        string txt = Path.Combine(dir, "a.txt");
        File.WriteAllText(txt, "x");
        check(Throws<NotSupportedException>(() => ImageLoader.Load(txt)), "未対応の拡張子は NotSupportedException");
        string broken = Path.Combine(dir, "broken.png");
        File.WriteAllBytes(broken, new byte[] { 1, 2, 3, 4, 5 });
        check(Throws<Exception>(() => ImageLoader.Load(broken)), "壊れたファイルは例外（WIC へのフォールバック後も失敗）");
    }

    static async Task WicCaseAsync(Action<bool, string> check, string dir, string ext, ushort? orientation)
    {
        string label = ext.ToUpperInvariant().TrimStart('.') + (orientation is ushort o ? $"（Orientation={o}）" : "");
        if (!WicCodecs.DecoderExtensions.Contains(ext))
        {
            Console.WriteLine($"SKIP {label}: この PC に {ext} のデコーダが無い");
            return;
        }
        var encoderId = BitmapEncoder.GetEncoderInformationEnumerator()
            .FirstOrDefault(i => i.FileExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))?.CodecId;
        if (encoderId == null)
        {
            Console.WriteLine($"SKIP {label}: テスト画像を作るエンコーダが無い");
            return;
        }

        string path = Path.Combine(dir, $"wic{orientation}{ext}");
        try
        {
            await EncodeAsync(path, encoderId.Value, 400, 200, orientation);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SKIP {label}: テスト画像を作れない（{ex.Message.Trim()}）");
            return;
        }

        using var img = ImageLoader.Load(path, LoadOptions.Thumbnail(100));
        if (orientation == 6)
            check(img.Width == 50 && img.Height == 100 && Near(img[25, 10], Red) && Near(img[25, 90], Blue),
                $"{label} 縮小＋回転補正 400x200 → {img.Width}x{img.Height}");
        else
            check(img.Width == 100 && img.Height == 50 && IsLeftRedRightBlue(img),
                $"{label} 縮小読み込み 400x200 → {img.Width}x{img.Height}");
    }

    static async Task EncodeAsync(string path, Guid encoderId, int w, int h, ushort? orientation)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool left = x < w / 2;
                bgra[i] = left ? (byte)0 : (byte)255; // B
                bgra[i + 2] = left ? (byte)255 : (byte)0; // R
                bgra[i + 3] = 255;
            }
        using var fs = File.Create(path);
        var encoder = await BitmapEncoder.CreateAsync(encoderId, fs.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, bgra);
        if (orientation is ushort o)
            await encoder.BitmapProperties.SetPropertiesAsync(new[]
            {
                new KeyValuePair<string, BitmapTypedValue>("System.Photo.Orientation",
                    new BitmapTypedValue(o, PropertyType.UInt16)),
            });
        await encoder.FlushAsync();
    }

    /// <summary>左半分が赤、右半分が青の画像</summary>
    static Image<Rgba32> SplitImage(int w, int h)
    {
        var img = new Image<Rgba32>(w, h, Blue);
        img.ProcessPixelRows(a =>
        {
            for (int y = 0; y < h; y++) a.GetRowSpan(y)[..(w / 2)].Fill(Red);
        });
        return img;
    }

    static bool IsLeftRedRightBlue(Image<Rgba32> img) =>
        Near(img[img.Width / 5, img.Height / 2], Red) && Near(img[img.Width * 4 / 5, img.Height / 2], Blue);

    /// <summary>非可逆圧縮の誤差を許容した色比較</summary>
    static bool Near(Rgba32 a, Rgba32 b) =>
        Math.Abs(a.R - b.R) < 40 && Math.Abs(a.G - b.G) < 40 && Math.Abs(a.B - b.B) < 40;

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
