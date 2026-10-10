// 編集ダイアログ（切り抜き / モザイク・ぼかし / 枠・矢印）を画面の外に開き、ボタンとマウスの操作で動かす動作確認。
// 部品は表示の文字で探すので、ダイアログの中の作りには依らない
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using ImageViewer.App.Dialogs;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

static class DialogTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        // ダイアログは STA のスレッドで動かす（await の続きは、このスレッドでメッセージを回して進める）
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                // await の続きがこのスレッドに戻るようにする（DoEvents は終わるたびに自動で入れた分を外してしまうので、自分で入れる）
                WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                MaskScenario(check, dir);
                AnnotateScenario(check, dir);
                AnnotateTextScenario(check, dir);
                CropScenario(check, dir);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        check(error == null, $"編集ダイアログ: 最後まで動かせる{(error == null ? "" : $"（{error}）")}");
    }

    static void MaskScenario(Action<bool, string> check, string dir)
    {
        string a = Noise(dir, "m1.png"), b = Noise(dir, "m2.png"), c = Noise(dir, "m3.png");
        using var ui = new Driver(new MaskDialog(new[] { a, b, c }));
        var dialog = (MaskDialog)ui.Form;
        check(ui.Title.StartsWith("[1/3] m1.png") && !ui.Button("上書き保存").Enabled, "モザイク: 開くと 1 枚目。範囲が無いうちは保存できない");

        ui.Drag();
        check(ui.Button("上書き保存").Enabled && ui.Button("保存").Enabled, "モザイク: ドラッグで範囲を作ると保存できる");
        byte[] before = File.ReadAllBytes(a);
        ui.Click("上書きして次へ (Shift+Enter)");
        check(ui.Title.StartsWith("[2/3]") && !File.ReadAllBytes(a).SequenceEqual(before) && dialog.SavedCount == 1, "モザイク: 上書きして次へ（元の画像を置き換えて、次の画像へ）");
        check(!ui.Button("上書き保存").Enabled, "モザイク: 「同じ範囲にかける」がオフなら、次の画像には範囲を引き継がない");
        ui.Click("◀ 前");
        check(ui.Title.StartsWith("[1/3]") && !ui.Button("上書き保存").Enabled && !ui.Button("保存").Enabled,
            "モザイク: 上書きした画像に戻っても、かけ終えた範囲は残っていない（二重にかけない）");

        // 保存に失敗したら範囲は残す（読み取り専用にして上書きを失敗させる）
        ui.Drag();
        File.SetAttributes(a, File.GetAttributes(a) | FileAttributes.ReadOnly);
        ui.Click("上書き保存");
        File.SetAttributes(a, File.GetAttributes(a) & ~FileAttributes.ReadOnly);
        check(ui.HasLabel("保存できませんでした") && ui.Button("上書き保存").Enabled && dialog.SavedCount == 1, "モザイク: 保存できなかったら知らせて、範囲は残す");
        ui.Click("次 ▶");
        ui.Click("◀ 前");
        check(ui.Title.StartsWith("[1/3]") && ui.Button("上書き保存").Enabled, "モザイク: 保存していない範囲は、行き来しても残っている");

        ui.Click("保存");
        check(File.Exists(Path.Combine(dir, "m1_mosaic.png")) && dialog.SavedCount == 2 && ui.Title.StartsWith("[1/3]"), "モザイク: 保存（別の名前で保存して、その画像のまま）");

        // 続けて送ったら、最後に頼んだ画像になる（途中の画像の読み込みは捨てる）
        ui.Button("次 ▶").PerformClick();
        ui.Button("次 ▶").PerformClick();
        ui.WaitIdle();
        check(ui.Title.StartsWith("[3/3] m3.png") && ui.Title.Contains("200 × 100"), $"モザイク: 続けて送ると最後に頼んだ画像になる（{ui.Title}）");

        // 保存している間は閉じない
        ui.Drag();
        ui.Button("保存").PerformClick();
        ui.Form.Close();
        check(!ui.Form.IsDisposed && ui.Form.Visible, "モザイク: 保存している間は閉じない");
        ui.WaitIdle();
        check(File.Exists(Path.Combine(dir, "m3_mosaic.png")) && dialog.SavedCount == 3, "モザイク: 閉じようとしても保存は終える");
        ui.Form.Close();
        check(ui.Form.IsDisposed, "モザイク: 保存が終われば閉じられる");
    }

    static void AnnotateScenario(Action<bool, string> check, string dir)
    {
        string a = Noise(dir, "a1.png"), b = Noise(dir, "a2.png");
        using var ui = new Driver(new AnnotateDialog(new[] { a, b }));
        var dialog = (AnnotateDialog)ui.Form;
        check(ui.Title.StartsWith("[1/2] a1.png") && !ui.Button("保存").Enabled, "枠・矢印: 開くと 1 枚目。描かないうちは保存できない");
        ui.Drag();
        byte[] before = File.ReadAllBytes(a);
        ui.Click("上書きして次へ (Shift+Enter)");
        check(ui.Title.StartsWith("[2/2]") && !File.ReadAllBytes(a).SequenceEqual(before), "枠・矢印: 上書きして次へ");
        ui.Click("◀ 前");
        check(ui.Title.StartsWith("[1/2]") && !ui.Button("上書き保存").Enabled, "枠・矢印: 上書きした画像に戻っても、描き終えたものは残っていない");
        ui.Drag();
        ui.Click("次 ▶");
        ui.Click("次 ▶");
        check(ui.Title.StartsWith("[1/2]") && ui.Button("保存").Enabled, "枠・矢印: 保存していないものは、行き来しても残っている");
        ui.Click("上書き保存");
        check(ui.Title.StartsWith("[1/2]") && !ui.Button("保存").Enabled && dialog.SavedCount == 2, "枠・矢印: 上書き保存（描いた後の画像を出し直して、描いたものは空に）");
        ui.Click("次 ▶");
        ui.Drag();
        ui.Click("保存して次へ (Enter)");
        check(ui.Form.IsDisposed && File.Exists(Path.Combine(dir, "a2_mark.png")) && dialog.SavedCount == 3, "枠・矢印: 最後の 1 枚を保存して次へ → 閉じる");
    }

    static void AnnotateTextScenario(Action<bool, string> check, string dir)
    {
        string a = Noise(dir, "t1.png");
        using var ui = new Driver(new AnnotateDialog(new[] { a }));
        var dialog = (AnnotateDialog)ui.Form;
        ui.Radio("文字").Checked = true;
        ui.Radio("吹き出し").Checked = true;
        ui.Drag();
        check(ui.Button("保存").Enabled && ui.HasLabel("描いたもの 1 個（選んだ文字"), "文字: 置く所から指したい所へドラッグで吹き出しを置ける");
        ui.Radio("番号").Checked = true;
        ui.Drag(dy: 150);
        ui.Drag(dx: 300, dy: 150);
        check(ui.HasLabel("描いたもの 3 個（選んだ番号: 2）"), "番号: 置くたびに 1 ずつ増える");
        ui.Click("保存");
        check(File.Exists(Path.Combine(dir, "t1_mark.png")) && dialog.SavedCount == 1, "文字: 描いたものを保存できる");
    }

    static void CropScenario(Action<bool, string> check, string dir)
    {
        string a = Noise(dir, "c1.png"), b = Noise(dir, "c2.png");
        using var ui = new Driver(new CropDialog(new[] { a, b }));
        var dialog = (CropDialog)ui.Form;
        check(ui.Title.StartsWith("[1/2] c1.png") && ui.Button("保存").Enabled, "切り抜き: 開くと 1 枚目（枠は画像全体）");
        ui.Click("保存して次へ (Enter)");
        check(ui.Title.StartsWith("[2/2]") && Width(Path.Combine(dir, "c1_crop.png")) == 200, "切り抜き: 保存して次へ");
        ui.Radio("1:1").Checked = true;
        ui.Click("上書き保存");
        check(ui.Title.StartsWith("[2/2]") && ui.Title.Contains("100 × 100") && Width(b) == 100, $"切り抜き: 上書き保存（切り抜いた後の画像を出し直す）（{ui.Title}）");
        ui.Click("全部を保存");
        check(Width(Path.Combine(dir, "c1_crop (2).png")) == 100 && Width(Path.Combine(dir, "c2_crop.png")) == 100 && dialog.SavedCount == 4,
            "切り抜き: 全部を保存（同じ比で中央から。同名があれば (2)）");
        ui.Click("次 ▶");
        check(ui.Title.StartsWith("[1/2]"), "切り抜き: 最後の次は最初に戻る");
        ui.Click("◀ 前");
        ui.Click("保存して次へ (Enter)");
        check(ui.Form.IsDisposed && File.Exists(Path.Combine(dir, "c2_crop (2).png")) && dialog.SavedCount == 5, "切り抜き: 最後の 1 枚を保存して次へ → 閉じる");
    }

    static int Width(string path) => File.Exists(path) ? Image.Identify(path).Width : -1;

    /// <summary>モザイクをかけると必ず画素が変わるよう、ばらばらの色で埋めた 200 × 100 の画像</summary>
    static string Noise(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        var random = new Random(name.Sum(ch => ch));
        using var image = new SixLabors.ImageSharp.Image<Rgba32>(200, 100);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                image[x, y] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, path);
        return path;
    }

    /// <summary>ダイアログを画面の外に開いて、表示の文字で部品を探して動かす</summary>
    sealed class Driver : IDisposable
    {
        public Form Form { get; }

        public Driver(Form form)
        {
            Form = form;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-30000, -30000);
            form.Show();
            WaitIdle();
        }

        private static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(All).Prepend(root);

        public Button Button(string text) => All(Form).OfType<Button>().First(b => b.Text == text);
        public RadioButton Radio(string text) => All(Form).OfType<RadioButton>().First(r => r.Text == text);
        public bool HasLabel(string prefix) => All(Form).OfType<Label>().Any(l => l.Text.StartsWith(prefix, StringComparison.Ordinal));

        /// <summary>◀ ▶ の横の見出し（「[1/3] 名前  （幅 × 高さ）」）</summary>
        public string Title => Button("◀ 前").Parent!.Controls.OfType<Label>().First().Text;

        private Control Canvas => All(Form).First(c => c is Panel and not FlowLayoutPanel && c.Dock == DockStyle.Fill);

        public void Click(string text)
        {
            Button(text).PerformClick();
            WaitIdle();
        }

        /// <summary>キャンバスの中央あたり（画像の中）を斜めにドラッグする（dx, dy でドラッグする所をずらす）</summary>
        public void Drag(int dx = 0, int dy = 0)
        {
            var canvas = Canvas;
            int cx = canvas.ClientSize.Width / 2 + dx, cy = canvas.ClientSize.Height / 2 + dy;
            Mouse("OnMouseDown", cx - 100, cy - 60);
            Mouse("OnMouseMove", cx, cy);
            Mouse("OnMouseMove", cx + 100, cy + 60);
            Mouse("OnMouseUp", cx + 100, cy + 60);
            WaitIdle();

            void Mouse(string method, int x, int y) =>
                typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
        }

        /// <summary>読み込みと保存が終わるまで（または閉じるまで）メッセージを回す</summary>
        public void WaitIdle()
        {
            var clock = Stopwatch.StartNew();
            int quiet = 0;
            while (quiet < 3)
            {
                Application.DoEvents();
                if (Form.IsDisposed) return;
                bool idle = !Form.UseWaitCursor && Title.Length > 0 && !Title.Contains("読み込み中");
                quiet = idle ? quiet + 1 : 0;
                if (!idle) Thread.Sleep(5);
                if (clock.ElapsedMilliseconds > 30000) throw new TimeoutException($"ダイアログが落ち着かない: {Title}");
            }
        }

        public void Dispose()
        {
            if (Form.IsDisposed) return;
            WaitIdle();
            Form.Dispose();
        }
    }
}
