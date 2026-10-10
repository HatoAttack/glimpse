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

        // ---- 幅 × 高さをぴったり指定 ----
        var exact = new ConvertOptions { ExactSize = true, ExactWidth = 560, ExactHeight = 315 };
        check(Converter.OutputGeometry(1920, 1078, new ConvertOptions { LongEdge = 560 }) is (_, 560, 314),
            "長辺の指定: 16:9 より少し低い画像は短い辺が 1px 足りなくなる（比率を保つので）");
        check(Converter.OutputGeometry(1120, 629, new ConvertOptions { LongEdge = 560 }) is (_, 560, 315), "長辺の指定: ちょうど .5 は切り上げる（314.5 → 315）");
        check(Converter.OutputGeometry(1920, 1078, exact) is ({ X: 2, Y: 0, Width: 1916, Height: 1078 }, 560, 315),
            "幅 × 高さの指定: 比率が合わない分は中央から切って、ぴったりの大きさにする（横を切る）");
        check(Converter.OutputGeometry(1920, 1080, exact) is ({ X: 0, Y: 0, Width: 1920, Height: 1080 }, 560, 315), "幅 × 高さの指定: 比率が同じなら切らない");
        check(Converter.OutputGeometry(1000, 2000, exact) is ({ X: 0, Y: 718, Width: 1000, Height: 563 }, 560, 315), "幅 × 高さの指定: 縦長の画像は上下を切る");
        check(Converter.OutputGeometry(400, 300, exact) is ({ Width: 400, Height: 300 }, 400, 300)
              && Converter.OutputGeometry(400, 300, exact with { NoUpscale = false }) is ({ X: 0, Y: 37, Width: 400, Height: 225 }, 560, 315),
            "幅 × 高さの指定: 指定より小さい画像は「拡大しない」ならそのまま、オフなら切って拡大");
        opts = new ConvertOptions { ExactSize = true, ExactWidth = 200, ExactHeight = 100, Suffix = "_exact", OutputMode = OutputFolderMode.Same };
        Converter.Run(Converter.Plan(new[] { src }, opts), opts);
        using (var img = Image.Load<Rgba32>(P("photo_exact.jpg")))
            check(img.Width == 200 && img.Height == 100, $"幅 × 高さの指定: 保存した画像がぴったりの大きさ（{img.Width}x{img.Height}）");
        check(Converter.Describe(exact).StartsWith("560 × 315px"), "幅 × 高さの指定: 設定の説明に大きさを出す");

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
        var carried = Masker.CarryRegions(new[] { MaskRegion.Rect(10, 20, 30, 40) }, 100, 100, 200, 50);
        check(carried.Count == 1 && (carried[0].X0, carried[0].Y0, carried[0].X1, carried[0].Y1) == (20, 10, 60, 20),
            $"モザイク: 大きさが違う画像には縦横それぞれ割合で範囲を合わせる（{carried[0]}）");
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
            // マスより小さい範囲（ドラッグし始めの数 px など）でも例外にならず、マスを範囲に合わせてかける
            for (int y = 30; y < 34; y++)
                for (int x = 30; x < 34; x++)
                    img[x, y] = (x + y) % 2 == 0 ? Red : Blue;
            bool thrown = false;
            try
            {
                Masker.Apply(img, new[] { new Rectangle(30, 30, 4, 4), new Rectangle(0, 30, 1, 5) }, MaskEffect.Mosaic, 10);
                Masker.Apply(img, new[] { new Rectangle(30, 30, 4, 4) }, MaskEffect.Blur, 10);
                // ぼかしも、差し渡しが範囲より大きいと例外になる（ボックスぼかしで実際に起きた）。1〜3px の範囲や円でも止まらない
                foreach (var effect in new[] { MaskEffect.Blur, MaskEffect.BoxBlur })
                    foreach (int w in new[] { 1, 2, 3, 5 })
                        Masker.Apply(img, new[] { MaskRegion.Rect(0, 0, w, 12), new MaskRegion(MaskShape.Ellipse, 10, 10, 10 + w, 13) }, effect, 40);
            }
            catch (ArgumentException) { thrown = true; }
            check(!thrown && img[30, 30] == img[33, 33], "モザイク: マスより小さい範囲でも失敗せず、範囲を 1 マスにする");
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
        // 円・楕円と自由な形: 外枠の中でも、形の外は変えない
        static Image<Rgba32> Checker()
        {
            var c = new Image<Rgba32>(40, 40);
            for (int y = 0; y < 40; y++)
                for (int x = 0; x < 40; x++)
                    c[x, y] = (x + y) % 2 == 0 ? Red : Blue;
            return c;
        }
        var ellipse = new MaskRegion(MaskShape.Ellipse, 0, 0, 40, 40);
        check(ellipse.Contains(20, 20) && !ellipse.Contains(1, 1), "円: 中心は中、外枠の角は外");
        using (var img = Checker())
        {
            Masker.Apply(img, new[] { ellipse }, MaskEffect.Blur, 8);
            var mid = img[20, 20];
            check(mid.R is > 60 and < 200 && img[0, 0] == Red && img[39, 0] == Blue, $"円: 円の中だけ混ざり、外枠の角は変えない（{mid}）");
        }
        var triangle = MaskRegion.FromPath(new[] { (0.0, 0.0), (40.0, 0.0), (0.0, 40.0) });
        check(triangle is { Shape: MaskShape.Freehand, Width: 40, Height: 40 } && triangle.Contains(5, 5) && !triangle.Contains(35, 35),
            "自由な形: なぞった点から外枠と形を作る（中・外の判定）");
        check(MaskRegion.FromPath(new[] { (0.0, 0.0), (10.0, 10.0) }) == null, "自由な形: 点が 3 つ未満なら作らない");
        using (var img = Checker())
        {
            Masker.Apply(img, new[] { triangle! }, MaskEffect.Blur, 8);
            var inside = img[5, 5];
            check(inside.R is > 60 and < 200 && img[35, 35] == Red && img[30, 31] == Blue, $"自由な形: 形の中だけ混ざり、外は変えない（{inside}）");
        }
        // ボックスぼかし・塗りつぶし: 形の中だけ変え、外は変えない（塗りつぶしは黒一色）
        using (var img = Checker())
        {
            Masker.Apply(img, new[] { ellipse }, MaskEffect.BoxBlur, 8);
            var mid = img[20, 20];
            check(mid.R is > 60 and < 200 && img[0, 0] == Red, $"ボックスぼかし: 円の中だけ混ざる（{mid}）");
        }
        using (var img = Checker())
        {
            var black = new Rgba32(0, 0, 0);
            Masker.Apply(img, new[] { triangle!, MaskRegion.Rect(30, 30, 40, 40) }, MaskEffect.Fill, 0);
            check(img[5, 5] == black && img[35, 35] == black && img[29, 31] == Red && img[25, 25] == Red,
                "塗りつぶし: 自由な形・四角の中を黒で塗り、外は変えない（強さは使わない）");
        }
        check(Masker.OutputPathFor(P("mask.png"), dir, MaskEffect.BoxBlur) == P("mask_boxblur.png")
              && Masker.OutputPathFor(P("mask.png"), dir, MaskEffect.Fill) == P("mask_fill.png"), "保存先の名前は _boxblur / _fill");
        var moved = Masker.CarryRegions(new[] { triangle! }, 40, 40, 80, 80)[0];
        check(moved.Contains(10, 10) && !moved.Contains(70, 70), "自由な形: 大きさの違う画像へ引き継ぐと形ごと伸びる");

        using (var img = new Image<Rgba32>(60, 30, Red)) img.SaveAsPng(P("mask.png"));
        string maskOut = Masker.OutputPathFor(P("mask.png"), dir, MaskEffect.Mosaic);
        check(maskOut == P("mask_mosaic.png") && Masker.OutputPathFor(P("mask.png"), dir, MaskEffect.Blur) == P("mask_blur.png"),
            "モザイク: 保存先の名前は _mosaic / _blur");
        using (var whole = ImageViewer.Core.Imaging.ImageLoader.Load(P("mask.png")))
        {
            Masker.SaveMasked(whole, new[] { MaskRegion.Rect(0, 0, 10, 10) }, MaskEffect.Mosaic, 5, P("mask.png"), maskOut);
            check(whole.Width == 60 && whole[0, 0] == Red, "モザイク: 保存しても表示中の画像は変えない");
        }
        check(File.Exists(maskOut), "モザイク: 別の名前で保存できる");
        Masker.MaskCarried(P("mask.png"), new[] { MaskRegion.Rect(0, 0, 10, 10) }, 60, 30, MaskEffect.Blur, 5, P("mask.png"));
        using (var img = Image.Load<Rgba32>(P("mask.png")))
            check(img.Width == 60 && img.Height == 30, "モザイク: 元のファイルに上書きできる（大きさはそのまま）");
        refused = false;
        try { Masker.MaskCarried(P("over.gif"), new[] { MaskRegion.Rect(0, 0, 10, 10) }, 20, 20, MaskEffect.Mosaic, 5, P("over.gif")); }
        catch (NotSupportedException) { refused = true; }
        check(refused && new FileInfo(P("over.gif")).Length == gifSize, "モザイク: アニメーションは上書きしない（元のまま）");

        // ---- 枠・矢印 ----
        var frame = new Annotation(AnnotationKind.Frame, 20, 20, 80, 60, Red, 4, 0, 4, false);
        using (var img = new Image<Rgba32>(100, 80, Blue))
        {
            Annotator.Draw(img, new[] { frame });
            check(img[20, 40] == Red && img[50, 20] == Red && img[79, 59] == Red && img[18, 18] == Red, "枠: 線の中心が四角の辺を通り、丸みが 0 なら外側の角まで塗る");
            check(img[50, 40] == Blue && img[10, 10] == Blue && img[50, 63] == Blue, "枠: 中と外は変えない");
        }
        using (var img = new Image<Rgba32>(100, 80, Blue))
        {
            Annotator.Draw(img, new[] { frame with { CornerRadius = 20 } });
            check(img[18, 18] == Blue && img[20, 40] == Red && img[50, 20] == Red, "枠: 角の丸みを付けると角が丸くなる（辺はそのまま）");
        }
        check((frame with { CornerRadius = 999 }).Distance(19.5, 19.5) == (frame with { CornerRadius = 20 }).Distance(19.5, 19.5),
            "枠: 角の丸みは短い辺の半分まで");
        using (var img = new Image<Rgba32>(100, 80, Blue))
        {
            Annotator.Draw(img, new[] { frame with { Thickness = 12 } });
            check(img[15, 40] == Red && img[25, 40] == Red && img[27, 40] == Blue, "枠: 太さを変えると線が両側に太る");
        }
        using (var img = new Image<Rgba32>(100, 80, Blue))
        {
            Annotator.Draw(img, new[] { frame with { Shadow = true } });
            check(img[50, 63].B < 255 && img[50, 63].R == 0 && img[50, 16] == Blue && img[20, 40] == Red, "枠: 影は右下にだけ落ちる（線の色は変えない）");
        }
        var arrow = new Annotation(AnnotationKind.Arrow, 10, 40, 90, 40, Red, 4, 0, 4, false);
        using (var img = new Image<Rgba32>(100, 80, Blue))
        {
            Annotator.Draw(img, new[] { arrow });
            check(img[50, 40] == Red && img[50, 45] == Blue && img[76, 45] == Red && img[86, 40] == Red, "矢印: 軸と、(X1, Y1) 側に先端の三角を描く");
            check(img[95, 40] == Blue && img[5, 40] == Blue && img[60, 50] == Blue, "矢印: 端より先と横は変えない");
        }
        using (var img = new Image<Rgba32>(100, 80, Blue))
        {
            Annotator.Draw(img, new[] { arrow with { HeadSize = 8 }, arrow with { X0 = 50, Y0 = 70, X1 = 50, Y1 = 70 } });
            check(img[60, 50] == Red && img[50, 70] == Blue, "矢印: 先端の大きさを変えると三角が大きくなる（長さ 0 の矢印は描かない）");
        }
        // ---- 文字・番号・吹き出し（書体は PC によって違うので、文字の形そのものでなく位置と色で確かめる） ----
        var White = new Rgba32(255, 255, 255);
        var label = new Annotation(AnnotationKind.Text, 20, 20, 0, 0, Red, 2, 4, 4, false) { Text = "AB", FontSize = 20 };
        using (var img = new Image<Rgba32>(200, 120, Blue))
        {
            Annotator.Draw(img, new[] { label });
            var (bx, by, bw, bh) = label.Body;
            int inside = 0, outside = 0;
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                {
                    if (img[x, y] == Blue) continue;
                    if (x >= bx - 1 && x <= bx + bw && y >= by - 1 && y <= by + bh) inside++;
                    else outside++;
                }
            check(inside > 20 && outside == 0 && bx == 20 && by == 20 && bw > 10 && bh >= 20, "文字: (X0, Y0) を左上にした本体の中にだけ描く");
        }
        var boxed = label with { Background = TextBackground.Box, Fill = White };
        var (ox, oy, ow, oh) = boxed.Body;
        using (var img = new Image<Rgba32>(200, 120, Blue))
        {
            Annotator.Draw(img, new[] { boxed });
            int midY = (int)(oy + oh / 2);
            check(ow > label.Body.Width && img[(int)ox, midY] == Red && img[(int)ox + 4, midY] == White && img[(int)ox - 3, midY] == Blue,
                "文字: 帯は余白を足した角丸の四角（線は色、中は中の色）");
        }
        using (var img = new Image<Rgba32>(200, 120, Blue))
        {
            var Green = new Rgba32(0, 128, 0);
            Annotator.Draw(img, new[] { boxed with { TextColor = Green } });
            int green = 0, red = 0; // 縁から離れた中の画素（文字）の色
            for (int y = (int)oy + 4; y < oy + oh - 4; y++)
                for (int x = (int)ox + 4; x < ox + ow - 4; x++)
                {
                    if (img[x, y] == Green) green++;
                    else if (img[x, y] == Red) red++;
                }
            check(green > 10 && red == 0 && img[(int)ox, (int)(oy + oh / 2)] == Red, "文字: 文字の色は縁の色と別に決められる（決めなければ縁と同じ色）");
        }
        check((label with { FontName = "この名前の書体は無い" }).Body == label.Body && (label with { FontName = Annotator.DefaultFontName }).Body == label.Body
            && Annotator.FontNames().Contains(Annotator.DefaultFontName), "文字: 書体を選べる（無い書体・空の名前は初めの書体で描く）");
        // 2 行目が短い文字: 左寄せなら 2 行目は本体の左側に、右寄せなら右側に描く（本体の大きさは同じ）
        var lines = label with { Text = "ABCDEFGH\nI" };
        var (lx, ly, lw, lh) = lines.Body;
        int InkAt(TextLineAlign align, bool left)
        {
            using var img = new Image<Rgba32>(300, 120, Blue);
            Annotator.Draw(img, new[] { lines with { Align = align } });
            int count = 0;
            for (int y = (int)(ly + lh / 2); y < ly + lh; y++)
                for (int x = left ? (int)lx : (int)(lx + lw * 0.75); x < (left ? lx + lw * 0.25 : lx + lw); x++)
                    if (img[x, y] != Blue) count++;
            return count;
        }
        check(InkAt(TextLineAlign.Left, left: true) > 0 && InkAt(TextLineAlign.Left, left: false) == 0
            && InkAt(TextLineAlign.Right, left: false) > 0 && InkAt(TextLineAlign.Right, left: true) == 0
            && InkAt(TextLineAlign.Center, left: true) == 0 && InkAt(TextLineAlign.Center, left: false) == 0
            && (lines with { Align = TextLineAlign.Left }).Body == lines.Body, "文字: 行を 左 / 中央 / 右 にそろえられる（本体の大きさは変わらない）");
        // 本体の大きさは文字と別に決められる（文字が収まる大きさより小さくはならない）
        var roomy = boxed with { BoxWidth = 150, BoxHeight = 60 };
        check(roomy.Body is { Width: 150, Height: 60 } && (boxed with { BoxWidth = 5, BoxHeight = 5 }).Body == boxed.Body && roomy.MinBody == (ow, oh)
            && roomy.Scale(0.5).Body is { Width: 75, Height: 30 }, "文字: 本体の大きさを文字と別に決められる（文字より小さくはならない）");
        int InkIn(TextLineAlign align, double from, double to)
        {
            using var img = new Image<Rgba32>(200, 120, Blue);
            Annotator.Draw(img, new[] { roomy with { Align = align, TextColor = new Rgba32(0, 128, 0) } });
            int count = 0;
            for (int y = (int)oy + 4; y < oy + 56; y++)
                for (int x = (int)(ox + from); x < ox + to; x++)
                    if (img[x, y] != White) count++;
            return count;
        }
        check(InkIn(TextLineAlign.Left, 4, 50) > 0 && InkIn(TextLineAlign.Left, 75, 146) == 0 && InkIn(TextLineAlign.Right, 100, 146) > 0 && InkIn(TextLineAlign.Right, 4, 75) == 0
            && InkIn(TextLineAlign.Center, 4, 40) == 0 && InkIn(TextLineAlign.Center, 110, 146) == 0 && InkIn(TextLineAlign.Center, 50, 100) > 0,
            "文字: 本体が文字より広ければ、左 / 中央 / 右 に寄せる");
        var balloon = boxed with { Background = TextBackground.Balloon, X1 = ox + ow / 2, Y1 = oy + oh + 30 };
        using (var img = new Image<Rgba32>(200, 120, Blue))
        {
            Annotator.Draw(img, new[] { balloon });
            int tipX = (int)(ox + ow / 2), bottom = (int)(oy + oh);
            check(img[tipX, bottom + 10] == White && img[tipX, bottom + 34] == Blue && img[tipX + 20, bottom + 10] == Blue,
                "吹き出し: 本体から (X1, Y1) へ向かうしっぽを付ける");
        }
        using (var img = new Image<Rgba32>(200, 120, Blue))
        {
            Annotator.Draw(img, new[] { boxed with { Background = TextBackground.Balloon, X1 = ox + 5, Y1 = oy + 5 } });
            check(img[(int)(ox + ow / 2), (int)(oy + oh) + 6] == Blue, "吹き出し: しっぽの先が本体の中なら、しっぽは描かない");
        }
        var movedBalloon = balloon.MoveBody(5, 7);
        check(movedBalloon is { X0: 25, Y0: 27 } && movedBalloon.X1 == balloon.X1 && movedBalloon.Y1 == balloon.Y1 && movedBalloon.Bounds.Bottom == balloon.Y1,
            "吹き出し: 本体を動かしても、しっぽの先は指している所に残る");
        check(balloon.Distance(ox + ow / 2, oy + oh + 10) < 0 && balloon.Distance(ox + ow / 2, oy + oh / 2) < 0 && balloon.Distance(ox - 20, oy) > 10,
            "吹き出し: 本体としっぽの上は距離が負（クリックで選ぶ判定用）");
        var number = new Annotation(AnnotationKind.Number, 100, 20, 0, 0, Red, 2, 0, 4, false) { Text = "3", FontSize = 20 };
        using (var img = new Image<Rgba32>(200, 120, Blue))
        {
            Annotator.Draw(img, new[] { number });
            var (nx, ny, nw, nh) = number.Body;
            int digit = 0; // 丸の中の、丸の色でない画素（数字）
            for (int y = (int)ny + 4; y < ny + nh - 4; y++)
                for (int x = (int)nx + 4; x < nx + nw - 4; x++)
                    if (img[x, y] != Red) digit++;
            check(nw == nh && img[(int)(nx + nw / 2), (int)ny + 2] == Red && img[(int)nx + 1, (int)ny + 1] == Blue && digit > 10,
                "番号: 色で塗った丸の中に数字を描く");
            check((number with { Text = "123" }).Body.Width > nw, "番号: 桁が増えたら丸を大きくする");
        }
        var halfLabel = boxed.Scale(0.5);
        check(halfLabel.FontSize == 10 && Math.Abs(halfLabel.Body.Width - ow / 2) < 0.001 && Math.Abs(halfLabel.Body.Height - oh / 2) < 0.001,
            "文字: 縮小した画像には文字の大きさも本体も同じ倍率で描く");
        check((label with { Text = "AB\nCD" }).Body.Height > label.Body.Height * 1.9 && Annotator.DefaultFontSize(4000, 3000) == 100 && Annotator.DefaultFontSize(200, 100) == 14,
            "文字: 改行で行が増える。大きさの初めの値は長い辺に対する割合（最小 14px）");

        var halfFrame = frame.Scale(0.5);
        check(halfFrame is { X0: 10, Y0: 10, X1: 40, Y1: 30, Thickness: 2 }, "枠・矢印: 縮小した画像には位置も太さも同じ倍率で描く");
        check(frame.Distance(20, 40) < 0 && frame.Distance(50, 40) > 10 && arrow.Distance(50, 40) < 0 && arrow.Distance(50, 60) > 10,
            "枠・矢印: 線の上は距離が負、枠の中や離れた所は正（クリックで選ぶ判定用）");
        check(Annotator.DefaultThickness(4000, 3000) == 20 && Annotator.DefaultThickness(100, 100) == 2, "枠・矢印: 太さの初めの値は長い辺に対する割合（最小 2px）");
        string markOut = Annotator.OutputPathFor(P("mask.png"), dir);
        check(markOut == P("mask_mark.png"), "枠・矢印: 保存先の名前は _mark");
        using (var whole = ImageViewer.Core.Imaging.ImageLoader.Load(P("mask.png")))
        {
            var before = whole[5, 5];
            Annotator.SaveAnnotated(whole, new[] { new Annotation(AnnotationKind.Frame, 2, 2, 20, 20, Blue, 4, 0, 4, true) }, P("mask.png"), markOut);
            check(whole[2, 10] == before, "枠・矢印: 保存しても表示中の画像は変えない");
        }
        using (var img = Image.Load<Rgba32>(markOut))
            check(img.Width == 60 && img[2, 10] == Blue, "枠・矢印: 別の名前で保存できる");
        refused = false;
        using (var gif = ImageViewer.Core.Imaging.ImageLoader.Load(P("over.gif")))
        {
            try { Annotator.SaveAnnotated(gif, new[] { frame }, P("over.gif"), P("over.gif")); }
            catch (NotSupportedException) { refused = true; }
        }
        check(refused && new FileInfo(P("over.gif")).Length == gifSize, "枠・矢印: アニメーションは上書きしない（元のまま）");

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

        // ---- 別の名前で保存: 保存先を決めた後に同じ名前のファイルができても、そのファイルは置き換えない ----
        using (var img = new Image<Rgba32>(40, 20, Red)) img.SaveAsPng(P("late.png"));
        var region = new[] { MaskRegion.Rect(0, 0, 10, 10) };
        var mark = new[] { new Annotation(AnnotationKind.Frame, 2, 2, 20, 18, Blue, 2, 0, 4, false) };
        using (var whole = Image.Load<Rgba32>(P("late.png")))
        {
            var saves = new (string Name, string Decided, Func<string, string> Save)[]
            {
                ("切り抜き", Cropper.OutputPathFor(P("late.png"), dir), d => Cropper.SaveCrop(whole, new Rectangle(0, 0, 10, 10), P("late.png"), d)),
                ("切り抜き（中央・一括）", P("late_center.png"), d => Cropper.CropCenter(P("late.png"), 1.0, d)),
                ("切り抜き（引き継ぎ・一括）", P("late_carried.png"), d => Cropper.CropCarried(P("late.png"), (0, 0, 20, 10), 40, 20, d)),
                ("モザイク", Masker.OutputPathFor(P("late.png"), dir, MaskEffect.Mosaic), d => Masker.SaveMasked(whole, region, MaskEffect.Mosaic, 5, P("late.png"), d)),
                ("モザイク（一括）", P("late_all.png"), d => Masker.MaskCarried(P("late.png"), region, 40, 20, MaskEffect.Fill, 5, d)),
                ("枠・矢印", Annotator.OutputPathFor(P("late.png"), dir), d => Annotator.SaveAnnotated(whole, mark, P("late.png"), d)),
            };
            foreach (var (name, decided, save) in saves)
            {
                File.WriteAllText(decided, "other"); // 保存先を決めた後に、ほかの処理が同じ名前で作った
                string actual = save(decided);
                check(File.ReadAllText(decided) == "other" && actual != decided && Path.GetDirectoryName(actual) == dir && Image.Identify(actual).Width > 0,
                    $"{name}: 決めた保存先に後からできたファイルは置き換えず、別の名前（{Path.GetFileName(actual)}）で保存する");
            }
        }
        check(!Directory.GetFiles(dir, "*.tmp").Any(), "別の名前で保存し直したときも一時ファイルが残らない");
        using (var fresh = new Image<Rgba32>(8, 8, Blue))
        {
            string savedFirst = ImageSaver.SaveNew(fresh, P("fresh.png")), savedAgain = ImageSaver.SaveNew(fresh, P("fresh.png"));
            check(savedFirst == P("fresh.png") && savedAgain == P("fresh (2).png"), "新しいファイルとして保存: 空いていればその名前、無ければ (2)…");
        }

        // ---- 一括処理の進め方（リサイズ・まとめて補正・回転で共通）: 打ち切り・進み具合・数え方 ----
        Directory.CreateDirectory(P("batch"));
        string B(string name) => Path.Combine(P("batch"), name);
        using (var img = new Image<Rgba32>(40, 20, Red)) { img.SaveAsPng(B("1.png")); img.SaveAsPng(B("2.png")); img.SaveAsPng(B("3.png")); }
        Directory.CreateDirectory(B("resized"));
        File.WriteAllText(Path.Combine(B("resized"), "1.png"), "old"); // 計画の時点で同名がある → 飛ばす
        File.WriteAllText(B("4.png"), "画像ではない");                    // 読めない → 失敗
        var batchOptions = new ConvertOptions();
        var batchPlan = Converter.Plan(new[] { B("1.png"), B("2.png"), B("3.png"), B("4.png") }, batchOptions);
        using (var stop = new CancellationTokenSource())
        {
            var seen = new List<ConvertProgress>();
            var progress = new SyncProgress(p => { seen.Add(p); if (p.Done == 1) stop.Cancel(); }); // 2 枚目（3.png）を始めた所で打ち切る
            var stopped = Converter.Run(batchPlan, batchOptions, progress, stop.Token);
            check(stopped.Canceled && stopped.Converted == 2 && stopped.Skipped == 1 && stopped.Errors.Count == 0,
                $"一括: 打ち切ると、始めていた 1 枚は済ませて、次の 1 枚の前で止まる（済み {stopped.Converted}・飛ばした {stopped.Skipped}）");
            check(seen.Select(p => (p.Done, p.Total, p.Name)).SequenceEqual(new[] { (0, 3, "2.png"), (1, 3, "3.png") }),
                "一括: 進み具合は 1 枚ごとに始める前に知らせる（飛ばすものは数に入れない。打ち切ったら最後の知らせは出さない）");
        }
        {
            var seen = new List<ConvertProgress>();
            var all = Converter.Run(Converter.Plan(new[] { B("1.png"), B("2.png"), B("3.png"), B("4.png") }, batchOptions), batchOptions, new SyncProgress(seen.Add));
            check(!all.Canceled && all.Converted == 0 && all.Skipped == 3 && all.Errors.Count == 1 && all.Errors[0].StartsWith("4.png: "),
                $"一括: 済み・飛ばした・失敗を数える（済み {all.Converted}・飛ばした {all.Skipped}・失敗 {all.Errors.Count}）");
            check(seen.SequenceEqual(new ConvertProgress[] { new(0, 1, "4.png"), new(1, 1, "") }), "一括: 終わったら「全部済み」を知らせる");
        }

        // ---- 画像ごとに覚える編集（モザイクの範囲・枠や矢印）: 上書きした分は戻ってきても残さない ----
        var edits = new PendingEdits<string>();
        edits.Recall(0);
        edits.Current.Add("A の範囲");
        edits.Remember(0); // 保存せずに次の画像へ
        edits.Recall(1);
        check(edits.Current.Count == 0, "編集: 別の画像へ移ると、その画像の分（無ければ空）になる");
        edits.Remember(1);
        edits.Recall(0);
        check(edits.Current.SequenceEqual(new[] { "A の範囲" }), "編集: 保存していない（保存に失敗した）範囲は、戻ってきても残っている");
        edits.Discard(0);  // 上書きして次へ: ダイアログは上書きできたら捨ててから、今の編集を覚えて次の画像へ移る
        edits.Remember(0);
        edits.Recall(1);
        edits.Remember(1);
        edits.Recall(0);
        check(edits.Current.Count == 0, "編集: 上書きした画像に戻っても、かけ終えた範囲は残っていない（二重にかけない）");
        edits.Current.Add("A のやり直し");
        edits.Remember(0);
        edits.Set(new() { "引き継いだ範囲" });
        edits.Recall(0);
        check(edits.Current.SequenceEqual(new[] { "A のやり直し" }), "編集: 引き継いだ範囲に置き換えても、覚えている分は変わらない");
        edits.DiscardAll();
        edits.Recall(0);
        check(edits.Current.Count == 0, "編集: 全部に上書きしたら、覚えていた分をすべて捨てる");
    }

    /// <summary>知らせをその場で受ける（Progress&lt;T&gt; は後から別スレッドで届くので、順番を確かめられない）</summary>
    sealed class SyncProgress(Action<ConvertProgress> report) : IProgress<ConvertProgress>
    {
        public void Report(ConvertProgress value) => report(value);
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
