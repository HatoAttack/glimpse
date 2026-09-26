// 色調補正（レベル補正・明るさ・コントラスト・彩度・自動補正）の動作確認
using ImageViewer.Core.Editing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

static class AdjustTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        Directory.CreateDirectory(dir);
        string P(string name) => Path.Combine(dir, name);

        // ---- 表 ----
        var identity = Adjuster.BuildToneTable(new AdjustOptions());
        check(Enumerable.Range(0, 256).All(i => identity[i] == i), "既定の値では何も変えない");
        check(new AdjustOptions().IsIdentity && !new AdjustOptions { Saturation = 1 }.IsIdentity, "何も変えない値の判定");

        var levels = Adjuster.BuildToneTable(new AdjustOptions { BlackPoint = 20, WhitePoint = 200 });
        check(levels[10] == 0 && levels[20] == 0 && levels[200] == 255 && levels[250] == 255, "レベル: 黒点より暗いと黒、白点より明るいと白");
        check(levels[110] is >= 127 and <= 128, "レベル: 黒点と白点の真ん中は中間の灰色");
        check(Adjuster.BuildToneTable(new AdjustOptions { Gamma = 2 })[128] > 170, "レベル: ガンマ 2 で中間が明るくなる");

        foreach (int b in new[] { -100, -50, 50, 100 })
        {
            var t = Adjuster.BuildToneTable(new AdjustOptions { Brightness = b });
            bool monotonic = Enumerable.Range(1, 255).All(i => t[i] >= t[i - 1]);
            check(t[0] == 0 && t[255] == 255 && monotonic && (b > 0 ? t[128] > 128 : t[128] < 128),
                $"明るさ {b}: 黒と白はそのまま、中間だけ動き、明るさの順は変わらない");
        }

        var high = Adjuster.BuildToneTable(new AdjustOptions { Contrast = 50 });
        var low = Adjuster.BuildToneTable(new AdjustOptions { Contrast = -50 });
        check(high[64] < 64 && high[192] > 192 && high[128] is >= 127 and <= 129, "コントラスト +: 暗いところはより暗く、明るいところはより明るく");
        check(low[0] > 0 && low[255] < 255, "コントラスト -: 黒と白が灰色に寄る");

        var wild = new AdjustOptions { Brightness = 500, Temperature = -300, BlackPoint = 300, WhitePoint = -5, Gamma = double.NaN }.Normalize();
        check(wild.Brightness == 100 && wild.Temperature == -100 && wild.BlackPoint == 254 && wild.WhitePoint == 255 && wild.Gamma == 1, "範囲外の値は丸める");

        // ---- 彩度 ----
        using (var img = new Image<Rgba32>(1, 1, new Rgba32(200, 80, 40, 128)))
        {
            Adjuster.Apply(img, new AdjustOptions { Saturation = -100 });
            var p = img[0, 0];
            check(p.R == p.G && p.G == p.B && p.A == 128, $"彩度 -100 で白黒、透明度はそのまま（{p}）");
        }
        using (var img = new Image<Rgba32>(1, 1, new Rgba32(160, 120, 100)))
        {
            Adjuster.Apply(img, new AdjustOptions { Saturation = 100 });
            var p = img[0, 0];
            check(p.R - p.B > 60, $"彩度 +100 で色の差が広がる（{p}）");
        }

        // ---- 色温度 ----
        using (var img = new Image<Rgba32>(2, 1, new Rgba32(128, 128, 128)))
        {
            img[1, 0] = new Rgba32(255, 255, 255);
            Adjuster.Apply(img, new AdjustOptions { Temperature = 100 });
            var gray = img[0, 0];
            check(gray.R > 128 && gray.G == 128 && gray.B < 128, $"色温度 +: 赤みが増えて青みが減る（{gray}）");
            check(img[1, 0].R == 255 && img[1, 0].B < 255, "色温度 +: 白も暖かい色になる（赤は 255 で止まる）");
        }
        using (var img = new Image<Rgba32>(1, 1, new Rgba32(128, 128, 128)))
        {
            Adjuster.Apply(img, new AdjustOptions { Temperature = -100 });
            check(img[0, 0].R < 128 && img[0, 0].B > 128, $"色温度 -: 青みが増える（{img[0, 0]}）");
        }

        // ---- BGRA（プレビュー用）と ImageSharp の結果が同じ ----
        var opts = new AdjustOptions { Brightness = 30, Contrast = -20, Saturation = 40, Temperature = 25, BlackPoint = 10, WhitePoint = 240, Gamma = 1.3 };
        using (var img = new Image<Rgba32>(3, 2))
        {
            var colors = new[] { new Rgba32(255, 0, 0), new Rgba32(10, 200, 90), new Rgba32(128, 128, 128), new Rgba32(0, 0, 0), new Rgba32(250, 250, 5), new Rgba32(33, 66, 99) };
            int stride = 3 * 4 + 4; // 行の終わりの詰め物
            var bgra = new byte[stride * 2];
            for (int i = 0; i < 6; i++)
            {
                img[i % 3, i / 3] = colors[i];
                int o = (i / 3) * stride + (i % 3) * 4;
                (bgra[o], bgra[o + 1], bgra[o + 2], bgra[o + 3]) = (colors[i].B, colors[i].G, colors[i].R, 255);
            }
            bgra[12] = 77; // 詰め物は触らない
            Adjuster.Apply(img, opts);
            Adjuster.ApplyBgra(bgra, 3, 2, stride, opts);
            bool same = Enumerable.Range(0, 6).All(i =>
            {
                var p = img[i % 3, i / 3];
                int o = (i / 3) * stride + (i % 3) * 4;
                return bgra[o] == p.B && bgra[o + 1] == p.G && bgra[o + 2] == p.R;
            });
            check(same && bgra[12] == 77, "BGRA の並びでも同じ結果（行の詰め物は触らない）");
        }

        // ---- 自動補正 ----
        using (var img = new Image<Rgba32>(100, 10))
        {
            for (int x = 0; x < 100; x++)
                for (int y = 0; y < 10; y++)
                    img[x, y] = new Rgba32((byte)(60 + x), (byte)(60 + x), (byte)(60 + x)); // 60〜159 の眠い画像
            var auto = Adjuster.Auto(img);
            check(auto.BlackPoint is >= 60 and <= 62 && auto.WhitePoint is >= 157 and <= 159 && auto.Brightness == 0,
                $"自動補正: 両端を黒と白に広げる（{auto.BlackPoint}〜{auto.WhitePoint}, γ{auto.Gamma}）");
            Adjuster.Apply(img, auto);
            check(img[0, 0].R == 0 && img[99, 0].R == 255, "自動補正: かけると黒から白まで使う");
        }
        check(Adjuster.AutoFromHistogram(new long[256]).IsIdentity, "自動補正: 画素が無ければ何もしない");
        var flat = new long[256];
        flat[100] = 1000;
        check(Adjuster.AutoFromHistogram(flat).IsIdentity, "自動補正: ほぼ一色の画像は触らない");
        var dark = new long[256];
        for (int i = 0; i < 256; i++) dark[i] = i < 64 ? 100 : 1; // 暗い画素が多い
        var darkAuto = Adjuster.AutoFromHistogram(dark);
        check(darkAuto.Gamma > 1 && darkAuto.Gamma <= Adjuster.AutoMaxGamma, $"自動補正: 暗い画像は中間を持ち上げる（γ{darkAuto.Gamma}）");

        // ---- ファイル: 上書きしても EXIF が残る ----
        using (var img = new Image<Rgba32>(20, 10, new Rgba32(100, 100, 100)))
        {
            img.Metadata.ExifProfile = new ExifProfile();
            img.Metadata.ExifProfile.SetValue(ExifTag.Model, "TestCam");
            img.SaveAsJpeg(P("photo.jpg"));
        }
        Adjuster.ApplyToFile(P("photo.jpg"), P("photo.jpg"), new AdjustOptions { Brightness = 50 });
        using (var img = Image.Load<Rgba32>(P("photo.jpg")))
        {
            bool exif = img.Metadata.ExifProfile?.TryGetValue(ExifTag.Model, out var model) == true && model.Value == "TestCam";
            check(img[5, 5].R > 110 && exif, "ファイル: 上書きで補正がかかり、EXIF は残る");
        }
        check(Directory.GetFiles(dir, "*.tmp").Length == 0, "ファイル: 一時ファイルを残さない");

        // ---- ファイル: アニメは保存しない（先頭のコマだけの静止画になってしまうため） ----
        using (var anim = new Image<Rgba32>(8, 8, new Rgba32(255, 0, 0)))
        {
            anim.Frames.AddFrame(new Image<Rgba32>(8, 8, new Rgba32(0, 0, 255)).Frames.RootFrame);
            anim.SaveAsGif(P("anim.gif"));
        }
        byte[] gifBefore = File.ReadAllBytes(P("anim.gif"));
        bool animRefused = false;
        try { Adjuster.ApplyToFile(P("anim.gif"), P("anim.gif"), new AdjustOptions { Brightness = 30 }); }
        catch (NotSupportedException) { animRefused = true; }
        check(animRefused && File.ReadAllBytes(P("anim.gif")).SequenceEqual(gifBefore), "ファイル: アニメは補正して保存しない（元のまま）");

        using (var pages = new Image<Rgba32>(8, 8, new Rgba32(255, 0, 0)))
        {
            pages.Frames.AddFrame(new Image<Rgba32>(8, 8, new Rgba32(0, 255, 0)).Frames.RootFrame);
            pages.SaveAsTiff(P("pages.tif"));
        }
        byte[] tifBefore = File.ReadAllBytes(P("pages.tif"));
        bool tifRefused = false;
        try { Adjuster.ApplyToFile(P("pages.tif"), P("pages.tif"), new AdjustOptions { Brightness = 30 }); }
        catch (NotSupportedException) { tifRefused = true; }
        check(Adjuster.FrameCount(P("pages.tif")) == 2 && tifRefused && File.ReadAllBytes(P("pages.tif")).SequenceEqual(tifBefore),
            "ファイル: 複数ページの TIFF も補正して保存しない（元のまま）");
        check(Adjuster.FrameCount(P("photo.jpg")) == 1, "コマの数: 静止画は 1");
        check(ImageViewer.Core.Imaging.ImageLoader.WicFrameCount(P("pages.tif")) == 2, "コマの数: WIC でも複数ページの TIFF を数えられる（ImageSharp で読めない亜種用）");

        // ---- ファイル: WIC で読む画像はメタデータを残せないので、許可が無ければ保存しない ----
        string jxr = P("wic.jxr");
        if (!ImageViewer.Core.Imaging.WicCodecs.DecoderExtensions.Contains(".jxr"))
        {
            Console.WriteLine("SKIP 色調補正の WIC 画像: この PC に JPEG XR のデコーダが無い");
            return;
        }
        LoaderTests.EncodeAsync(jxr, Windows.Graphics.Imaging.BitmapEncoder.JpegXREncoderId, 40, 20, null).GetAwaiter().GetResult();
        byte[] before = File.ReadAllBytes(jxr);
        bool refused = false;
        try { Adjuster.ApplyToFile(jxr, P("wic.jpg"), new AdjustOptions { Brightness = 30 }); }
        catch (MetadataLossException) { refused = true; }
        check(refused && !File.Exists(P("wic.jpg")) && File.ReadAllBytes(jxr).SequenceEqual(before),
            "ファイル: WIC で読む画像は、許可が無ければメタデータが消えるので保存しない");
        Adjuster.ApplyToFile(jxr, P("wic.jpg"), new AdjustOptions { Brightness = 30 }, allowMetadataLoss: true);
        check(File.Exists(P("wic.jpg")), "ファイル: 許可すれば WIC で読む画像も保存できる");
        check(Adjuster.FrameCount(jxr) == 1, "コマの数: WIC で読む形式も数える");
    }
}
