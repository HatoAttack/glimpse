// GIF / WEBP アニメの読み込みとフレーム保存の動作確認
// WEBP は WIC で書き出せないので、コミットしてあるもの（Fixtures/anim.webp・rotated.webp）を使う
using ImageViewer.Core.Imaging;

static class AnimationTests
{
    static readonly Px Red = new(255, 0, 0), Blue = new(0, 0, 255), Green = new(0, 200, 0);

    public static void Run(Action<bool, string> check, string dir)
    {
        // 3 コマ: 赤 → 左半分だけ青 → 緑。表示時間は 0（→ 0.1 秒扱い）/ 0.2 秒 / 0.5 秒
        // 2 コマ目は右半分を透明にした差分のコマ（前のコマが透けて見える）
        string gif = Path.Combine(dir, "anim.gif");
        TestImage.SaveFrames(gif, MakeAnimation(80, 40), new[] { 0, 20, 50 });
        check(AnimationLoader.FrameCount(gif) == 3, $"GIF のコマ数（{AnimationLoader.FrameCount(gif)}）");

        using (var anim = AnimationLoader.Load(gif, maxEdge: 1000))
        {
            check(anim != null && anim.Count == 3 && anim.Image.Width == 80, "GIF: 全部のコマを原寸のまま読む（画面より小さい）");
            check(anim != null && anim.DelaysMs.SequenceEqual(new[] { 100, 200, 500 }), $"GIF: 表示時間（{string.Join(",", anim?.DelaysMs ?? Array.Empty<int>())}）");
            // 2 コマ目は左半分だけ変えたもの。右半分は前のコマ（赤）が残っていること（重ね合わせ済み）
            var f = anim == null ? null : TestImage.FromApi(anim.Image, 1);
            check(f != null && f[10, 20].Near(Blue) && f[70, 20].Near(Red), "GIF: 差分だけのコマも重ね合わせた絵になる");
            check(anim != null && TestImage.FromApi(anim.Image, 2)[40, 20].Near(Green), "GIF: 3 コマ目");
        }

        using (var anim = AnimationLoader.Load(gif, maxEdge: 20))
            check(anim != null && anim.Image.Width == 20 && anim.Image.Height == 10, $"GIF: 画面の大きさに縮める（{anim?.Image.Width}x{anim?.Image.Height}）");

        string still = Path.Combine(dir, "still.gif");
        new TestImage(20, 20, Red).Save(still);
        check(AnimationLoader.Load(still, 1000) == null, "コマが 1 つの GIF はアニメとして扱わない");
        check(!AnimationLoader.MayBeAnimated("a.png") && AnimationLoader.MayBeAnimated("A.WEBP"), "アニメかもしれない形式（GIF / WEBP）");

        // ---- WEBP ----
        // GIF と同じ 3 コマ（差分ではなく全体）。表示時間は 0.005 秒（→ 0.1 秒扱い）/ 0.2 秒 / 0.5 秒
        string webp = TestImage.Fixture("anim.webp", dir);
        using (var anim = AnimationLoader.Load(webp, maxEdge: 1000))
        {
            check(anim != null && anim.Count == 3, $"WEBP: コマ数（{anim?.Count}）");
            check(anim != null && anim.DelaysMs.SequenceEqual(new[] { 100, 200, 500 }), $"WEBP: 表示時間（{string.Join(",", anim?.DelaysMs ?? Array.Empty<int>())}）");
            var f = anim == null ? null : TestImage.FromApi(anim.Image, 1);
            check(f != null && f[10, 20].Near(Blue) && f[70, 20].Near(Red), "WEBP: 2 コマ目");
        }

        // EXIF の Orientation=6（時計回り 90° で正立）: 元の左半分が上に来る。先頭のコマの静止画と同じ向きになること
        string rotated = TestImage.Fixture("rotated.webp", dir);
        using (var first = ImageLoader.Load(rotated))
        using (var anim = AnimationLoader.Load(rotated, maxEdge: 1000))
        {
            var f = anim == null ? null : TestImage.FromApi(anim.Image, 1);
            check(anim != null && anim.Image.Width == first.Width && anim.Image.Height == first.Height && first.Width == 40,
                $"EXIF の回転: 静止画と同じ向き（静止画 {first.Width}x{first.Height}・アニメ {anim?.Image.Width}x{anim?.Image.Height}）");
            check(f != null && f[20, 10].Near(Blue) && f[20, 70].Near(Red), "EXIF の回転: 2 コマ目も回っている");
        }
        using (var loaded = AnimationLoader.LoadFrame(rotated, 1))
        {
            var frame = TestImage.FromApi(loaded);
            check(frame.Width == 40 && frame.Height == 80 && frame[20, 10].Near(Blue) && frame[20, 70].Near(Red),
                $"EXIF の回転: 原寸で読むコマ（100% 表示・フレーム保存）も回っている（{frame.Width}x{frame.Height}）");
        }

        // ---- フレーム保存 ----
        check(Path.GetFileName(AnimationLoader.FramePath(gif, 11, 40)) == "anim_frame012.png", "保存先の名前: 元の名前_frame012.png（1 から数える）");
        check(Path.GetFileName(AnimationLoader.FramePath(gif, 0, 1500)) == "anim_frame0001.png", "保存先の名前: 桁はコマの数に合わせる");

        string saved = AnimationLoader.SaveFrame(gif, 1, 3);
        check(Path.GetFileName(saved) == "anim_frame002.png", $"フレーム保存: {Path.GetFileName(saved)}");
        var savedImg = TestImage.Load(saved);
        check(savedImg.Width == 80 && savedImg.Height == 40 && savedImg[10, 20].Near(Blue) && savedImg[70, 20].Near(Red), "フレーム保存: 原寸で、そのコマの絵");
        string again = AnimationLoader.SaveFrame(gif, 1, 3);
        check(Path.GetFileName(again) == "anim_frame002 (2).png" && File.Exists(saved), "フレーム保存: 同じ名前があれば上書きせず (2) を付ける");
        check(TestImage.Load(AnimationLoader.SaveFrame(webp, 2, 3))[40, 20].Near(Green), "フレーム保存: WEBP の 3 コマ目");
        check(!Directory.EnumerateFiles(dir, "*.tmp").Any(), "フレーム保存: 一時ファイルを残さない");
    }

    /// <summary>赤 → 左半分だけ青（右半分は透明。前のコマの赤が透けて見える）→ 緑 の 3 コマ</summary>
    static TestImage[] MakeAnimation(int w, int h) =>
        new[] { new TestImage(w, h, Red), new TestImage(w, h, Px.Transparent).Fill(Blue, 0, 0, w / 2, h), new TestImage(w, h, Green) };
}
