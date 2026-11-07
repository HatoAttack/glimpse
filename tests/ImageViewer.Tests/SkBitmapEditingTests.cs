// 編集の API を SKBitmap で受け渡したときの動作確認（#81 手順4）。
// 画素は ImageSharp の画像で受け渡したときと同じになること、保存するときは元のファイルからメタデータを読み直すことを確かめる
using ImageViewer.Core.Editing;
using ImageViewer.Core.Imaging;
using SkiaSharp;

static class SkBitmapEditingTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        Directory.CreateDirectory(dir);
        string P(string name) => Path.Combine(dir, name);
        var source = new TestImage(60, 40, new Px(90, 120, 150)).Fill(Px.Red, 0, 0, 30, 40).Fill(new Px(0, 0, 255, 128), 40, 10, 10, 10);

        // ---- 画素: ImageSharp の画像で受け渡したときと同じ ----
        bool Same(Action<SKBitmap> skia, Action<SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>> imageSharp)
        {
            using var a = source.ToSkia();
            using var b = source.ToApi();
            skia(a);
            imageSharp(b);
            return TestImage.FromApi(a).Rgba.AsSpan().SequenceEqual(TestImage.FromApi(b).Rgba);
        }
        var adjust = new AdjustOptions { Brightness = 30, Saturation = -40 };
        check(Same(b => Adjuster.Apply(b, adjust), i => Adjuster.Apply(i, adjust)), "SKBitmap: 補正の結果が同じ（半透明も）");
        using (var a = source.ToSkia())
        using (var b = source.ToApi())
            check(Adjuster.Auto(a) == Adjuster.Auto(b), "SKBitmap: 自動補正の値が同じ");
        var regions = new[] { MaskRegion.Rect(5, 5, 35, 30), new MaskRegion(MaskShape.Ellipse, 30, 0, 60, 40) };
        foreach (var effect in new[] { MaskEffect.Mosaic, MaskEffect.Blur, MaskEffect.Fill })
            check(Same(b => Masker.Apply(b, regions, effect, 6), i => Masker.Apply(i, regions, effect, 6)), $"SKBitmap: モザイク・ぼかしの結果が同じ（{effect}）");
        var marks = new[] { new Annotation(AnnotationKind.Frame, 5, 5, 50, 30, new SixLabors.ImageSharp.PixelFormats.Rgba32(0, 200, 0), 4, 0, 4, true) };
        check(Same(b => Annotator.Draw(b, marks), i => Annotator.Draw(i, marks)), "SKBitmap: 枠の結果が同じ");
        using (var a = source.ToSkia())
        using (var b = new TestImage(30, 50, Px.Blue).ToSkia())
        using (var a2 = source.ToApi())
        using (var b2 = new TestImage(30, 50, Px.Blue).ToApi())
        {
            var options = new CombineOptions { Spacing = 4, Padding = 2 };
            using var combined = Combiner.Combine(new[] { a, b }, options);
            using var expected = Combiner.Combine(new[] { a2, b2 }, options);
            check(TestImage.FromApi(combined).Rgba.AsSpan().SequenceEqual(TestImage.FromApi(expected).Rgba), "SKBitmap: 連結の結果が同じ");
            using var bounded = Combiner.CombineBounded(new[] { a, b }, options, 40);
            check(Math.Max(bounded.Width, bounded.Height) <= 40, $"SKBitmap: 縮めて連結する（{bounded.Width}×{bounded.Height}）");
        }

        // ---- 保存: SKBitmap にはメタデータが無いので、元のファイルから読み直す ----
        // 向き 6（右に 90° 回して見る）の写真を読む。読んだ画像は回転を直してあるので、保存すると向きは「そのまま」になる
        string photo = P("photo.jpg");
        new TestImage(40, 20, new Px(90, 90, 90)).Save(photo, new TestExif { Orientation = 6, Model = "TestCam" });
        using (var loaded = ImageLoader.Load(photo))
        {
            check(loaded.Width == 20 && loaded.Height == 40, $"SKBitmap: 向きを直して読む（{loaded.Width}×{loaded.Height}）");
            string cropped = Cropper.SaveCrop(loaded, new SKRectI(0, 0, 20, 30), photo, P("photo_crop.jpg"));
            var exif = TestImage.ReadExif(cropped);
            check(TestImage.SizeOf(cropped) == (20, 30) && exif?.Model == "TestCam" && (exif.Orientation ?? 1) == 1,
                $"SKBitmap: 切り抜いて保存すると、元のファイルの撮影情報を残し、向きは「そのまま」（{exif?.Model}・向き {exif?.Orientation}）");

            var before = TestImage.FromApi(loaded);
            string masked = Masker.SaveMasked(loaded, new[] { MaskRegion.Rect(0, 0, 10, 10) }, MaskEffect.Mosaic, 5, photo, P("photo_mask.jpg"));
            string marked = Annotator.SaveAnnotated(loaded, marks, photo, P("photo_mark.jpg"));
            check(TestImage.FromApi(loaded).Rgba.AsSpan().SequenceEqual(before.Rgba), "SKBitmap: 保存しても表示中の画像は変えない");
            check(TestImage.ReadExif(masked)?.Model == "TestCam" && TestImage.ReadExif(marked)?.Model == "TestCam",
                "SKBitmap: モザイク・枠を保存しても撮影情報を残す");

            // 上書き（保存先が元のファイル）
            Cropper.SaveCrop(loaded, new SKRectI(0, 10, 20, 40), photo, photo);
            exif = TestImage.ReadExif(photo);
            check(TestImage.SizeOf(photo) == (20, 30) && exif?.Model == "TestCam" && (exif.Orientation ?? 1) == 1,
                "SKBitmap: 元のファイルに上書きしても撮影情報を残し、向きは「そのまま」");

            bool outside = false;
            try { Cropper.SaveCrop(loaded, new SKRectI(10, 10, 10, 20), photo, P("empty.jpg")); }
            catch (ArgumentException) { outside = true; }
            check(outside && !File.Exists(P("empty.jpg")), "SKBitmap: 空の範囲は切り抜かない");
        }

        // メタデータの無い画像（新しく作った画像）を、元のファイルを指定せずに保存すると、メタデータは付かない
        using (var fresh = source.ToSkia())
        {
            ImageSaver.Save(fresh, P("fresh.jpg"));
            check(!TestImage.HasExif(P("fresh.jpg")), "SKBitmap: 元のファイルを指定しなければメタデータを付けない");
        }

        // WIC で読む画像（TIFF）は撮影情報を残せないので、上書きしない
        new TestImage(20, 10, Px.Red).Save(P("one.tif"));
        byte[] tiff = File.ReadAllBytes(P("one.tif"));
        bool refused = false;
        using (var loaded = ImageLoader.Load(P("one.tif")))
        {
            try { Cropper.SaveCrop(loaded, new SKRectI(0, 0, 10, 10), P("one.tif"), P("one.tif")); }
            catch (NotSupportedException) { refused = true; }
        }
        check(refused && File.ReadAllBytes(P("one.tif")).SequenceEqual(tiff), "SKBitmap: 撮影情報を残せない画像（TIFF）は上書きしない（元のまま）");
    }
}
