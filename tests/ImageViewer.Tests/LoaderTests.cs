// ImageLoader の動作確認
// HEIC / JPEG XL のテスト画像は WinRT のエンコーダで作る（その PC に拡張機能が無ければ SKIP）
using ImageViewer.Core.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;

static class LoaderTests
{
    static readonly Px Red = Px.Red, Blue = Px.Blue;

    public static async Task RunAsync(Action<bool, string> check, string dir)
    {
        // ---- WIC デコーダの列挙 ----
        var exts = WicCodecs.DecoderExtensions;
        check(exts.Contains(".jpg") && exts.Contains(".png"), $"WIC デコーダ列挙（{WicCodecs.Decoders.Count} 個、標準の JPEG / PNG を含む）");
        Console.WriteLine("    この PC の WIC 拡張形式: " + string.Join(" ",
            new[] { ".heic", ".avif", ".jxl", ".cr2", ".nef", ".arw", ".dng" }.Where(exts.Contains)));

        // ---- ImageSharp 経路 ----
        string jpg = Path.Combine(dir, "wide.jpg");
        SplitImage(2000, 1000).Save(jpg);
        using (var img = ImageLoader.Load(jpg, LoadOptions.Thumbnail(500)))
            check(img.Width == 500 && img.Height == 250 && IsLeftRedRightBlue(TestImage.FromApi(img)), $"JPEG 縮小読み込み 2000x1000 → {img.Width}x{img.Height}");
        using (var img = ImageLoader.Load(jpg))
            check(img.Width == 2000 && img.Height == 1000, "JPEG 原寸読み込み");

        string small = Path.Combine(dir, "small.png");
        SplitImage(300, 100).Save(small);
        using (var img = ImageLoader.Load(small, LoadOptions.Thumbnail(500)))
            check(img.Width == 300 && img.Height == 100, "上限より小さい画像は拡大しない");

        // Orientation=6（時計回り 90° で正立）: 元画像の左側が上に来る
        string rotated = Path.Combine(dir, "rotated.jpg");
        SplitImage(200, 100).Save(rotated, new TestExif { Orientation = 6 });
        using (var img = ImageLoader.Load(rotated))
            check(img.Width == 100 && img.Height == 200 && TestImage.FromApi(img)[50, 20].Near(Red, 39) && TestImage.FromApi(img)[50, 180].Near(Blue, 39),
                $"JPEG の EXIF 回転補正 200x100 → {img.Width}x{img.Height}");

        // ---- 大きさと形式だけを調べる（ヘッダーだけ読む） ----
        var info = ImageLoader.Identify(rotated);
        check(info == new ImageHeader(100, 200, "JPEG"), $"Identify: 回転を反映した縦横（{info}）");
        info = ImageLoader.Identify(small);
        check(info == new ImageHeader(300, 100, "PNG"), $"Identify: PNG（{info}）");

        // ---- 撮影情報（EXIF） ----
        string photo = Path.Combine(dir, "photo.jpg");
        SplitImage(120, 80).Save(photo, new TestExif
        {
            Taken = "2026:09:10 10:21:05", Make = "Canon", Model = "Canon EOS R5", Lens = "RF24-70mm F2.8 L IS USM",
            FNumber = (28, 10), Exposure = (1, 250), Iso = 200,
        });
        var shot = ImageLoader.Identify(photo)?.Photo;
        check(shot?.TakenAt == new DateTime(2026, 9, 10, 10, 21, 5), $"EXIF: 撮影日時（{shot?.TakenAt}）");
        check(shot?.Camera == "Canon EOS R5", $"EXIF: 機種名がメーカー名で始まれば機種名だけ（{shot?.Camera}）");
        check(shot?.Lens == "RF24-70mm F2.8 L IS USM", "EXIF: レンズ");
        check(shot?.SettingsText == "f/2.8 · 1/250 · ISO 200", $"EXIF: 撮影設定（{shot?.SettingsText}）");
        check(ImageLoader.Identify(small)?.Photo == null, "EXIF が無ければ撮影情報は null");
        check(PhotoInfo.CameraName("NIKON CORPORATION", "NIKON Z 6") == "NIKON Z 6", "カメラ名: メーカー名の最初の語で始まる機種名");
        check(PhotoInfo.CameraName("FUJIFILM", "X-T5") == "FUJIFILM X-T5", "カメラ名: 機種名にメーカー名が無ければ前に付ける");
        check(PhotoInfo.FormatExposure(2) == "2s" && PhotoInfo.FormatExposure(0.5) == "1/2", "シャッター速度の表示");

        // ---- 色プロファイル（#81 の決定: 読むときに sRGB に変換し、保存はプロファイルなし） ----
        var raw = new Px(40, 160, 90);
        string p3Png = Path.Combine(dir, "p3.png");
        var expected = SaveDisplayP3(p3Png, SkiaSharp.SKEncodedImageFormat.Png, raw);
        check(HasNonSrgbProfile(p3Png) && !expected.Near(raw, 6), $"テスト画像: Display P3 のプロファイル付きの PNG（sRGB では {expected}）");
        using (var img = ImageLoader.Load(p3Png))
        {
            var got = TestImage.FromApi(img)[10, 10];
            check(got.Near(expected, 3), $"色プロファイル付きの PNG は sRGB に変換して読む（{got}、変換しなければ {raw}）");
        }
        using (var img = ImageLoader.Load(p3Png, LoadOptions.Thumbnail(16)))
            check(img.Width == 16 && TestImage.FromApi(img)[5, 5].Near(expected, 3), "縮小して読むときも sRGB に変換する");
        string p3Jpg = Path.Combine(dir, "p3.jpg");
        expected = SaveDisplayP3(p3Jpg, SkiaSharp.SKEncodedImageFormat.Jpeg, raw);
        using (var img = ImageLoader.Load(p3Jpg))
            check(HasNonSrgbProfile(p3Jpg) && TestImage.FromApi(img)[10, 10].Near(expected, 8), "色プロファイル付きの JPEG も sRGB に変換して読む");
        ImageViewer.Core.Editing.Rotator.RotateFile(p3Jpg, ImageViewer.Core.Editing.RotateDirection.Half);
        using (var img = ImageLoader.Load(p3Jpg))
            check(!HasNonSrgbProfile(p3Jpg) && TestImage.FromApi(img)[10, 10].Near(expected, 10),
                "編集して保存すると、sRGB の画素をプロファイルなしで書く（同じ色に見える）");

        // PNG のガンマ（gAMA）も色の情報: 直線（ガンマ 1.0）の PNG は sRGB に変換して読み、保存するときはガンマを付けない
        string linear = Path.Combine(dir, "linear.png");
        // WIC で書く PNG は sRGB の印（sRGB の区切り）を持ち、そちらが優先されるので、印の無い PNG を Skia で書く
        using (var gray = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(20, 10, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul)))
        {
            gray.Erase(new SkiaSharp.SKColor(128, 128, 128));
            using var encoded = gray.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(linear, WithPngChunk(encoded.ToArray(), "gAMA", new byte[] { 0, 1, 0x86, 0xA0 })); // 100000 = 1.0
        }
        using (var img = ImageLoader.Load(linear))
        {
            var got = TestImage.FromApi(img)[5, 5];
            check(got.R is > 180 and < 195, $"ガンマ 1.0 の PNG は sRGB に変換して読む（128 → {got.R}）");
        }
        ImageViewer.Core.Editing.Rotator.RotateFile(linear, ImageViewer.Core.Editing.RotateDirection.Half);
        using (var img = ImageLoader.Load(linear))
            check(!HasPngChunk(linear, "gAMA") && TestImage.FromApi(img)[5, 5].R is > 180 and < 195,
                "編集して保存すると、元のガンマを付けない（同じ明るさに見える）");

        string gif = Path.Combine(dir, "anim.gif");
        TestImage.SaveFrames(gif, new[] { new TestImage(40, 40, Red), new TestImage(40, 40, Blue), new TestImage(40, 40, Red) }, new[] { 10, 10, 10 });
        using (var img = ImageLoader.Load(gif))
            check(img.Width == 40 && TestImage.FromApi(img)[20, 20].Near(Red, 39), "アニメ GIF: 先頭のコマを読む");

        // ---- TIFF は WIC で読む（ImageSharp の TIFF デコーダの脆弱性を避けるため） ----
        string tif = Path.Combine(dir, "split.tif");
        SplitImage(400, 200).Save(tif);
        using (var img = ImageLoader.Load(tif))
            check(img.Width == 400 && img.Height == 200 && IsLeftRedRightBlue(TestImage.FromApi(img)) && !ImageViewer.Core.Editing.Converter.KeepsMetadata(tif),
                "TIFF: WIC で読む（ImageSharp で読む形式ならメタデータを残せる）");
        using (var img = ImageLoader.Load(tif, LoadOptions.Thumbnail(100)))
            check(img.Width == 100 && img.Height == 50 && IsLeftRedRightBlue(TestImage.FromApi(img)), $"TIFF: 縮小読み込み 400x200 → {img.Width}x{img.Height}");
        info = ImageLoader.Identify(tif);
        check(info == new ImageHeader(400, 200, "TIFF"), $"Identify: TIFF（{info}）");
        check(ImageFormats.IsSupported(tif) && !ImageFormats.IsImageSharpFormat(tif), "TIFF: 対応形式だが ImageSharp の形式ではない");
        // 拡張子を変えた TIFF も ImageSharp の TIFF デコーダには渡さない（読めない扱いにして WIC で読み直す）
        string disguised = Path.Combine(dir, "tiff_inside.png");
        File.Copy(tif, disguised, overwrite: true);
        using (var img = ImageLoader.Load(disguised))
            check(img.Width == 400 && IsLeftRedRightBlue(TestImage.FromApi(img)), "拡張子が PNG の TIFF: ImageSharp では読まず WIC で読む");
        info = ImageLoader.Identify(disguised);
        check(info == new ImageHeader(400, 200, "TIFF"), $"拡張子が PNG の TIFF: Identify も WIC で、形式は中身の TIFF（{info}）");
        check(!ImageViewer.Core.Editing.Converter.KeepsMetadata(disguised) && ImageViewer.Core.Editing.Converter.KeepsMetadata(small),
            "拡張子が PNG の TIFF: 変換でメタデータを残せない（中身で判断する。本物の PNG は残せる）");
        // WIC にデコーダが無い拡張子（.qoi など ImageSharp だけの形式）でも、中身が TIFF なら WIC で読む
        string disguisedQoi = Path.Combine(dir, "tiff_inside.qoi");
        File.Copy(tif, disguisedQoi, overwrite: true);
        using (var img = ImageLoader.Load(disguisedQoi))
            check(img.Width == 400 && IsLeftRedRightBlue(TestImage.FromApi(img)), "拡張子が QOI の TIFF: WIC で読む");
        info = ImageLoader.Identify(disguisedQoi);
        check(info == new ImageHeader(400, 200, "TIFF"), $"拡張子が QOI の TIFF: Identify も WIC で（{info}）");
        string notImage = Path.Combine(dir, "not_image.qoi");
        File.WriteAllText(notImage, "not an image");
        check(ImageLoader.Identify(notImage) == null && Throws<Exception>(() => ImageLoader.Load(notImage)),
            "画像でないファイルは WIC で読み直しても失敗する（拡張子が QOI）");

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
        check(ImageLoader.Identify(broken) == null && ImageLoader.Identify(txt) == null, "Identify: 壊れた・未対応のファイルは null");
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

        var info = ImageLoader.Identify(path);
        var expected = orientation == 6 ? new ImageHeader(200, 400, ext.TrimStart('.').ToUpperInvariant()) : new ImageHeader(400, 200, ext.TrimStart('.').ToUpperInvariant());
        check(info == expected, $"{label} Identify（{info}）");

        using var img = ImageLoader.Load(path, LoadOptions.Thumbnail(100));
        if (orientation == 6)
            check(img.Width == 50 && img.Height == 100 && TestImage.FromApi(img)[25, 10].Near(Red, 39) && TestImage.FromApi(img)[25, 90].Near(Blue, 39),
                $"{label} 縮小＋回転補正 400x200 → {img.Width}x{img.Height}");
        else
            check(img.Width == 100 && img.Height == 50 && IsLeftRedRightBlue(TestImage.FromApi(img)),
                $"{label} 縮小読み込み 400x200 → {img.Width}x{img.Height}");

        // 拡張子を .qoi に変えた HEIC も、HEIC と同じ向き・大きさで読む。
        // （ここで作る HEIC は回転をコンテナにだけ持ち EXIF の向きが無いので、二重の回転そのものは再現しない）
        if (ext == ".heic" && orientation == 6)
        {
            string renamed = Path.ChangeExtension(path, ".qoi");
            File.Copy(path, renamed, overwrite: true);
            var renamedInfo = ImageLoader.Identify(renamed);
            check(renamedInfo == expected, $"{label} 拡張子が QOI でも同じ向き（Identify: {renamedInfo}）");
            using var renamedImg = ImageLoader.Load(renamed, LoadOptions.Thumbnail(100));
            check(renamedImg.Width == 50 && renamedImg.Height == 100 && TestImage.FromApi(renamedImg)[25, 10].Near(Red, 39) && TestImage.FromApi(renamedImg)[25, 90].Near(Blue, 39),
                $"{label} 拡張子が QOI でも同じ向き（読み込み {renamedImg.Width}x{renamedImg.Height}）");
        }
    }

    /// <summary>WinRT のエンコーダで左半分が赤・右半分が青の画像を作る（WIC で読む形式のテスト画像用）</summary>
    internal static async Task EncodeAsync(string path, Guid encoderId, int w, int h, ushort? orientation)
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

    /// <summary>
    /// 全体が raw（Display P3 の値）の画像を、Display P3 のプロファイル付きで保存する（Skia はプロファイルを埋め込む）。
    /// その色を sRGB で表した値（Skia で変換したもの）を返す
    /// </summary>
    static Px SaveDisplayP3(string path, SkiaSharp.SKEncodedImageFormat format, Px raw)
    {
        var p3 = SkiaSharp.SKColorSpace.CreateRgb(SkiaSharp.SKColorSpaceTransferFn.Srgb, SkiaSharp.SKColorSpaceXyz.DisplayP3);
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(32, 24, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul, p3));
        var pixels = bitmap.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = raw.R;
            pixels[i + 1] = raw.G;
            pixels[i + 2] = raw.B;
            pixels[i + 3] = 255;
        }
        using (var stream = File.Create(path))
            if (!bitmap.Encode(stream, format, 95)) throw new InvalidOperationException("テスト画像を保存できません");
        using var srgb = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(32, 24, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul,
            SkiaSharp.SKColorSpace.CreateSrgb()));
        using (var from = bitmap.PeekPixels())
        using (var to = srgb.PeekPixels())
            from.ReadPixels(to);
        var c = srgb.GetPixelSpan();
        return new Px(c[0], c[1], c[2]);
    }

    /// <summary>PNG の先頭の区切り（IHDR）の後に、区切りを 1 つ入れる</summary>
    static byte[] WithPngChunk(byte[] png, string type, byte[] data)
    {
        int ihdrEnd = 8 + 12 + 13;
        var typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        uint crc = 0xFFFFFFFF;
        foreach (byte b in typeAndData)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        crc ^= 0xFFFFFFFF;
        byte[] Be(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        return png.Take(ihdrEnd).Concat(Be((uint)data.Length)).Concat(typeAndData).Concat(Be(crc)).Concat(png.Skip(ihdrEnd)).ToArray();
    }

    static bool HasPngChunk(string path, string type) =>
        File.ReadAllBytes(path).AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(type)) >= 0;

    /// <summary>sRGB でない色プロファイルが埋め込まれているか（Skia でヘッダーを読む）</summary>
    static bool HasNonSrgbProfile(string path)
    {
        using var codec = SkiaSharp.SKCodec.Create(path);
        return codec?.Info.ColorSpace is { IsSrgb: false };
    }

    /// <summary>左半分が赤、右半分が青の画像</summary>
    static TestImage SplitImage(int w, int h) => new TestImage(w, h, Blue).Fill(Red, 0, 0, w / 2, h);

    /// <summary>左が赤・右が青か（非可逆圧縮の誤差は許す）</summary>
    static bool IsLeftRedRightBlue(TestImage img) =>
        img[img.Width / 5, img.Height / 2].Near(Red, 39) && img[img.Width * 4 / 5, img.Height / 2].Near(Blue, 39);

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
