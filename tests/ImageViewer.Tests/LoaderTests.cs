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

        string gif = Path.Combine(dir, "anim.gif");
        TestImage.SaveFrames(gif, new[] { new TestImage(40, 40, Red), new TestImage(40, 40, Blue), new TestImage(40, 40, Red) }, new[] { 10, 10, 10 });
        using (var img = ImageLoader.LoadForEdit(gif))
            check(img.Frames.Count == 1, "アニメ GIF: 既定は先頭フレームのみ");
        using (var img = ImageLoader.LoadForEdit(gif, new LoadOptions { FirstFrameOnly = false }))
            check(img.Frames.Count == 3, $"アニメ GIF: 全フレーム指定で {img.Frames.Count} フレーム");

        // ---- TIFF は WIC で読む（ImageSharp の TIFF デコーダの脆弱性を避けるため） ----
        string tif = Path.Combine(dir, "split.tif");
        SplitImage(400, 200).Save(tif);
        using (var img = ImageLoader.LoadForEdit(tif))
            check(img.Width == 400 && img.Height == 200 && IsLeftRedRightBlue(TestImage.FromApi(img)) && !ImageViewer.Core.Editing.Adjuster.KeepsMetadata(img),
                "TIFF: WIC で読む（ImageSharp で読んでいればメタデータを持つ）");
        using (var img = ImageLoader.Load(tif, LoadOptions.Thumbnail(100)))
            check(img.Width == 100 && img.Height == 50 && IsLeftRedRightBlue(TestImage.FromApi(img)), $"TIFF: 縮小読み込み 400x200 → {img.Width}x{img.Height}");
        info = ImageLoader.Identify(tif);
        check(info == new ImageHeader(400, 200, "TIFF"), $"Identify: TIFF（{info}）");
        check(ImageFormats.IsSupported(tif) && !ImageFormats.IsImageSharpFormat(tif), "TIFF: 対応形式だが ImageSharp の形式ではない");
        // 拡張子を変えた TIFF も ImageSharp の TIFF デコーダには渡さない（読めない扱いにして WIC で読み直す）
        string disguised = Path.Combine(dir, "tiff_inside.png");
        File.Copy(tif, disguised, overwrite: true);
        using (var img = ImageLoader.LoadForEdit(disguised))
            check(img.Width == 400 && IsLeftRedRightBlue(TestImage.FromApi(img)) && !ImageViewer.Core.Editing.Adjuster.KeepsMetadata(img),
                "拡張子が PNG の TIFF: ImageSharp では読まず WIC で読む");
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
