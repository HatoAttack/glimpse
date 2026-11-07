// 編集の API を SKBitmap で受け渡したときの動作確認（#81 手順4）。
// 受け渡しで画素の値が変わらないこと、保存するときは元のファイルからメタデータを読み直すことを確かめる
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

        // ---- 画素: 移行中は SKBitmap を ImageSharp の画像に写して処理し書き戻すが、その受け渡しで値は変わらない ----
        {
            var gradient = new TestImage(256, 4);
            for (int x = 0; x < 256; x++)
                for (int y = 0; y < 4; y++)
                    gradient[x, y] = new Px((byte)x, (byte)(255 - x), (byte)(x * 7), (byte)(y * 60 + 15));
            using var bitmap = gradient.ToApi();
            Annotator.Draw(bitmap, Array.Empty<Annotation>()); // 何も描かない（写して書き戻すだけ）
            check(TestImage.FromApi(bitmap).Rgba.AsSpan().SequenceEqual(gradient.Rgba), "SKBitmap: 処理の受け渡しで、半透明も含めて画素の値が変わらない");
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
    }
}
