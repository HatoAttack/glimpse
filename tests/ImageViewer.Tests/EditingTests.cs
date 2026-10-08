// リサイズ・形式変換 / 切り抜き / 連結（image-sizechange から移植した処理）の動作確認
using ImageViewer.Core.Editing;
using ImageViewer.Core.Settings;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;
using Rectangle = SixLabors.ImageSharp.Rectangle;
using Size = SixLabors.ImageSharp.Size;

static class EditingTests
{
    static readonly Rgba32 Red = new(255, 0, 0), Blue = new(0, 0, 255);

    public static void Run(Action<bool, string> check, string dir)
    {
        Directory.CreateDirectory(dir);
        string P(string name) => Path.Combine(dir, name);

        // ---- 出力名 ----
        var o = new ConvertOptions { ReplaceSearch = "DSC", ReplaceWith = "photo", Suffix = "_s", Lowercase = true, Format = OutputFormat.Webp };
        check(Converter.BuildDestName("DSC001.JPG", o) == "photo001_s.webp", "出力名: 置換 → 末尾 → 小文字化、拡張子は出力形式に");
        check(Converter.BuildDestName("a.JPG", new ConvertOptions()) == "a.JPG", "出力名: 形式そのままなら拡張子もそのまま");
        check(Converter.BuildDestName("a.HEIC", new ConvertOptions()) == "a.jpg", "出力名: 書き出せない形式（HEIC）の「そのまま」は JPG");
        check(Converter.BuildDestName("a.jpg", new ConvertOptions { Suffix = "_s", UseSuffix = false }) == "a.jpg", "出力名: 「末尾に付ける」がオフなら文字列があっても付けない");

        // ---- 計画 ----
        using (var img = new Image<Rgba32>(40, 20, Red)) { img.SaveAsPng(P("a.png")); img.SaveAsJpeg(P("a.jpg")); img.SaveAsPng(P("b.png")); }
        Directory.CreateDirectory(P("resized"));
        File.WriteAllText(P(Path.Combine("resized", "b.png")), "old");
        var plan = Converter.Plan(new[] { P("a.png"), P("a.jpg"), P("b.png") }, new ConvertOptions { Format = OutputFormat.Png });
        check(plan[0].Status == ConvertStatus.Error && plan[1].Status == ConvertStatus.Error, "計画: a.png と a.jpg が両方 a.png になるのはエラー");
        check(plan[2].Status == ConvertStatus.Skip && plan[2].Target == P(Path.Combine("resized", "b.png")), "計画: 出力先（resized）に同名があれば飛ばす");
        plan = Converter.Plan(new[] { P("b.png") }, new ConvertOptions { OutputMode = OutputFolderMode.Same, Overwrite = true });
        check(plan[0].Status == ConvertStatus.Ok && plan[0].ReplacesSource, "計画: 同じフォルダ・同じ名前・上書きなら元の画像を置き換える");
        plan = Converter.Plan(new[] { P("b.png") }, new ConvertOptions { OutputMode = OutputFolderMode.Custom, CustomFolder = P("out") });
        check(plan[0].Target == P(Path.Combine("out", "b.png")), "計画: 指定のフォルダ");

        // ---- 前回の設定のまま実行してよいか（新しいファイルを作るだけのときだけ） ----
        var quick = new ConvertOptions();
        check(Converter.QuickRunBlocker(Converter.Plan(new[] { P("a.png") }, quick), quick) == null, "そのまま実行: resized に新しく作るだけなら実行できる");
        check(Converter.QuickRunBlocker(Converter.Plan(new[] { P("b.png") }, quick), quick)?.Contains("変換する画像がありません") == true,
            "そのまま実行: 全部飛ばすなら設定画面へ");
        var replace = new ConvertOptions { OutputMode = OutputFolderMode.Same, Overwrite = true };
        check(Converter.QuickRunBlocker(Converter.Plan(new[] { P("b.png") }, replace), replace)?.Contains("置き換える") == true, "そのまま実行: 元の画像を置き換えるなら設定画面へ");
        var overwrite = new ConvertOptions { Overwrite = true };
        check(Converter.QuickRunBlocker(Converter.Plan(new[] { P("b.png") }, overwrite), overwrite)?.Contains("上書き") == true, "そのまま実行: 上書きになるなら設定画面へ");
        var clash = new ConvertOptions { Format = OutputFormat.Png };
        check(Converter.QuickRunBlocker(Converter.Plan(new[] { P("a.png"), P("a.jpg") }, clash), clash) != null, "そのまま実行: 出力名が重なるなら設定画面へ");
        // 確かめた後に保存先ができたら上書きしない（ほかの処理が同時に作った等）
        var racePlan = Converter.Plan(new[] { P("a.png") }, quick with { Suffix = "_race" });
        string raced = racePlan[0].Target;
        File.WriteAllText(raced, "other");
        var raceResult = Converter.Run(racePlan, quick with { Suffix = "_race", Overwrite = true }, createOnly: true);
        check(raceResult.Converted == 0 && raceResult.Skipped == 1 && raceResult.Errors.Count == 0 && File.ReadAllText(raced) == "other",
            "そのまま実行: 実行中に保存先ができたら上書きせずに飛ばす");
        raceResult = Converter.Run(racePlan, quick with { Suffix = "_race" });
        check(raceResult.Skipped == 1 && File.ReadAllText(raced) == "other", "上書きしない設定: 実行中に保存先ができたら飛ばす");
        check(!Directory.GetFiles(P("resized"), "*.tmp").Any(), "飛ばしたときも一時ファイルが残らない");
        var noFolder = new ConvertOptions { OutputMode = OutputFolderMode.Custom };
        check(Converter.QuickRunBlocker(Converter.Plan(new[] { P("a.png") }, noFolder), noFolder) != null, "そのまま実行: 出力先の指定が無いなら設定画面へ");

        // ---- 変換 ----
        string src = P("photo.jpg");
        using (var img = SplitImage(2000, 1000))
        {
            img.Metadata.ExifProfile = new ExifProfile();
            img.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6); // 時計回り 90° で正立 → 1000x2000
            img.Metadata.ExifProfile.SetValue(ExifTag.Artist, "someone");
            img.SaveAsJpeg(src);
        }
        var opts = new ConvertOptions { LongEdge = 600, Format = OutputFormat.Png };
        var result = Converter.Run(Converter.Plan(new[] { src }, opts), opts);
        string outPng = P(Path.Combine("resized", "photo.png"));
        using (var img = Image.Load<Rgba32>(outPng))
            check(result.Converted == 1 && img.Width == 300 && img.Height == 600 && img.Metadata.ExifProfile == null && Near(img[150, 10], Red),
                $"リサイズ: 回転を反映して長辺 600（{img.Width}x{img.Height}）・PNG へ・メタデータを消す");
        check(!Directory.GetFiles(P("resized"), "*.tmp").Any(), "一時ファイルが残らない");

        opts = new ConvertOptions { LongEdge = 5000, StripMetadata = false, Suffix = "_big", OutputMode = OutputFolderMode.Same };
        Converter.Run(Converter.Plan(new[] { src }, opts), opts);
        using (var img = Image.Load<Rgba32>(P("photo_big.jpg")))
            check(img.Width == 1000 && img.Height == 2000 && img.Metadata.ExifProfile?.TryGetValue(ExifTag.Artist, out _) == true,
                "拡大しない（既定）・メタデータを残す");
        opts = opts with { NoUpscale = false, Suffix = "_up", LongEdge = 3000, Format = OutputFormat.Webp };
        Converter.Run(Converter.Plan(new[] { src }, opts), opts);
        using (var img = Image.Load<Rgba32>(P("photo_up.webp")))
            check(img.Width == 1500 && img.Height == 3000, "拡大あり・WEBP へ");

        string transparent = P("clear.png");
        using (var img = new Image<Rgba32>(10, 10, new Rgba32(0, 0, 0, 0))) img.SaveAsPng(transparent);
        opts = new ConvertOptions { LongEdge = ConvertOptions.KeepSize, Format = OutputFormat.Jpeg, OutputMode = OutputFolderMode.Same };
        Converter.Run(Converter.Plan(new[] { transparent }, opts), opts);
        using (var img = Image.Load<Rgba32>(P("clear.jpg")))
            check(img.Width == 10 && Near(img[5, 5], new Rgba32(255, 255, 255)), "サイズそのまま JPG へ（透過は白）");

        File.WriteAllText(P("broken.png"), "not an image");
        opts = new ConvertOptions { OutputMode = OutputFolderMode.Same, Suffix = "_x" };
        result = Converter.Run(Converter.Plan(new[] { P("broken.png"), transparent }, opts), opts);
        check(result.Converted == 1 && result.Errors.Count == 1 && !File.Exists(P("broken_x.png")), "読めない画像はエラーにして続ける（書きかけを残さない）");

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            result = Converter.Run(Converter.Plan(new[] { transparent }, opts with { Suffix = "_c" }), opts with { Suffix = "_c" }, null, cts.Token);
            check(result.Canceled && result.Converted == 0, "中断");
        }

        bool tooBig = false;
        try
        {
            using var img = new Image<Rgba32>(ImageSaver.WebpMaxEdge + 1, 1);
            ImageSaver.Save(img, P("wide.webp"));
        }
        catch (NotSupportedException) { tooBig = true; }
        check(tooBig && !File.Exists(P("wide.webp")), "WEBP の上限を超えるとエラー");

        // ---- 保存の画質 ----
        long SavedSize(string name)
        {
            using var img = new Image<Rgba32>(64, 64);
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                    img[x, y] = new Rgba32((byte)(x * 4), (byte)(y * 4), (byte)((x * y) % 256));
            ImageSaver.Save(img, P(name));
            return new FileInfo(P(name)).Length;
        }
        ImageSaver.JpegQuality = 20;
        long low = SavedSize("q20.jpg");
        ImageSaver.JpegQuality = 98;
        long high = SavedSize("q98.jpg");
        check(low < high, "画質: JPEG の画質を上げるとファイルが大きくなる");
        ImageSaver.JpegQuality = 0;
        ImageSaver.WebpQuality = 500;
        check(ImageSaver.JpegQuality == ImageSaver.MinQuality && ImageSaver.WebpQuality == ImageSaver.MaxQuality, "画質: 範囲外は 1〜100 に丸める");
        ImageSaver.JpegQuality = ImageSaver.WebpQuality = ImageSaver.DefaultQuality;

        // ---- 切り抜き ----
        var (cx, cy, cw, ch) = Cropper.CenterRect(400, 300, 1.0);
        check(cx == 50 && cy == 0 && cw == 300 && ch == 300, "切り抜き: 中央の 1:1");
        check(Cropper.Flip(4.0 / 3, true) == 0.75, "切り抜き: 縦横の入れ替え");
        check(Cropper.ClampBox(-3, 2.4, 500, 99.6, 400, 300) == Rectangle.FromLTRB(0, 2, 400, 100), "切り抜き: 画像の範囲に丸める");

        // 枠の引き継ぎ（iPhone のスクショを 1:2 で、中央より少し下を切り抜く例）
        var shot = (X0: 0.0, Y0: 150.0, X1: 1179.0, Y1: 2508.0); // 1179 × 2556 の画像の、中央より少し下の 1:2
        check(Cropper.CarryRect(shot, 1179, 2556, 1179, 2556) == (0, 150, 1179, 2508), "切り抜き: 同じ大きさの画像には同じ範囲");
        var half = Cropper.CarryRect(shot, 1179, 2556, 590, 1278); // 同じ縦横比で半分の大きさ
        check(Math.Abs(half.Y0 - 75) < 1 && Math.Abs((half.X1 - half.X0) * 2 - (half.Y1 - half.Y0)) < 0.01,
            $"切り抜き: 大きさが違えば割合で合わせ、アスペクト比は保つ（{half}）");
        var wide = Cropper.CarryRect(shot, 1179, 2556, 1000, 1000); // 縦横比が違う画像
        check(wide.X0 >= 0 && wide.Y0 >= 0 && wide.X1 <= 1000 && wide.Y1 <= 1000
              && Math.Abs((wide.X1 - wide.X0) * 2 - (wide.Y1 - wide.Y0)) < 0.01, $"切り抜き: 縦横比が違っても画像からはみ出さない（{wide}）");
        var edge = Cropper.CarryRect((300, 200, 400, 300), 400, 300, 200, 300); // 右端の枠を幅の狭い画像へ
        check(edge.X1 <= 200 && edge.X0 >= 0, $"切り抜き: 端に寄せた枠も画像の中に収める（{edge}）");
        Cropper.CropCenter(src, 1.0, Cropper.OutputPathFor(src, dir));
        string second = Cropper.OutputPathFor(src, dir);
        check(second == P("photo_crop (2).jpg"), "切り抜き: 同名があれば (2) を付ける");
        Cropper.CropCarried(src, (0, 0, 100, 50), 200, 100, P("carried.png"));
        using (var img = Image.Load<Rgba32>(P("carried.png")))
        {
            using var whole = ImageViewer.Core.Imaging.ImageLoader.Load(src); // 回転を反映した大きさで比べる
            // 200 × 100 の画像での 100 × 50 の枠を、縦横同じ倍率（小さい方の比）で合わせた大きさになる
            double carry = Math.Min(whole.Width / 200.0, whole.Height / 100.0);
            check(Math.Abs(img.Width - 100 * carry) <= 1 && Math.Abs(img.Height - 50 * carry) <= 1,
                $"切り抜き: 一括で枠を引き継いで切り抜く（{whole.Width}x{whole.Height} → {img.Width}x{img.Height}）");
        }
        using (var img = Image.Load<Rgba32>(P("photo_crop.jpg")))
            check(img.Width == 1000 && img.Height == 1000, $"切り抜き: 回転を反映した画像の中央 1:1（{img.Width}x{img.Height}）");

        // 上書き（保存先が元のファイル）
        using (var img = new Image<Rgba32>(200, 100, Red)) img.SaveAsPng(P("over.png"));
        Cropper.CropCenter(P("over.png"), 1.0, P("over.png"));
        using (var img = Image.Load<Rgba32>(P("over.png")))
            check(img.Width == 100 && img.Height == 100, $"切り抜き: 元のファイルに上書きできる（{img.Width}x{img.Height}）");
        using (var whole = ImageViewer.Core.Imaging.ImageLoader.Load(P("over.png")))
            Cropper.SaveCrop(whole, new Rectangle(0, 0, 40, 30), P("over.png"), P("over.png"));
        using (var img = Image.Load<Rgba32>(P("over.png")))
            check(img.Width == 40 && img.Height == 30, $"切り抜き: 表示中の画像の範囲で上書きできる（{img.Width}x{img.Height}）");
        using (var anim = new Image<Rgba32>(20, 20, Red))
        {
            anim.Frames.AddFrame(new Image<Rgba32>(20, 20, Blue).Frames.RootFrame);
            anim.SaveAsGif(P("over.gif"));
        }
        long gifSize = new FileInfo(P("over.gif")).Length;
        bool refused = false;
        try { Cropper.CropCenter(P("over.gif"), 1.0, P("over.gif")); }
        catch (NotSupportedException) { refused = true; }
        check(refused && new FileInfo(P("over.gif")).Length == gifSize, "切り抜き: アニメーションは上書きしない（元のまま）");

        // ---- モザイク・ぼかし ----
        check(Masker.EffectSize(3000, 2000, 3) == 30 && Masker.EffectSize(10, 10, 1) == 2, "モザイク: マスの大きさは長い辺に対する割合（最小 2px）");
        check(Masker.EffectSize(300, 300, 99) == Masker.EffectSize(300, 300, Masker.MaxLevel), "モザイク: 強さは 1〜10 に丸める");
        var carried = Masker.CarryRects(new[] { (10.0, 20.0, 30.0, 40.0) }, 100, 100, 200, 50);
        check(carried.Count == 1 && carried[0] == (20, 10, 60, 20), $"モザイク: 大きさが違う画像には縦横それぞれ割合で範囲を合わせる（{carried[0]}）");
        // 左半分が赤・右半分が青の画像。左の 20 × 20 だけにかけると、そこは市松から 1 色になり、外は変わらない
        using (var img = new Image<Rgba32>(40, 40, Blue))
        {
            for (int y = 0; y < 20; y++)
                for (int x = 0; x < 20; x++)
                    img[x, y] = (x + y) % 2 == 0 ? Red : Blue;
            var before = img[30, 30];
            Masker.Apply(img, new[] { new Rectangle(0, 0, 20, 20) }, MaskEffect.Mosaic, 10);
            check(img[0, 0] == img[9, 9] && img[0, 0] == img[1, 0], "モザイク: 範囲の中はマスごとに 1 色");
            check(img[30, 30] == before && img[20, 0] == Blue, "モザイク: 範囲の外は変えない");
        }
        using (var img = new Image<Rgba32>(40, 40, Blue))
        {
            for (int y = 0; y < 40; y++)
                for (int x = 0; x < 40; x++)
                    img[x, y] = (x + y) % 2 == 0 ? Red : Blue;
            Masker.Apply(img, new[] { new Rectangle(0, 0, 20, 40), new Rectangle(-5, -5, 0, 0) }, MaskEffect.Blur, 8);
            var mid = img[10, 20];
            check(mid.R is > 60 and < 200 && mid.B is > 60 and < 200, $"ぼかし: 範囲の中は混ざる（{mid}）");
            check(img[30, 20] == ((30 + 20) % 2 == 0 ? Red : Blue), "ぼかし: 範囲の外は変えない（画像の外の範囲は無視する）");
        }
        using (var img = new Image<Rgba32>(60, 30, Red)) img.SaveAsPng(P("mask.png"));
        string maskOut = Masker.OutputPathFor(P("mask.png"), dir, MaskEffect.Mosaic);
        check(maskOut == P("mask_mosaic.png") && Masker.OutputPathFor(P("mask.png"), dir, MaskEffect.Blur) == P("mask_blur.png"),
            "モザイク: 保存先の名前は _mosaic / _blur");
        using (var whole = ImageViewer.Core.Imaging.ImageLoader.Load(P("mask.png")))
        {
            Masker.SaveMasked(whole, new[] { new Rectangle(0, 0, 10, 10) }, MaskEffect.Mosaic, 5, P("mask.png"), maskOut);
            check(whole.Width == 60 && whole[0, 0] == Red, "モザイク: 保存しても表示中の画像は変えない");
        }
        check(File.Exists(maskOut), "モザイク: 別の名前で保存できる");
        Masker.MaskCarried(P("mask.png"), new[] { (0.0, 0.0, 10.0, 10.0) }, 60, 30, MaskEffect.Blur, 5, P("mask.png"));
        using (var img = Image.Load<Rgba32>(P("mask.png")))
            check(img.Width == 60 && img.Height == 30, "モザイク: 元のファイルに上書きできる（大きさはそのまま）");
        refused = false;
        try { Masker.MaskCarried(P("over.gif"), new[] { (0.0, 0.0, 10.0, 10.0) }, 20, 20, MaskEffect.Mosaic, 5, P("over.gif")); }
        catch (NotSupportedException) { refused = true; }
        check(refused && new FileInfo(P("over.gif")).Length == gifSize, "モザイク: アニメーションは上書きしない（元のまま）");

        // ---- 連結 ----
        using (var a = new Image<Rgba32>(100, 50, Red))
        using (var b = new Image<Rgba32>(40, 100, Blue))
        {
            var imgs = new[] { a, b };
            using (var r = Combiner.Combine(imgs, new CombineOptions { Spacing = 10, Padding = 5 }))
                check(r.Width == 100 + 40 + 10 + 10 && r.Height == 100 + 10 && Near(r[0, 0], new Rgba32(255, 255, 255)) && Near(r[5, 5], Red),
                    $"連結: 横・そのまま・間隔と余白（{r.Width}x{r.Height}）");
            using (var r = Combiner.Combine(imgs, new CombineOptions { Normalize = CombineNormalize.Min }))
                check(r.Width == 100 + 20 && r.Height == 50, $"連結: 横・高さを小さい方に（{r.Width}x{r.Height}）");
            using (var r = Combiner.Combine(imgs, new CombineOptions { Layout = CombineLayout.Vertical, Normalize = CombineNormalize.Fixed, TargetPx = 200 }))
                check(r.Width == 200 && r.Height == 100 + 500, $"連結: 縦・幅を指定 px に（{r.Width}x{r.Height}）");
            using (var r = Combiner.Combine(new[] { a, b, a }, new CombineOptions { Layout = CombineLayout.Grid, Columns = 2, Transparent = true }))
                check(r.Width == 200 && r.Height == 200 && r[199, 199].A == 0, $"連結: グリッド 2 列・透明（{r.Width}x{r.Height}）");
            using (var r = Combiner.Combine(imgs, new CombineOptions { Layout = CombineLayout.Grid, Columns = 5 }))
                check(r.Width == 200, "連結: 列の数が枚数より多ければ枚数に合わせる");
        }
        bool badColor = false;
        try { Combiner.ParseColor("#12345g", false); } catch (ArgumentException) { badColor = true; }
        check(Combiner.ParseColor("#f00", false) == Red && badColor, "連結: 背景色の解釈");

        // プレビュー（縮小して組む）と原寸の結果の大きさがほぼ同じ
        string c1 = P("c1.png"), c2 = P("c2.png");
        using (var img = new Image<Rgba32>(3000, 2000, Red)) img.SaveAsPng(c1);
        using (var img = new Image<Rgba32>(1000, 1500, Blue)) img.SaveAsPng(c2);
        var co = new CombineOptions { Normalize = CombineNormalize.Max, Spacing = 30, Padding = 60 };
        var (pre, scale) = Combiner.LoadForPreview(new[] { c1, c2 }, 1200, long.MaxValue);
        try
        {
            using var preview = Combiner.Combine(pre, co.Scaled(scale));
            var size = Combiner.CombineFiles(new[] { c1, c2 }, co, P("combined.png"));
            check(scale == 0.4 && size == (3000 + 1333 + 30 + 120, 2000 + 120)
                  && Math.Abs(preview.Width / scale - size.Width) < 10 && Math.Abs(preview.Height / scale - size.Height) < 10,
                $"連結: プレビュー（{preview.Width}x{preview.Height}、×{scale}）と原寸（{size.Width}x{size.Height}）が合う");
            using var bounded = Combiner.CombineBounded(pre, co.Scaled(scale), 600);
            check(bounded.Width == 600 && Math.Abs(bounded.Height - preview.Height * 600.0 / preview.Width) <= 1,
                $"連結: プレビューは長辺の上限の大きさで描く（{bounded.Width}x{bounded.Height}）");
        }
        finally
        {
            foreach (var im in pre) im.Dispose();
        }

        // 保存の前に、画像を読まずに仕上がりの大きさを計算できる（プレビューの計算を待たない）
        var measured = Combiner.MeasureFiles(new[] { c1, c2 }, co);
        check(measured == new Size(3000 + 1333 + 30 + 120, 2000 + 120), $"連結: ヘッダーだけから仕上がりの大きさを計算（{measured}）");
        check(Combiner.MeasureFiles(new[] { c1, P("broken.png") }, co) == null, "連結: 大きさの分からない画像があれば計算しない");

        // 形式の上限を超えるなら、大きなキャンバスを作る前にやめる（WEBP は 16383px まで）
        bool rejected = false;
        try { Combiner.CombineFiles(new[] { c1, c1, c1, c1, c1, c1 }, new CombineOptions(), P("wide_combined.webp")); }
        catch (NotSupportedException) { rejected = true; }
        check(rejected && !File.Exists(P("wide_combined.webp")), "連結: 仕上がり 18000px の WEBP は保存前にエラー");

        // 画素数の合計の上限: 3000x2000 + 1000x1500 = 7.5M 画素を 0.75M 画素までに → 比率 √0.1
        (pre, scale) = Combiner.LoadForPreview(new[] { c1, c2 }, 1200, 750_000);
        try
        {
            check(Math.Abs(scale - Math.Sqrt(0.1)) < 1e-9 && pre.Sum(im => (long)im.Width * im.Height) <= 760_000,
                $"連結: プレビュー用に読む画素数の合計に上限（×{scale:0.###}）");
        }
        finally
        {
            foreach (var im in pre) im.Dispose();
        }

        // 何百枚並べても、プレビューのキャンバスは上限の大きさ（原寸なら 60000 x 200 になる並び）
        var many = Enumerable.Range(0, 300).Select(_ => new Image<Rgba32>(200, 200, Red)).ToList();
        try
        {
            var layout = Combiner.Layout(many.Select(im => new Size(im.Width, im.Height)).ToList(), new CombineOptions());
            using var bounded = Combiner.CombineBounded(many, new CombineOptions(), 2400);
            check(layout.Canvas == new Size(60000, 200) && bounded.Width == 2400 && bounded.Height == 8,
                $"連結: 300 枚の横並び（原寸 {layout.Canvas.Width}x{layout.Canvas.Height}）もプレビューは {bounded.Width}x{bounded.Height} で描く");
        }
        finally
        {
            foreach (var im in many) im.Dispose();
        }

        // ---- 出力先の外に書かない・メタデータを残せない形式 ----
        plan = Converter.Plan(new[] { P("b.png") }, new ConvertOptions { Suffix = @"\..\..\evil" });
        check(plan[0].Status == ConvertStatus.Error, "計画: 末尾に \\ や .. を含む名前はエラー（出力先の外に書かない）");
        plan = Converter.Plan(new[] { P("b.png") }, new ConvertOptions { ReplaceSearch = "b", ReplaceWith = @"sub\b" });
        check(plan[0].Status == ConvertStatus.Error, "計画: 置換で \\ を含む名前はエラー（勝手にフォルダを作らない）");
        plan = Converter.Plan(new[] { P("b.png") }, new ConvertOptions { SubfolderName = @"..\x" });
        check(plan[0].Status == ConvertStatus.Error, "計画: 中のフォルダの名前に \\ や .. はエラー");
        plan = Converter.Plan(new[] { P("x.heic"), P("clear.png") }, new ConvertOptions { StripMetadata = false });
        check(plan[0].Status == ConvertStatus.Ok && plan[0].Note?.Contains("メタデータ") == true && plan[1].Note == null,
            "計画: メタデータを残す設定でも HEIC などは残せないと示す");

        // ---- 前回の設定の保存 ----
        var store = new SettingsStore(P("settings.json"));
        store.Save(new AppSettings { Resize = new ConvertOptions { LongEdge = 777, Format = OutputFormat.Webp, OutputMode = OutputFolderMode.Custom, CustomFolder = @"D:\x" },
            Combine = new CombineOptions { Layout = CombineLayout.Grid, Columns = 3 } });
        var loaded = store.Load();
        check(loaded.Resize == new ConvertOptions { LongEdge = 777, Format = OutputFormat.Webp, OutputMode = OutputFolderMode.Custom, CustomFolder = @"D:\x" }
              && loaded.Combine?.Columns == 3, "リサイズ・連結の設定を保存して読み戻せる");
    }

    static Image<Rgba32> SplitImage(int w, int h)
    {
        var img = new Image<Rgba32>(w, h, Blue);
        img.ProcessPixelRows(a =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = a.GetRowSpan(y);
                for (int x = 0; x < w / 2; x++) row[x] = Red;
            }
        });
        return img;
    }

    static bool Near(Rgba32 a, Rgba32 b) => Math.Abs(a.R - b.R) < 40 && Math.Abs(a.G - b.G) < 40 && Math.Abs(a.B - b.B) < 40;
}
