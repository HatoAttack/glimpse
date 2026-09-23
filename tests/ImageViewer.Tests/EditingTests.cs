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

        // ---- 切り抜き ----
        var (cx, cy, cw, ch) = Cropper.CenterRect(400, 300, 1.0);
        check(cx == 50 && cy == 0 && cw == 300 && ch == 300, "切り抜き: 中央の 1:1");
        check(Cropper.Flip(4.0 / 3, true) == 0.75, "切り抜き: 縦横の入れ替え");
        check(Cropper.ClampBox(-3, 2.4, 500, 99.6, 400, 300) == Rectangle.FromLTRB(0, 2, 400, 100), "切り抜き: 画像の範囲に丸める");
        Cropper.CropCenter(src, 1.0, Cropper.OutputPathFor(src, dir));
        string second = Cropper.OutputPathFor(src, dir);
        check(second == P("photo_crop (2).jpg"), "切り抜き: 同名があれば (2) を付ける");
        using (var img = Image.Load<Rgba32>(P("photo_crop.jpg")))
            check(img.Width == 1000 && img.Height == 1000, $"切り抜き: 回転を反映した画像の中央 1:1（{img.Width}x{img.Height}）");

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
