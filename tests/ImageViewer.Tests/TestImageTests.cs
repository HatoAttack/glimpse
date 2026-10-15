// テスト用の画像（TestImage）そのものの動作確認。ほかのテストの「正解」を作る道具なので、先に確かめる
using ImageViewer.Core.Imaging;

static class TestImageTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        string P(string name) => Path.Combine(dir, name);

        // PNG は透明も含めて画素がそのまま戻る
        var img = new TestImage(4, 3, Px.Red);
        img[1, 1] = new Px(10, 20, 30, 128);
        img[3, 2] = Px.Transparent;
        img.Save(P("t.png"));
        var back = TestImage.Load(P("t.png"));
        check(back.Width == 4 && back.Height == 3 && back.Rgba.SequenceEqual(img.Rgba), "TestImage: PNG は透明も含めて画素がそのまま戻る");

        // Core の API の画像との受け渡し
        using (var api = img.ToApi())
            check(TestImage.FromApi(api).Rgba.SequenceEqual(img.Rgba), "TestImage: Core の API の画像と行き来しても画素が変わらない");

        // JPEG と EXIF: WIC で書いた EXIF を、アプリが本物のカメラの写真と同じく読める
        var exif = new TestExif
        {
            Orientation = 6, Make = "Canon", Model = "Canon EOS R5", Artist = "someone", Taken = "2026:09:10 10:21:05",
            Lens = "RF24-70mm F2.8 L IS USM", FNumber = (28, 10), Exposure = (1, 250), Iso = 200,
        };
        new TestImage(64, 32, Px.Blue).Fill(Px.Red, 0, 0, 32, 32).Save(P("t.jpg"), exif);
        var read = TestImage.ReadExif(P("t.jpg"));
        check(read is { Orientation: 6, Model: "Canon EOS R5", Artist: "someone", Make: "Canon" }, $"TestImage: JPEG の EXIF を WIC で読み直せる（{read}）");
        var info = ImageLoader.Identify(P("t.jpg"));
        check(info is { Width: 32, Height: 64 } && info.Photo is { Camera: "Canon EOS R5", Lens: "RF24-70mm F2.8 L IS USM" }
              && info.Photo.TakenAt == new DateTime(2026, 9, 10, 10, 21, 5) && info.Photo.SettingsText == "f/2.8 · 1/250 · ISO 200",
            $"TestImage: WIC で書いた EXIF をアプリが読める（向き・撮影日時・カメラ・レンズ・撮影設定。{info} / {info?.Photo?.SettingsText}）");
        check(TestImage.ReadExif(P("t.png")) == null, "TestImage: EXIF の無い画像は null");
        var jpg = TestImage.Load(P("t.jpg"));
        check(jpg[5, 5].Near(Px.Red) && jpg[60, 5].Near(Px.Blue), "TestImage: JPEG の画素は近い色で戻る（向きは直さない）");

        // GIF のコマと表示時間、TIFF のページ
        var frames = new[] { new TestImage(10, 10, Px.Red), new TestImage(10, 10, Px.Green), new TestImage(10, 10, Px.Blue) };
        TestImage.SaveFrames(P("t.gif"), frames, new[] { 0, 20, 50 });
        check(TestImage.FrameCount(P("t.gif")) == 3 && TestImage.GifDelays(P("t.gif")).SequenceEqual(new[] { 0, 20, 50 })
              && TestImage.Load(P("t.gif"), 2)[5, 5] == Px.Blue,
            $"TestImage: GIF のコマと表示時間（{string.Join(",", TestImage.GifDelays(P("t.gif")))}）");
        TestImage.SaveFrames(P("t.tif"), frames.Take(2).ToList());
        check(TestImage.FrameCount(P("t.tif")) == 2 && TestImage.Load(P("t.tif"), 1)[0, 0] == Px.Green, "TestImage: TIFF のページ");
        check(TestImage.SizeOf(P("t.jpg")) == (64, 32) && TestImage.SizeOf(P("none.png")) == (-1, -1), "TestImage: 縦横（向きは反映しない）・無いファイル");
    }
}
