// 編集の API を SKBitmap で受け渡したときの動作確認（#81 手順4・7）。
// 画素の値が変わらないこと、保存するときは元のファイルからメタデータを読み直すこと、Skia に移した処理（ぼかし・縮小・回転）を確かめる
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

        // ---- 画素: 何も描かなければ、半透明も含めて値は変わらない ----
        {
            var gradient = new TestImage(256, 4);
            for (int x = 0; x < 256; x++)
                for (int y = 0; y < 4; y++)
                    gradient[x, y] = new Px((byte)x, (byte)(255 - x), (byte)(x * 7), (byte)(y * 60 + 15));
            using var bitmap = gradient.ToApi();
            Annotator.Draw(bitmap, Array.Empty<Annotation>()); // 何も描かない（写して書き戻すだけ）
            check(TestImage.FromApi(bitmap).Rgba.AsSpan().SequenceEqual(gradient.Rgba), "SKBitmap: 何も描かなければ、半透明も含めて画素の値が変わらない");
        }
        var marks = new[] { new Annotation(AnnotationKind.Frame, 5, 5, 50, 30, new Px(0, 200, 0), 4, 0, 4, true) };

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
        using (var fresh = source.ToApi())
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

        // ---- Skia に移した処理（#81 手順7） ----
        // ぼかしは乗算済みの値で平均する: 透明な所の色（ここでは青）が、赤い所ににじまない
        foreach (var effect in new[] { MaskEffect.Blur, MaskEffect.BoxBlur })
        {
            var half = new TestImage(40, 20, new Px(0, 0, 255, 0)).Fill(Px.Red, 0, 0, 20, 20);
            using var bitmap = half.ToApi();
            Masker.Apply(bitmap, new[] { MaskRegion.Rect(0, 0, 40, 20) }, effect, 8);
            var edge = TestImage.FromApi(bitmap)[21, 10];
            check(edge.A is > 0 and < 255 && edge.R > 250 && edge.B < 5, $"{effect}: 透明な所の色がにじまない（境目のすぐ外 {edge}）");
            var flat = new TestImage(30, 30, new Px(10, 120, 200));
            using var uniform = flat.ToApi();
            Masker.Apply(uniform, new[] { MaskRegion.Rect(5, 5, 25, 25) }, effect, 8);
            check(TestImage.FromApi(uniform).Rgba.AsSpan().SequenceEqual(flat.Rgba), $"{effect}: 一色の所は一色のまま（範囲の端も、外の画素を混ぜない）");
        }
        // 回転は画素を並べ替えるだけ（値は変わらない）
        {
            var marked = new TestImage(3, 2, Px.White).Fill(Px.Red, 0, 0, 1, 1); // 左上が赤
            string path = P("turn.png");
            marked.Save(path);
            Rotator.RotateFile(path, RotateDirection.Right90);
            var right = TestImage.Load(path);
            Rotator.RotateFile(path, RotateDirection.Half);
            var half = TestImage.Load(path);
            Rotator.RotateFile(path, RotateDirection.Left90);
            var back = TestImage.Load(path);
            check(right.Width == 2 && right[1, 0] == Px.Red && half[0, 2] == Px.Red && back.Rgba.AsSpan().SequenceEqual(new TestImage(3, 2, Px.White).Fill(Px.Red, 2, 1, 1, 1).Rgba),
                "回転: 右・180°・左で、左上の赤が右上・左下・右下へ動く（値はそのまま）");
        }
        // 縮小の方法（Skia の補間。Lanczos は Catmull-Rom で代える）: どれも指定の大きさで、左の赤・右の青は保つ
        string split = P("split.png");
        new TestImage(800, 400, Px.Blue).Fill(Px.Red, 0, 0, 400, 400).Save(split);
        foreach (var algorithm in new[] { ResizeAlgorithm.Bilinear, ResizeAlgorithm.Bicubic, ResizeAlgorithm.Lanczos })
        {
            string dst = P($"small_{algorithm}.png");
            Converter.ConvertOne(split, dst, new ConvertOptions { LongEdge = 100, Format = OutputFormat.Png, Algorithm = algorithm });
            var small = TestImage.Load(dst);
            check(small.Width == 100 && small.Height == 50 && small[10, 25] == Px.Red && small[90, 25] == Px.Blue, $"縮小（{algorithm}）: 100x50 で左が赤・右が青");
        }
    }
}
