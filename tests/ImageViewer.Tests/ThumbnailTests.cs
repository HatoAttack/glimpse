// サムネイル生成・キャッシュ・グリッド配置・選択の動作確認
using System.Collections.Concurrent;
using ImageViewer.App.Grid;
using ImageViewer.Core.Thumbnails;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;

static class ThumbnailTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        LayoutCases(check);
        SelectionCases(check);
        ServiceCases(check);
        GeneratorCases(check, dir);
    }

    // ---- GridLayout ----

    static void LayoutCases(Action<bool, string> check)
    {
        // セル 100x120、余白 10、幅 345 → (345-10)/110 = 3 列、余り 5px を左右に振り分け
        var l = new GridLayout(ItemCount: 10, ClientWidth: 345, CellWidth: 100, CellHeight: 120, Gap: 10);
        check(l.Columns == 3 && l.Rows == 4, $"列数・行数 ({l.Columns}列 {l.Rows}行)");
        check(l.ContentHeight == 4 * 130 + 10, "コンテンツの高さ");
        var r4 = l.CellBounds(4);
        check(r4 == new System.Drawing.Rectangle(2 + 10 + 110, 10 + 130, 100, 120), $"セル位置 {r4}");
        check(l.IndexAt(r4.X + 50, r4.Y + 60) == 4, "セル中央の当たり判定");
        check(l.IndexAt(r4.X - 5, r4.Y + 60) == -1, "余白は -1");
        check(l.IndexAt(l.CellBounds(9).X + 150, l.CellBounds(9).Y + 10) == -1, "最終行の空きセルは -1");
        check(new GridLayout(3, 50, 100, 120, 10).Columns == 1, "幅が足りなくても最低 1 列");

        // スクロール 200・高さ 150: 行1（y140〜260）と行2（y270〜390）が掛かる
        var (first, count) = l.VisibleRange(200, 150);
        check(first == 3 && count == 6, $"表示範囲 scroll=200 → {first}から{count}個");
        check(l.VisibleRange(0, 5) == (0, 0), "上端の余白だけが見えるときは空");
        check(l.VisibleRange(0, 10000) == (0, 10), "全部見えるときは全件");
        check(new GridLayout(0, 345, 100, 120, 10).VisibleRange(0, 500) == (0, 0), "0 件なら空");
    }

    // ---- SelectionModel ----

    static void SelectionCases(Action<bool, string> check)
    {
        var s = new SelectionModel();
        s.Reset(20);
        s.Click(3);
        check(s.SelectedIndices.SequenceEqual(new[] { 3 }), "クリックで 1 つ選択");
        s.CtrlClick(7);
        s.CtrlClick(9);
        s.CtrlClick(7);
        check(s.SelectedIndices.SequenceEqual(new[] { 3, 9 }), "Ctrl+クリックで反転");
        s.Click(5);
        s.ShiftClick(8);
        check(s.SelectedIndices.SequenceEqual(new[] { 5, 6, 7, 8 }), "Shift+クリックで範囲");
        s.ShiftClick(2);
        check(s.SelectedIndices.SequenceEqual(new[] { 2, 3, 4, 5 }) && s.Anchor == 5, "範囲は起点から張り直す（起点は動かない）");
        s.CtrlClick(10);
        s.ShiftClick(12, keepOthers: true);
        check(s.SelectedIndices.SequenceEqual(new[] { 2, 3, 4, 5, 10, 11, 12 }), "Ctrl+Shift で範囲を追加");

        s.Click(0);
        s.Move(-1, shift: false, ctrl: false);
        check(s.Focus == 0 && s.SelectedIndices.SequenceEqual(new[] { 0 }), "先頭より前には行かない");
        s.Move(4, shift: true, ctrl: false);
        check(s.SelectedIndices.SequenceEqual(new[] { 0, 1, 2, 3, 4 }), "Shift+移動で範囲を伸ばす");
        s.Move(5, shift: false, ctrl: true);
        check(s.Focus == 9 && s.Count == 5, "Ctrl+移動は選択を変えない");
        s.MoveTo(100, shift: false, ctrl: false);
        check(s.Focus == 19 && s.SelectedIndices.SequenceEqual(new[] { 19 }), "末尾で止まる");
        s.SelectAll();
        check(s.Count == 20, "すべて選択");
        s.Reset(3);
        check(s.Count == 0 && s.Focus == -1, "Reset で解除");
        s.ShiftClick(2);
        check(s.SelectedIndices.SequenceEqual(new[] { 2 }), "起点が無い Shift+クリックは通常クリック扱い");
    }

    // ---- ThumbnailService（生成処理は差し替え） ----

    static void ServiceCases(Action<bool, string> check)
    {
        var ctx = new QueueContext();
        var order = new ConcurrentQueue<string>();
        using var gate = new ManualResetEventSlim(true);
        Bitmap Fake(string path, int size)
        {
            gate.Wait();
            order.Enqueue(Path.GetFileName(path));
            if (path.Contains("bad")) throw new InvalidDataException();
            return new Bitmap(10, 10); // 400 バイト扱い
        }
        ThumbnailKey K(string name) => new(Path.Combine("C:\\t", name), 0, 0);

        var svc = new ThumbnailService(10, memoryBudget: 1000, ctx, Fake, workers: 1);
        var ready = new List<string>();
        svc.ThumbnailReady += k => ready.Add(Path.GetFileName(k.Path));

        svc.Schedule(new[] { K("a"), K("b"), K("bad") });
        ctx.PumpUntil(() => ready.Count == 3);
        check(order.SequenceEqual(new[] { "a", "b", "bad" }), "優先順どおりに生成");
        check(svc.TryGet(K("a"), out var a) == ThumbnailState.Ready && a != null, "生成済みは Ready");
        check(svc.TryGet(K("bad"), out _) == ThumbnailState.Failed, "失敗は Failed");
        check(svc.TryGet(K("zzz"), out _) == ThumbnailState.None, "未要求は None");

        // 待ち行列の差し替え: c の生成中に [f] だけを要求し直すと d, e は捨てられる
        gate.Reset();
        svc.Schedule(new[] { K("c"), K("d"), K("e") });
        SpinWait.SpinUntil(() => svc.TryGet(K("d"), out _) == ThumbnailState.Pending, 1000);
        Thread.Sleep(100); // ワーカーが c を取り出すのを待つ
        svc.Schedule(new[] { K("f"), K("a"), K("bad") }); // a（生成済み）と bad（失敗済み）は再生成しない
        check(svc.TryGet(K("d"), out _) == ThumbnailState.None, "差し替えで古い待ちは捨てる");
        gate.Set();
        ctx.PumpUntil(() => ready.Contains("f"));
        check(order.Skip(3).SequenceEqual(new[] { "c", "f" }), $"生成順 {string.Join(",", order.Skip(3))}（c → f のみ）");

        // 上限 1000 バイト = 10x10（400 バイト）2 枚まで。a, b, c, f の 4 枚から古い a, b が消えて Dispose される
        check(svc.CachedCount == 2 && svc.CachedBytes == 800, $"上限超過で古い順に破棄（{svc.CachedCount} 枚 / {svc.CachedBytes} バイト）");
        check(svc.TryGet(K("a"), out _) == ThumbnailState.None && IsDisposed(a!), "破棄されたものは Dispose 済み");

        svc.Dispose();
        check(svc.CachedCount == 0, "Dispose でキャッシュを空に");
    }

    // ---- 実際の生成（シェル → ImageLoader） ----

    static void GeneratorCases(Action<bool, string> check, string dir)
    {
        string jpg = Path.Combine(dir, "thumb.jpg");
        using (var img = new Image<Rgba32>(800, 400, new Rgba32(255, 0, 0))) img.SaveAsJpeg(jpg);

        using (var shell = ShellThumbnail.TryGet(jpg, 128))
            check(shell != null && Math.Max(shell.Width, shell.Height) <= 128 && IsRed(shell.GetPixel(shell.Width / 2, shell.Height / 2)),
                $"シェルのサムネイル取得（{shell?.Width}x{shell?.Height}）");

        string png = Path.Combine(dir, "alpha.png");
        // 左半分が不透明の赤、右半分が透明（全面透明だと「アルファを使わないハンドラ」と区別できないため）
        using (var img = new Image<Rgba32>(300, 300, new Rgba32(0, 0, 255, 0)))
        {
            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < 300; y++) acc.GetRowSpan(y)[..150].Fill(new Rgba32(255, 0, 0));
            });
            img.SaveAsPng(png);
        }
        using (var thumb = ThumbnailGenerator.Generate(png, 100))
        {
            var left = thumb.GetPixel(thumb.Width / 5, thumb.Height / 2);
            var right = thumb.GetPixel(thumb.Width * 4 / 5, thumb.Height / 2);
            check(IsRed(left) && left.A == 255 && right.A == 0,
                $"透過 PNG は透過のまま（{thumb.Width}x{thumb.Height}, 左={left}, 右 A={right.A}）");
        }

        string txt = Path.Combine(dir, "x.txt");
        File.WriteAllText(txt, "x");
        bool threw = false;
        try { ThumbnailGenerator.Generate(txt, 100).Dispose(); } catch (NotSupportedException) { threw = true; }
        check(threw, "画像でないファイルは例外");

        using (var img = new Image<Rgba32>(4, 2, new Rgba32(200, 100, 50, 128)))
        using (var bmp = ThumbnailGenerator.ToPArgbBitmap(img))
        {
            var p = bmp.GetPixel(0, 0); // GetPixel は乗算前の値に戻して返す
            check(bmp.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppPArgb
                  && Math.Abs(p.R - 200) <= 2 && Math.Abs(p.G - 100) <= 2 && p.A == 128, $"PArgb 変換 {p}");
        }
    }

    static bool IsRed(Color c) => c.R > 200 && c.G < 60 && c.B < 60;

    static bool IsDisposed(Bitmap b)
    {
        try { _ = b.Width; return false; }
        catch (ArgumentException) { return true; }
    }

    /// <summary>UI スレッドの代わり: Post された処理を溜めておき、テスト側で実行する</summary>
    sealed class QueueContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void PumpUntil(Func<bool> done, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!done() && DateTime.UtcNow < deadline)
                if (_queue.TryTake(out var item, 50)) item.Item1(item.Item2);
            if (!done()) throw new TimeoutException("ThumbnailReady が来ない");
        }
    }
}
