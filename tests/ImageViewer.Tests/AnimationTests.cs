// GIF / WEBP アニメの読み込みとフレーム保存の動作確認
using ImageViewer.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

static class AnimationTests
{
    static readonly Rgba32 Red = new(255, 0, 0), Blue = new(0, 0, 255), Green = new(0, 200, 0);

    public static void Run(Action<bool, string> check, string dir)
    {
        // 3 コマ: 赤 → 左半分だけ青 → 緑。表示時間は 0（→ 0.1 秒扱い）/ 0.2 秒 / 0.5 秒
        string gif = Path.Combine(dir, "anim.gif");
        using (var img = MakeAnimation(80, 40))
        {
            for (int i = 0; i < 3; i++) img.Frames[i].Metadata.GetGifMetadata().FrameDelay = new[] { 0, 20, 50 }[i];
            img.SaveAsGif(gif, new GifEncoder());
        }
        check(AnimationLoader.FrameCount(gif) == 3, $"GIF のコマ数（{AnimationLoader.FrameCount(gif)}）");

        using (var anim = AnimationLoader.Load(gif, maxEdge: 1000))
        {
            check(anim != null && anim.Count == 3 && anim.Image.Width == 80, "GIF: 全部のコマを原寸のまま読む（画面より小さい）");
            check(anim != null && anim.DelaysMs.SequenceEqual(new[] { 100, 200, 500 }), $"GIF: 表示時間（{string.Join(",", anim?.DelaysMs ?? Array.Empty<int>())}）");
            // 2 コマ目は左半分だけ変えたもの。右半分は前のコマ（赤）が残っていること（重ね合わせ済み）
            var f = anim?.Image.Frames[1];
            check(f != null && Near(f[10, 20], Blue) && Near(f[70, 20], Red), "GIF: 差分だけのコマも重ね合わせた絵になる");
            check(anim != null && Near(anim.Image.Frames[2][40, 20], Green), "GIF: 3 コマ目");
        }

        using (var anim = AnimationLoader.Load(gif, maxEdge: 20))
            check(anim != null && anim.Image.Width == 20 && anim.Image.Height == 10, $"GIF: 画面の大きさに縮める（{anim?.Image.Width}x{anim?.Image.Height}）");

        string still = Path.Combine(dir, "still.gif");
        using (var img = new Image<Rgba32>(20, 20, Red)) img.SaveAsGif(still);
        check(AnimationLoader.Load(still, 1000) == null, "コマが 1 つの GIF はアニメとして扱わない");
        check(!AnimationLoader.MayBeAnimated("a.png") && AnimationLoader.MayBeAnimated("A.WEBP"), "アニメかもしれない形式（GIF / WEBP）");

        // ---- WEBP ----
        string webp = Path.Combine(dir, "anim.webp");
        using (var img = MakeAnimation(80, 40))
        {
            for (int i = 0; i < 3; i++) img.Frames[i].Metadata.GetWebpMetadata().FrameDelay = (uint)new[] { 5, 200, 500 }[i];
            img.SaveAsWebp(webp, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
        }
        using (var anim = AnimationLoader.Load(webp, maxEdge: 1000))
        {
            check(anim != null && anim.Count == 3, $"WEBP: コマ数（{anim?.Count}）");
            check(anim != null && anim.DelaysMs.SequenceEqual(new[] { 100, 200, 500 }), $"WEBP: 表示時間（{string.Join(",", anim?.DelaysMs ?? Array.Empty<int>())}）");
            var f = anim?.Image.Frames[1];
            check(f != null && Near(f[10, 20], Blue) && Near(f[70, 20], Red), "WEBP: 2 コマ目");
        }

        // EXIF の Orientation=6（時計回り 90° で正立）: 元の左半分が上に来る。先頭のコマの静止画と同じ向きになること
        string rotated = Path.Combine(dir, "rotated.webp");
        using (var img = MakeAnimation(80, 40))
        {
            img.Metadata.ExifProfile = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
            img.Metadata.ExifProfile.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)6);
            img.SaveAsWebp(rotated, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
        }
        using (var first = ImageLoader.Load(rotated))
        using (var anim = AnimationLoader.Load(rotated, maxEdge: 1000))
        {
            var f = anim?.Image.Frames[1];
            check(anim != null && anim.Image.Width == first.Width && anim.Image.Height == first.Height && first.Width == 40,
                $"EXIF の回転: 静止画と同じ向き（静止画 {first.Width}x{first.Height}・アニメ {anim?.Image.Width}x{anim?.Image.Height}）");
            check(f != null && Near(f[20, 10], Blue) && Near(f[20, 70], Red), "EXIF の回転: 2 コマ目も回っている");
        }
        using (var frame = AnimationLoader.LoadFrame(rotated, 1))
            check(frame.Width == 40 && frame.Height == 80 && Near(frame[20, 10], Blue) && Near(frame[20, 70], Red),
                $"EXIF の回転: 原寸で読むコマ（100% 表示・フレーム保存）も回っている（{frame.Width}x{frame.Height}）");

        // ---- フレーム保存 ----
        check(Path.GetFileName(AnimationLoader.FramePath(gif, 11, 40)) == "anim_frame012.png", "保存先の名前: 元の名前_frame012.png（1 から数える）");
        check(Path.GetFileName(AnimationLoader.FramePath(gif, 0, 1500)) == "anim_frame0001.png", "保存先の名前: 桁はコマの数に合わせる");

        string saved = AnimationLoader.SaveFrame(gif, 1, 3);
        check(Path.GetFileName(saved) == "anim_frame002.png", $"フレーム保存: {Path.GetFileName(saved)}");
        using (var img = Image.Load<Rgba32>(saved))
            check(img.Width == 80 && img.Height == 40 && Near(img[10, 20], Blue) && Near(img[70, 20], Red), "フレーム保存: 原寸で、そのコマの絵");
        string again = AnimationLoader.SaveFrame(gif, 1, 3);
        check(Path.GetFileName(again) == "anim_frame002 (2).png" && File.Exists(saved), "フレーム保存: 同じ名前があれば上書きせず (2) を付ける");
        using (var img = Image.Load<Rgba32>(AnimationLoader.SaveFrame(webp, 2, 3)))
            check(Near(img[40, 20], Green), "フレーム保存: WEBP の 3 コマ目");
        check(!Directory.EnumerateFiles(dir, "*.tmp").Any(), "フレーム保存: 一時ファイルを残さない");
    }

    /// <summary>赤 → 左半分だけ青 → 緑 の 3 コマ</summary>
    static Image<Rgba32> MakeAnimation(int w, int h)
    {
        var img = new Image<Rgba32>(w, h, Red);
        using (var second = new Image<Rgba32>(w, h, Red))
        {
            second.ProcessPixelRows(a =>
            {
                for (int y = 0; y < h; y++) a.GetRowSpan(y)[..(w / 2)].Fill(Blue);
            });
            img.Frames.AddFrame(second.Frames.RootFrame);
        }
        using (var third = new Image<Rgba32>(w, h, Green)) img.Frames.AddFrame(third.Frames.RootFrame);
        return img;
    }

    static bool Near(Rgba32 a, Rgba32 b) =>
        Math.Abs(a.R - b.R) < 40 && Math.Abs(a.G - b.G) < 40 && Math.Abs(a.B - b.B) < 40;
}
