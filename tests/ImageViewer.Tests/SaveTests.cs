// 保存の動作確認（#81 手順6）: JPEG・PNG・WebP は Skia、GIF・BMP・TIFF は WIC、QOI は自前で書く。
// 撮影情報（EXIF）は元のファイルから取り出して書き戻し、向き・縦横・縮小画像を保存する画像に合わせる。PNM は自前で読む
using System.Text;
using ImageViewer.Core.Editing;
using ImageViewer.Core.Imaging;

static class SaveTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        Directory.CreateDirectory(dir);
        string P(string name) => Path.Combine(dir, name);
        var source = new TestImage(48, 32, new Px(30, 140, 200)).Fill(Px.Red, 0, 0, 24, 32).Fill(new Px(0, 200, 0, 100), 30, 4, 10, 10);

        // ---- 形式ごとに保存して読み直す ----
        foreach (var (ext, tolerance, keepsAlpha) in new[] { (".png", 0, true), (".webp", 12, true), (".jpg", 40, false), (".bmp", 0, false),
                                                             (".gif", 40, false), (".tif", 0, true), (".qoi", 0, true) })
        {
            string path = P("saved" + ext);
            using (var image = source.ToApi()) ImageSaver.Save(image, path);
            using var back = ImageLoader.Load(path);
            var img = TestImage.FromApi(back);
            bool colors = img[5, 5].Near(Px.Red, Math.Max(tolerance, 1)) && img[45, 28].Near(new Px(30, 140, 200), Math.Max(tolerance, 1));
            bool alpha = !keepsAlpha || (img[34, 8].A is > 90 and < 110 && img[34, 8].Near(new Px(0, 200, 0, 100), Math.Max(tolerance, 2)));
            check(img.Width == 48 && img.Height == 32 && colors && alpha, $"保存して読み直す: {ext}（{img[5, 5]} / {img[34, 8]}）");
        }
        // 透明の無い画像は、そのまま（BMP・PNG・QOI・TIFF は値が変わらない。BMP は透明を持たない 24 ビットで書く）
        var opaque = new TestImage(20, 10, new Px(10, 20, 30)).Fill(new Px(250, 128, 3), 0, 0, 7, 10);
        foreach (var ext in new[] { ".png", ".bmp", ".qoi", ".tif" })
        {
            string path = P("opaque" + ext);
            using (var image = opaque.ToApi()) ImageSaver.Save(image, path);
            using var back = ImageLoader.Load(path);
            check(TestImage.FromApi(back).Rgba.AsSpan().SequenceEqual(opaque.Rgba), $"透明の無い画像は値がそのまま: {ext}");
        }
        // JPEG は透明を扱えないので白に重ねる（元の画像は変えない）
        {
            var clear = new TestImage(10, 10, new Px(0, 0, 0, 0));
            using var image = clear.ToApi();
            ImageSaver.Save(image, P("clear.jpg"));
            using var back = ImageLoader.Load(P("clear.jpg"));
            check(TestImage.FromApi(back)[5, 5].Near(Px.White, 6) && TestImage.FromApi(image)[5, 5].A == 0, "JPEG: 透明な所は白（元の画像は変えない）");
        }
        // 色プロファイルは付けない（画素は sRGB。#81 の決定）
        check(!Contains(P("saved.jpg"), "ICC_PROFILE") && !Contains(P("saved.webp"), "ICCP") && !Contains(P("saved.png"), "iCCP"),
            "JPEG・WebP・PNG に色プロファイルを付けない");
        check(!ImageFormats.IsSupported("a.tga") && !ImageSaver.CanWrite(".tga"), "TGA は対応をやめた（#81 の決定）");

        // ---- QOI: 自前の読み書き ----
        {
            var gradient = new TestImage(300, 3);
            for (int x = 0; x < 300; x++)
                for (int y = 0; y < 3; y++)
                    gradient[x, y] = new Px((byte)x, (byte)(x / 2 + y), (byte)(x * 3), (byte)(x % 7 == 0 ? 255 : 200 + y)); // 同じ色の続き・差分・半透明を含む
            using (var image = gradient.ToApi()) ImageSaver.Save(image, P("gradient.qoi"));
            using var back = ImageLoader.Load(P("gradient.qoi"));
            check(TestImage.FromApi(back).Rgba.AsSpan().SequenceEqual(gradient.Rgba), "QOI: 色の差分・続き・半透明を含めて値がそのまま戻る");
            check(ImageLoader.Identify(P("gradient.qoi")) == new ImageHeader(300, 3, "QOI"), "QOI: 大きさだけを読む");
            using var small = ImageLoader.Load(P("gradient.qoi"), LoadOptions.Thumbnail(100));
            check(small.Width == 100 && small.Height == 1, $"QOI: 縮めて読む（{small.Width}x{small.Height}）");
            var bytes = File.ReadAllBytes(P("gradient.qoi"));
            check(bytes.AsSpan(0, 4).SequenceEqual("qoif"u8) && bytes[12] == 4 && bytes.AsSpan(bytes.Length - 8).SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }),
                "QOI: ヘッダー（RGBA）と終わりの印");
        }

        // ---- PNM（P1〜P6）: 自前で読む ----
        {
            void Pnm(string name, byte[] data) => File.WriteAllBytes(P(name), data);
            Pnm("p1.pbm", Encoding.ASCII.GetBytes("P1\n# comment\n3 2\n1 0 1\n0 1 0\n"));
            Pnm("p2.pgm", Encoding.ASCII.GetBytes("P2 2 1 10\n0 10\n"));
            Pnm("p3.ppm", Encoding.ASCII.GetBytes("P3\n1 1\n255\n255 128 0\n"));
            Pnm("p4.pbm", Encoding.ASCII.GetBytes("P4\n10 1\n").Concat(new byte[] { 0b1000_0000, 0b0100_0000 }).ToArray());
            Pnm("p5.pgm", Encoding.ASCII.GetBytes("P5 2 1 65535\n").Concat(new byte[] { 0x00, 0x00, 0xFF, 0xFF }).ToArray());
            Pnm("p6.ppm", Encoding.ASCII.GetBytes("P6 2 1 255\n").Concat(new byte[] { 255, 0, 0, 0, 0, 255 }).ToArray());
            TestImage L(string name)
            {
                using var bitmap = ImageLoader.Load(P(name));
                return TestImage.FromApi(bitmap);
            }
            var p1 = L("p1.pbm");
            check(p1.Width == 3 && p1.Height == 2 && p1[0, 0] == Px.Black && p1[1, 0] == Px.White && p1[1, 1] == Px.Black, "PNM: P1（文字の白黒。1 が黒・コメント）");
            var p2 = L("p2.pgm");
            check(p2[0, 0] == Px.Black && p2[1, 0] == Px.White, "PNM: P2（文字の濃淡。最大値 10）");
            check(L("p3.ppm")[0, 0] == new Px(255, 128, 0), "PNM: P3（文字の RGB）");
            var p4 = L("p4.pbm");
            check(p4.Width == 10 && p4[0, 0] == Px.Black && p4[1, 0] == Px.White && p4[9, 0] == Px.Black, "PNM: P4（ビットの白黒。行の終わりはバイトまで詰める）");
            var p5 = L("p5.pgm");
            check(p5[0, 0] == Px.Black && p5[1, 0] == Px.White, "PNM: P5（16 ビットの濃淡）");
            var p6 = L("p6.ppm");
            check(p6[0, 0] == Px.Red && p6[1, 0] == Px.Blue, "PNM: P6（バイトの RGB）");
            check(ImageLoader.Identify(P("p6.ppm")) == new ImageHeader(2, 1, "PPM") && ImageLoader.Identify(P("p1.pbm")) == new ImageHeader(3, 2, "PBM"),
                "PNM: 大きさだけを読む（形式の名前は PBM・PGM・PPM）");
            File.WriteAllBytes(P("broken.ppm"), Encoding.ASCII.GetBytes("P6 99999 99999 255\n"));
            check(Throws(() => ImageLoader.Load(P("broken.ppm")).Dispose()), "PNM: 大きさがおかしいものは読まない");
        }

        // ---- 撮影情報（EXIF）を書き戻す ----
        string photo = P("exif.jpg");
        new TestImage(40, 20, new Px(90, 90, 90)).Fill(Px.Red, 0, 0, 20, 20).Save(photo, new TestExif { Model = "TestCam", Artist = "someone" });
        foreach (var format in new[] { OutputFormat.Png, OutputFormat.Webp, OutputFormat.Jpeg })
        {
            var opts = new ConvertOptions { Format = format, StripMetadata = false, Suffix = "_" + format };
            var result = Converter.Run(Converter.Plan(new[] { photo }, opts), opts);
            string output = result.Converted == 1 ? Converter.Plan(new[] { photo }, opts)[0].Target : "";
            string? camera = result.Converted == 1 ? ImageLoader.Identify(output)?.Photo?.Camera : null;
            check(camera == "TestCam", $"変換: 撮影情報を残す（{format}、{camera ?? string.Join(", ", result.Errors)}）");
        }
        {
            var opts = new ConvertOptions { Format = OutputFormat.Png, StripMetadata = true, Suffix = "_strip" };
            Converter.Run(Converter.Plan(new[] { photo }, opts), opts);
            check(ImageLoader.Identify(Converter.Plan(new[] { photo }, opts)[0].Target)?.Photo == null, "変換: メタデータを消す設定なら撮影情報を書かない");
        }

        // 向き 6・縦横の値・縮小画像（IFD1）を持つ EXIF を作り、回して保存すると、向き 1・回した後の縦横・縮小画像なしになる
        string crafted = P("crafted.jpg");
        new TestImage(40, 20, new Px(90, 90, 90)).Fill(Px.Red, 0, 0, 20, 20).Save(crafted);
        File.WriteAllBytes(crafted, WithExif(File.ReadAllBytes(crafted), CraftedTiff(orientation: 6, width: 40, height: 20)));
        var before = Exif(crafted);
        check(before is { Orientation: 6, Width: 40, Height: 20, HasThumbnail: true }, $"テスト画像: 向き 6・縦横・縮小画像の EXIF（{before}）");
        Rotator.RotateFile(crafted, RotateDirection.Right90); // 読むと 20×40（向きを直す）→ 右に回して 40×20
        var after = Exif(crafted);
        using (var rotated = ImageLoader.Load(crafted))
            check(after is { Orientation: 1, Width: 40, Height: 20, HasThumbnail: false } && rotated.Width == 40 && rotated.Height == 20,
                $"保存すると、EXIF の向きは 1・縦横は保存した画像の大きさ・縮小画像は外す（{after}）");
        Cropper.CropCenter(crafted, 1.0, P("crafted_square.png"));
        var square = Exif(P("crafted_square.png"));
        check(square is { Orientation: 1, Width: 20, Height: 20, HasThumbnail: false }, $"PNG に保存しても EXIF を直して書く（{square}）");
    }

    /// <summary>EXIF の中身（MetadataExtractor で読む）</summary>
    sealed record ExifValues(int? Orientation, int? Width, int? Height, bool HasThumbnail);

    static ExifValues? Exif(string path)
    {
        var dirs = MetadataExtractor.ImageMetadataReader.ReadMetadata(path);
        var ifd0 = dirs.OfType<MetadataExtractor.Formats.Exif.ExifIfd0Directory>().FirstOrDefault();
        var sub = dirs.OfType<MetadataExtractor.Formats.Exif.ExifSubIfdDirectory>().FirstOrDefault();
        if (ifd0 == null) return null;
        int? Int(MetadataExtractor.Directory? d, int tag) => d != null && d.ContainsTag(tag) ? MetadataExtractor.DirectoryExtensions.GetInt32(d, tag) : null;
        return new ExifValues(Int(ifd0, 0x0112), Int(sub, 0xA002), Int(sub, 0xA003),
            dirs.OfType<MetadataExtractor.Formats.Exif.ExifThumbnailDirectory>().Any());
    }

    /// <summary>
    /// 小さな EXIF（TIFF の形・リトルエンディアン）: IFD0 に向き・EXIF の IFD への参照、EXIF の IFD に縦横（LONG と SHORT）、
    /// その後に縮小画像の IFD（IFD1）
    /// </summary>
    static byte[] CraftedTiff(ushort orientation, uint width, ushort height)
    {
        using var m = new MemoryStream();
        using var w = new BinaryWriter(m);
        w.Write("II"u8);
        w.Write((ushort)42);
        w.Write(8u);
        // IFD0（8〜37）
        w.Write((ushort)2);
        Entry(w, 0x0112, 3, orientation);
        Entry(w, 0x8769, 4, 38);
        w.Write(68u); // 次の IFD（IFD1）
        // EXIF の IFD（38〜67）
        w.Write((ushort)2);
        Entry(w, 0xA002, 4, width);
        Entry(w, 0xA003, 3, height);
        w.Write(0u);
        // IFD1（68〜85）: 圧縮の項目だけ（縮小画像のある IFD として読まれる）
        w.Write((ushort)1);
        Entry(w, 0x0103, 3, 6);
        w.Write(0u);
        return m.ToArray();
    }

    static void Entry(BinaryWriter w, ushort tag, ushort type, uint value)
    {
        w.Write(tag);
        w.Write(type);
        w.Write(1u);
        if (type == 3)
        {
            w.Write((ushort)value);
            w.Write((ushort)0);
        }
        else w.Write(value);
    }

    /// <summary>JPEG の先頭に EXIF の APP1 を入れる</summary>
    static byte[] WithExif(byte[] jpeg, byte[] tiff)
    {
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        int length = payload.Length + 2;
        return jpeg.Take(2).Concat(new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }).Concat(payload).Concat(jpeg.Skip(2)).ToArray();
    }

    static bool Contains(string path, string ascii) => File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.ASCII.GetBytes(ascii)) >= 0;

    static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (Exception) { return true; }
    }
}
