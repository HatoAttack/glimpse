// 2 枚並べて比べる（一覧で 2 枚選んで Space）。似た写真からピントの良いものを選ぶ用。
// - 左右に並べ、片方を「替える側」にする（クリック・Tab で切り替え）。← → はその側だけを前後の画像に替える
// - Z を押している間は 2 枚とも 100%。カーソルの位置に合わせて、2 枚の同じ場所（画像に対する割合）を見せる
// - 見出しに縦横の大きさとファイルの大きさを出す
// 1 枚表示と同じく暗い地で描く。画面に合わせた大きさで読み、100% のときだけ原寸で読む
using System.Diagnostics;
using System.Drawing.Drawing2D;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Thumbnails;

namespace ImageViewer.App.Viewer;

public sealed class CompareView : Control
{
    private const int HoldThresholdMs = 350; // これより長く Space を押していたら「押している間だけ」
    private const Keys ActualSizeKey = Keys.Z;
    private static readonly Color ActiveColor = Color.FromArgb(91, 143, 234);

    private IReadOnlyList<ImageFile> _items = Array.Empty<ImageFile>();
    private readonly int[] _index = { -1, -1 };
    private int _active = 1; // 替える側（0 = 左、1 = 右）

    private readonly Dictionary<string, Bitmap> _fit = new(StringComparer.OrdinalIgnoreCase);  // 画面に合わせて読んだもの
    private readonly Dictionary<string, Bitmap> _full = new(StringComparer.OrdinalIgnoreCase); // 原寸で読んだもの（100% 用）
    private readonly Dictionary<string, Size> _sizes = new(StringComparer.OrdinalIgnoreCase);  // 原寸の縦横（ヘッダーから）
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase), _fullFailed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private CancellationTokenSource? _loadCts, _fullCts;

    private bool _actualSize, _openedByKey, _spaceReleased;
    private readonly Stopwatch _openedFor = new();

    /// <summary>サムネイル（読み込みが終わるまでの仮表示用）</summary>
    public Func<ImageFile, Bitmap?>? PlaceholderProvider { get; set; }
    public Func<int, bool>? IsMarked { get; set; }
    public Func<int>? MarkedCount { get; set; }
    public Keys MarkKey { get; set; } = Keys.Oem5;
    public Keys MarkNextKey { get; set; } = Keys.Oem7;

    /// <summary>チェックを付け外ししたい（画像の番号）</summary>
    public event EventHandler<int>? ToggleMarkRequested;

    /// <summary>並べている 2 枚が変わった（一覧の選択を合わせる用）</summary>
    public event EventHandler? ShownChanged;

    public event EventHandler? Closed;

    /// <summary>並べている 2 枚の画像</summary>
    public IEnumerable<ImageFile> ShownItems => _index.Where(i => i >= 0 && i < _items.Count).Select(i => _items[i]);

    public CompareView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(24, 24, 24);
        ForeColor = Color.White;
        ImeMode = ImeMode.Disable;
        TabStop = true;
        Visible = false;
    }

    /// <param name="left">左に出す画像の番号</param>
    /// <param name="right">右に出す画像の番号（最初はこちらが替える側）</param>
    /// <param name="byKey">Space で開いた（押し続けたかどうかを離したときに判定する）</param>
    public void Open(IReadOnlyList<ImageFile> items, int left, int right, bool byKey)
    {
        if (left < 0 || right < 0 || left >= items.Count || right >= items.Count || left == right) return;
        _items = items;
        _index[0] = left;
        _index[1] = right;
        _active = 1;
        _openedByKey = byKey;
        _spaceReleased = !byKey;
        _openedFor.Restart();
        Visible = true;
        BringToFront();
        Focus();
        _ = LoadAsync();
        Invalidate();
    }

    public void Close()
    {
        if (!Visible) return;
        _actualSize = false;
        Visible = false;
        _loadCts?.Cancel();
        CancelFull();
        Clear(_fit);
        Clear(_full);
        _sizes.Clear();
        _failed.Clear();
        _fullFailed.Clear();
        _index[0] = _index[1] = -1;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 一覧の中身が入れ替わった（並べ替え・外での変更など）。2 枚とも残っていれば番号を合わせ、書き換えられた画像は読み直す。
    /// どちらかが無くなったら閉じる
    /// </summary>
    public void ItemsChanged(IReadOnlyList<ImageFile> items)
    {
        if (!Visible) return;
        var old = _items;
        var found = new int[2];
        for (int side = 0; side < 2; side++)
        {
            string path = old[_index[side]].FullName;
            found[side] = items.ToList().FindIndex(f => string.Equals(f.FullName, path, StringComparison.OrdinalIgnoreCase));
            if (found[side] < 0)
            {
                Close();
                return;
            }
        }
        bool reload = false;
        for (int side = 0; side < 2; side++)
        {
            if (!Modified(old[_index[side]], items[found[side]])) continue;
            string path = items[found[side]].FullName;
            Drop(_fit, path);
            Drop(_full, path);
            _sizes.Remove(path);
            _failed.Remove(path);
            _fullFailed.Remove(path);
            reload = true;
        }
        _items = items;
        _index[0] = found[0];
        _index[1] = found[1];
        if (reload)
        {
            _ = LoadAsync();
            if (_actualSize) _ = LoadFullAsync(restart: true);
            else CancelFull();
        }
        Invalidate();

        static bool Modified(ImageFile before, ImageFile after)
        {
            try
            {
                return before.LastWriteTimeUtc != after.LastWriteTimeUtc || before.Length != after.Length || before.Version != after.Version;
            }
            catch (IOException)
            {
                return true;
            }
        }
    }

    /// <summary>サムネイルが届いた（読み込み中の仮表示を描き直す）</summary>
    public void OnThumbnailReady()
    {
        if (Visible) Invalidate();
    }

    // ---- 読み込み ----

    private string PathOf(int side) => _items[_index[side]].FullName;

    /// <summary>並べている 2 枚を画面に合わせた大きさで読む（ほかに読んだものは捨てる）</summary>
    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var wanted = new[] { PathOf(0), PathOf(1) };
        foreach (var key in _fit.Keys.Where(k => !wanted.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) Drop(_fit, key);
        int maxEdge = Math.Max(1, Math.Max(PaneBounds(0).Width, PaneBounds(0).Height));
        // 替える側を先に読む（今変えたほう）
        foreach (var path in _active == 0 ? wanted : wanted.Reverse())
        {
            if (_fit.ContainsKey(path) || _failed.Contains(path)) continue;
            try
            {
                await _decodeGate.WaitAsync(cts.Token);
                (Bitmap Bitmap, Size? Size) loaded;
                try
                {
                    cts.Token.ThrowIfCancellationRequested();
                    loaded = await Task.Run(() =>
                    {
                        var header = ImageLoader.Identify(path);
                        using var image = ImageLoader.Load(path, LoadOptions.Thumbnail(maxEdge));
                        return (ThumbnailGenerator.ToPArgbBitmap(image), header is { } h ? new Size(h.Width, h.Height) : (Size?)null);
                    });
                }
                finally
                {
                    _decodeGate.Release();
                }
                if (cts.IsCancellationRequested || !Visible || _fit.ContainsKey(path))
                {
                    loaded.Bitmap.Dispose();
                    if (cts.IsCancellationRequested || !Visible) return;
                    continue;
                }
                _fit[path] = loaded.Bitmap;
                _sizes[path] = loaded.Size ?? loaded.Bitmap.Size;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (cts.IsCancellationRequested) return;
                _failed.Add(path);
            }
            Invalidate();
        }
    }

    /// <summary>
    /// 100% 用に並べている 2 枚を原寸で読む（読み終わるまでは画面に合わせたものを引き伸ばして見せる）。
    /// restart なら読んでいる途中のものを打ち切って読み直す（送って画像が変わったとき）。そうでなければ、読んでいる途中ならそのまま待つ。
    /// 原寸は重いので 1 枚ずつ読み、読み終えたときに並べていない画像になっていたら捨てる（持つのは 2 枚分だけ）
    /// </summary>
    private async Task LoadFullAsync(bool restart = false)
    {
        if (!restart && _fullCts is { IsCancellationRequested: false }) return;
        _fullCts?.Cancel();
        var cts = _fullCts = new CancellationTokenSource();
        try
        {
            foreach (var path in new[] { PathOf(0), PathOf(1) })
            {
                if (_full.ContainsKey(path) || _fullFailed.Contains(path)) continue;
                Bitmap bmp;
                try
                {
                    await _decodeGate.WaitAsync(cts.Token);
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        bmp = await Task.Run(() =>
                        {
                            using var image = ImageLoader.Load(path, LoadOptions.Full);
                            return ThumbnailGenerator.ToPArgbBitmap(image);
                        });
                    }
                    finally
                    {
                        _decodeGate.Release();
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception) // 大きすぎてメモリが足りない場合も含む
                {
                    if (cts.IsCancellationRequested) return;
                    _fullFailed.Add(path);
                    Invalidate();
                    continue;
                }
                if (cts.IsCancellationRequested || !Visible || !IsShown(path) || _full.ContainsKey(path))
                {
                    bmp.Dispose(); // 送った・閉じた後に読み終わった
                    if (cts.IsCancellationRequested || !Visible) return;
                    continue;
                }
                _full[path] = bmp;
                _sizes[path] = bmp.Size;
                Invalidate();
            }
        }
        finally
        {
            // 読み終えた（打ち切られていない）なら、次の BeginActualSize で読み直せるようにしておく
            if (_fullCts == cts && !cts.IsCancellationRequested)
            {
                _fullCts = null;
                cts.Dispose();
            }
        }
    }

    private bool IsShown(string path) =>
        _index[0] >= 0 && _index[1] >= 0 && _items.Count > Math.Max(_index[0], _index[1])
        && (string.Equals(PathOf(0), path, StringComparison.OrdinalIgnoreCase) || string.Equals(PathOf(1), path, StringComparison.OrdinalIgnoreCase));

    private void CancelFull()
    {
        _fullCts?.Cancel();
        _fullCts = null;
    }

    /// <summary>並べていない画像の原寸を捨てる（原寸は重いので、2 枚分だけ持つ）</summary>
    private void TrimFull()
    {
        var shown = new[] { PathOf(0), PathOf(1) };
        foreach (var key in _full.Keys.Where(k => !shown.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) Drop(_full, key);
    }

    private static void Drop(Dictionary<string, Bitmap> cache, string key)
    {
        if (cache.Remove(key, out var bmp)) bmp.Dispose();
    }

    private static void Clear(Dictionary<string, Bitmap> cache)
    {
        foreach (var b in cache.Values) b.Dispose();
        cache.Clear();
    }

    // ---- 送る ----

    /// <summary>替える側を前後の画像にする（もう片方に出している画像は飛ばす）</summary>
    private void Step(int delta)
    {
        int other = _index[1 - _active];
        int next = _index[_active] + delta;
        if (next == other) next += delta;
        if (next < 0 || next >= _items.Count) return;
        _index[_active] = next;
        TrimFull();
        ShownChanged?.Invoke(this, EventArgs.Empty);
        _ = LoadAsync();
        if (_actualSize) _ = LoadFullAsync(restart: true);
        else CancelFull(); // 100% でなければ、前の画像の原寸を読み続けない
        Invalidate();
    }

    private void SetActive(int side)
    {
        if (_active == side) return;
        _active = side;
        Invalidate();
    }

    // ---- 描画 ----

    private int Bar => Font.Height * 2;

    /// <summary>左右の枠（上下の文字の行を除き、真ん中を少しあける）</summary>
    private Rectangle PaneBounds(int side)
    {
        int gap = LogicalToDeviceUnits(8), pad = LogicalToDeviceUnits(12);
        int width = Math.Max(1, (ClientSize.Width - pad * 2 - gap) / 2);
        int height = Math.Max(1, ClientSize.Height - Bar * 2);
        return new Rectangle(pad + side * (width + gap), Bar, width, height);
    }

    private int? PaneAt(Point p)
    {
        for (int side = 0; side < 2; side++)
            if (Rectangle.Inflate(PaneBounds(side), LogicalToDeviceUnits(4), 0).Contains(p)) return side;
        return null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_index[0] < 0 || _index[1] < 0) return;

        // 100% のときの見る場所: カーソルが乗っている枠の中の位置（割合）を、2 枚ともに当てる
        var cursor = PointToClient(Cursor.Position);
        (double X, double Y) at = (0.5, 0.5);
        if (_actualSize && PaneAt(cursor) is int over)
        {
            var pane = PaneBounds(over);
            at = (Math.Clamp((cursor.X - pane.X) / (double)Math.Max(1, pane.Width - 1), 0, 1),
                  Math.Clamp((cursor.Y - pane.Y) / (double)Math.Max(1, pane.Height - 1), 0, 1));
        }

        bool anyLoading = false;
        for (int side = 0; side < 2; side++)
        {
            var pane = PaneBounds(side);
            var file = _items[_index[side]];
            var state = g.Save();
            g.SetClip(pane);
            Rectangle imageRect = Rectangle.Empty;
            if (_actualSize && PaintActualSize(g, file.FullName, pane, at, out imageRect, out bool loading))
            {
                anyLoading |= loading;
            }
            else if (_fit.TryGetValue(file.FullName, out var bmp))
            {
                imageRect = Fit(bmp.Size, pane, allowUpscale: false);
                g.InterpolationMode = imageRect.Width < bmp.Width ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(bmp, imageRect);
            }
            else if (_failed.Contains(file.FullName))
            {
                TextRenderer.DrawText(g, "この画像は表示できません", Font, pane, Color.Gainsboro,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else if (PlaceholderProvider?.Invoke(file) is Bitmap thumb)
            {
                imageRect = Fit(thumb.Size, pane, allowUpscale: true);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(thumb, imageRect);
            }
            g.Restore(state);
            if (IsMarked?.Invoke(_index[side]) == true && !imageRect.IsEmpty)
                DrawCheck(g, Rectangle.Intersect(imageRect, pane));
            PaintHeader(g, side, file);
        }

        // 下: 操作の案内（100% のときは倍率も）
        var bottom = new Rectangle(16, ClientSize.Height - Bar, ClientSize.Width - 32, Bar);
        string guide = _actualSize
            ? "100%（2 枚とも）" + (anyLoading ? "  原寸を読み込み中…" : "") + "    カーソルで見る場所を動かす"
            : $"← → 替える    Tab / クリック 替える側    {KeyName(MarkKey)} チェック    {KeyName(MarkNextKey)} チェックして次へ"
              + (KeyFree(ActualSizeKey) ? "    Z 100%" : "") + "    Space / Esc 閉じる";
        TextRenderer.DrawText(g, guide, Font, bottom, Color.Gray,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    /// <summary>枠の上の見出し: 名前・何枚目か・縦横・ファイルの大きさ。替える側は青い線と明るい文字</summary>
    private void PaintHeader(Graphics g, int side, ImageFile file)
    {
        var pane = PaneBounds(side);
        var r = new Rectangle(pane.X + 4, 0, pane.Width - 8, Bar);
        bool active = side == _active;
        string size = _sizes.TryGetValue(file.FullName, out var s) ? $"    {s.Width} × {s.Height}" : "";
        string bytes;
        try
        {
            bytes = $"    {FormatBytes(file.Length)}";
        }
        catch (IOException)
        {
            bytes = "";
        }
        TextRenderer.DrawText(g, $"{(active ? "▸ " : "")}{file.Name}    {_index[side] + 1} / {_items.Count}{size}{bytes}", Font, r,
            active ? Color.White : Color.FromArgb(150, 150, 150),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (active)
            using (var line = new SolidBrush(ActiveColor))
                g.FillRectangle(line, pane.X, Bar - LogicalToDeviceUnits(3), pane.Width, LogicalToDeviceUnits(2));
        if (side == 1)
        {
            int marked = MarkedCount?.Invoke() ?? 0;
            if (marked > 0)
                TextRenderer.DrawText(g, $"チェック {marked} 枚", Font, r, Color.FromArgb(255, 170, 60),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }
    }

    /// <summary>
    /// 100% で描く。at は見る場所（画像に対する割合）。原寸の大きさがまだ分からないときは false（いつもの表示のまま）。
    /// 原寸を読み終わるまでは、画面に合わせたものを原寸の大きさに引き伸ばして見せる
    /// </summary>
    private bool PaintActualSize(Graphics g, string path, Rectangle pane, (double X, double Y) at, out Rectangle dest, out bool loading)
    {
        dest = Rectangle.Empty;
        loading = false;
        if (_fullFailed.Contains(path) || !_sizes.TryGetValue(path, out var size)) return false;
        Image? image = _full.TryGetValue(path, out var full) ? full : _fit.TryGetValue(path, out var fit) ? fit : null;
        if (image == null) return false;
        loading = image != full;

        int Offset(int imageLength, int viewLength, double t) => imageLength <= viewLength
            ? (viewLength - imageLength) / 2
            : -(int)Math.Round((imageLength - viewLength) * t);
        dest = new Rectangle(pane.X + Offset(size.Width, pane.Width, at.X), pane.Y + Offset(size.Height, pane.Height, at.Y), size.Width, size.Height);

        g.PixelOffsetMode = PixelOffsetMode.Half;
        if (image == full)
        {
            // 画素をそのまま写す（ぼかさない）。見えている部分だけ描く
            var visible = Rectangle.Intersect(dest, pane);
            var source = new Rectangle(visible.X - dest.X, visible.Y - dest.Y, visible.Width, visible.Height);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(full, visible, source, GraphicsUnit.Pixel);
        }
        else
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(image, dest);
        }
        return true;
    }

    private static Rectangle Fit(Size image, Rectangle area, bool allowUpscale)
    {
        double scale = Math.Min((double)area.Width / image.Width, (double)area.Height / image.Height);
        if (!allowUpscale) scale = Math.Min(1.0, scale);
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    private void DrawCheck(Graphics g, Rectangle image)
    {
        int d = LogicalToDeviceUnits(34);
        var r = new Rectangle(image.X + 8, image.Y + 8, d, d);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(Color.FromArgb(232, 112, 0))) g.FillEllipse(fill, r);
        using (var ring = new Pen(Color.White, 2.5f)) g.DrawEllipse(ring, r);
        using (var tick = new Pen(Color.White, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(tick, new[]
            {
                new PointF(r.X + d * 0.27f, r.Y + d * 0.52f),
                new PointF(r.X + d * 0.44f, r.Y + d * 0.68f),
                new PointF(r.X + d * 0.74f, r.Y + d * 0.34f),
            });
        g.SmoothingMode = SmoothingMode.None;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    private static string KeyName(Keys key) => key switch
    {
        Keys.Oem5 => "¥",
        Keys.Oem7 => "^",
        _ => new KeysConverter().ConvertToString(key) ?? key.ToString(),
    };

    /// <summary>チェックのキーと同じキーなら、チェックを優先して 100% には使わない</summary>
    private bool KeyFree(Keys key) => MarkKey != key && MarkNextKey != key;

    // ---- 操作 ----

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!Visible || _index[0] < 0) return;
        Clear(_fit); // 大きさが変わったら読み直す（小さく読んだものを引き伸ばさない）
        _ = LoadAsync();
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Space or Keys.Tab
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Control && !e.Alt && e.KeyCode == MarkKey)
        {
            ToggleMarkRequested?.Invoke(this, _index[_active]);
            Invalidate();
        }
        else if (!e.Control && !e.Alt && e.KeyCode == MarkNextKey)
        {
            ToggleMarkRequested?.Invoke(this, _index[_active]);
            Step(+1);
            Invalidate();
        }
        else
        {
            switch (e.KeyCode)
            {
                case Keys.Left or Keys.Up: Step(-1); break;
                case Keys.Right or Keys.Down: Step(+1); break;
                case Keys.Tab: SetActive(1 - _active); break;
                case Keys.Escape: Close(); break;
                case ActualSizeKey when !e.Control && !e.Alt && KeyFree(ActualSizeKey): BeginActualSize(); break;
                // 開いたときの Space を押し続けている間（キーリピート）は閉じない
                case Keys.Space when _spaceReleased: Close(); break;
                default: return;
            }
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == ActualSizeKey) EndActualSize();
        if (e.KeyCode != Keys.Space || _spaceReleased) return;
        _spaceReleased = true;
        // 開いたときの Space を長く押していた = 「押している間だけ」見たい → 離したら閉じる
        if (_openedByKey && _openedFor.ElapsedMilliseconds >= HoldThresholdMs) Close();
    }

    private void BeginActualSize()
    {
        // Space を押し続けて見ているとき（離したら閉じる）は使わない。ちらっと見るだけの表示なので
        if (_actualSize || !_spaceReleased) return;
        _actualSize = true;
        _ = LoadFullAsync(); // 読んだ原寸は閉じるまで（替えるまで）持つので、2 回目からはすぐ

        Invalidate();
    }

    private void EndActualSize()
    {
        if (!_actualSize) return;
        _actualSize = false;
        Invalidate();
    }

    /// <summary>ホイールでは何もしない（後ろの一覧へも回さない）</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Clicks == 1 && PaneAt(e.Location) is int side) SetActive(side);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_actualSize) Invalidate(); // 100% のときはカーソルで見える場所が動く
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        Close();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        EndActualSize(); // Z を離したことが届かないので戻す
        _spaceReleased = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loadCts?.Cancel();
            _fullCts?.Cancel();
            Clear(_fit);
            Clear(_full);
            _decodeGate.Dispose();
        }
        base.Dispose(disposing);
    }
}
