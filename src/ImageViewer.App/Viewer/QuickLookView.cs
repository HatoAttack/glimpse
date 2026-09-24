// Quick Look: 一覧の上に重ねて画像を大きく表示する（別ウィンドウは作らない）
// - Space を短く押すと開いたまま（もう一度 Space / Esc で閉じる）。押し続けた場合は離すと閉じる
// - ← → で前後の画像、¥（MarkKey）でチェックの付け外し、^（MarkNextKey）で付け外しして次へ
// - 表示は画面の大きさに縮小して読む。持つのは今の画像と前後 1 枚ずつの最大 3 枚、閉じたら全部解放する
// - 開いた直後はサムネイルを引き伸ばして先に見せ、裏で本来の画像を読む（待ち時間を増やさない）
// - I で右側に詳細パネル（画像には重ねず、画像は残りの幅に合わせる）
using System.Diagnostics;
using System.Drawing.Drawing2D;
using ImageViewer.App.Chrome;
using ImageViewer.App.Theming;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Thumbnails;

namespace ImageViewer.App.Viewer;

public sealed class QuickLookView : Control
{
    private const int HoldThresholdMs = 350; // これより長く Space を押していたら「押している間だけ」

    private IReadOnlyList<FileInfo> _items = Array.Empty<FileInfo>();
    private int _index = -1;
    private readonly Dictionary<string, Bitmap> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private CancellationTokenSource? _loadCts;
    private readonly Stopwatch _openedFor = new();
    private bool _openedByKey;
    private bool _spaceReleased;

    /// <summary>サムネイル（読み込みが終わるまでの仮表示用）</summary>
    public Func<FileInfo, Bitmap?>? PlaceholderProvider { get; set; }

    /// <summary>チェックの状態（一覧と共有する）</summary>
    public Func<int, bool>? IsMarked { get; set; }
    public Func<int>? MarkedCount { get; set; }

    public Keys MarkKey { get; set; } = Keys.Oem5;
    public Keys MarkNextKey { get; set; } = Keys.Oem7;

    /// <summary>表示中の画像が変わった（一覧の選択を合わせる用）。引数は画像番号</summary>
    public event EventHandler<int>? CurrentChanged;

    /// <summary>チェックの付け外しを頼む。引数は画像番号</summary>
    public event EventHandler<int>? ToggleMarkRequested;

    public event EventHandler? Closed;

    /// <summary>右側の詳細パネル（中身は本体が入れる）。1 枚表示は常に暗い地なのでダークの配色で描く</summary>
    public DetailsPanel Details { get; } = new() { Dock = DockStyle.Right, FixedPalette = Palette.Dark, Visible = false };

    /// <summary>I キーで詳細パネルを出した / 閉じた（本体が設定に保存する）</summary>
    public event EventHandler? DetailsToggled;

    /// <summary>詳細パネルを出す / 閉じる（I キー・本体の ⓘ / Ctrl+I）</summary>
    public void ToggleDetails()
    {
        Details.Visible = !Details.Visible;
        Invalidate();
        DetailsToggled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>画像を描ける幅（詳細パネルを出していればその分を除く）</summary>
    private int ContentWidth => Math.Max(1, ClientSize.Width - (Details.Visible ? Details.Width : 0));

    public bool IsOpen => Visible;
    public int CurrentIndex => _index;

    public QuickLookView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(24, 24, 24);
        ForeColor = Color.White;
        ImeMode = ImeMode.Disable;
        TabStop = true;
        Visible = false;
        Controls.Add(Details);
    }

    /// <param name="byKey">Space で開いた（押し続けたかどうかを離したときに判定する）</param>
    public void Open(IReadOnlyList<FileInfo> items, int index, bool byKey)
    {
        if (index < 0 || index >= items.Count) return;
        _items = items;
        _openedByKey = byKey;
        _spaceReleased = !byKey;
        _openedFor.Restart();
        Visible = true;
        BringToFront();
        Focus();
        ShowIndex(index);
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        _loadCts?.Cancel();
        foreach (var b in _cache.Values) b.Dispose();
        _cache.Clear();
        _failed.Clear();
        _index = -1;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>一覧の中身が入れ替わった（並べ替え・リネーム等）。同じファイルを表示し続けられなければ閉じる</summary>
    public void ItemsChanged(IReadOnlyList<FileInfo> items)
    {
        if (!Visible) return;
        string? current = _index >= 0 && _index < _items.Count ? _items[_index].FullName : null;
        int found = current == null ? -1 : items.ToList().FindIndex(f => string.Equals(f.FullName, current, StringComparison.OrdinalIgnoreCase));
        if (found < 0)
        {
            Close();
            return;
        }
        _items = items;
        _index = found;
        Invalidate();
    }

    private void ShowIndex(int index)
    {
        _index = Math.Clamp(index, 0, _items.Count - 1);
        CurrentChanged?.Invoke(this, _index);
        Invalidate();
        _ = LoadAroundAsync();
    }

    /// <summary>今の画像 → 次 → 前 の順に読む。それ以外は捨てる</summary>
    private async Task LoadAroundAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var wanted = new[] { _index, _index + 1, _index - 1 }
            .Where(i => i >= 0 && i < _items.Count)
            .Select(i => _items[i].FullName)
            .ToList();
        foreach (var key in _cache.Keys.Where(k => !wanted.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _cache[key].Dispose();
            _cache.Remove(key);
        }

        int maxEdge = Math.Max(1, Math.Max(ContentWidth, ClientSize.Height));
        foreach (var path in wanted)
        {
            if (cts.IsCancellationRequested) return;
            if (_cache.ContainsKey(path) || _failed.Contains(path)) continue;
            Bitmap? bmp = null;
            try
            {
                await _decodeGate.WaitAsync(cts.Token);
                try
                {
                    cts.Token.ThrowIfCancellationRequested();
                    bmp = await Task.Run(() =>
                    {
                        using var image = ImageLoader.Load(path, LoadOptions.Thumbnail(maxEdge));
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
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (cts.IsCancellationRequested) return;
                _failed.Add(path);
            }
            if (bmp == null) continue;
            if (cts.IsCancellationRequested || !Visible || _cache.ContainsKey(path))
            {
                bmp.Dispose(); // 閉じた・別の画像へ移った・並行した読み込みが先に入れた
                if (cts.IsCancellationRequested || !Visible) return;
                continue;
            }
            _cache[path] = bmp;
            if (_index >= 0 && string.Equals(_items[_index].FullName, path, StringComparison.OrdinalIgnoreCase)) Invalidate();
        }
    }

    // ---- 描画 ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_index < 0 || _index >= _items.Count) return;
        var file = _items[_index];
        int bar = Font.Height * 2;
        var area = new Rectangle(16, bar, Math.Max(1, ContentWidth - 32), Math.Max(1, ClientSize.Height - bar * 2));

        Rectangle imageRect = Rectangle.Empty;
        if (_cache.TryGetValue(file.FullName, out var bmp))
        {
            imageRect = Fit(bmp.Size, area, allowUpscale: false);
            g.InterpolationMode = imageRect.Width < bmp.Width ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bmp, imageRect);
        }
        else if (_failed.Contains(file.FullName))
        {
            TextRenderer.DrawText(g, "この画像は表示できません", Font, area, Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else if (PlaceholderProvider?.Invoke(file) is Bitmap thumb)
        {
            // 読み込み中: サムネイルを引き伸ばして先に見せる
            imageRect = Fit(thumb.Size, area, allowUpscale: true);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(thumb, imageRect);
        }

        if (IsMarked?.Invoke(_index) == true && !imageRect.IsEmpty) DrawCheck(g, imageRect);

        // 上: ファイル名と位置、チェック数
        var top = new Rectangle(16, 0, ContentWidth - 32, bar);
        TextRenderer.DrawText(g, $"{file.Name}    {_index + 1} / {_items.Count}", Font, top, Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        int marked = MarkedCount?.Invoke() ?? 0;
        if (marked > 0)
            TextRenderer.DrawText(g, $"チェック {marked} 枚", Font, top, Color.FromArgb(255, 170, 60),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);

        // 下: 操作の案内
        var bottom = new Rectangle(16, ClientSize.Height - bar, ContentWidth - 32, bar);
        TextRenderer.DrawText(g, $"← → 前後    {KeyName(MarkKey)} チェック    {KeyName(MarkNextKey)} チェックして次へ    I 詳細    Space / Esc 閉じる",
            Font, bottom, Color.Gray, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
    }

    private static string KeyName(Keys key) => key switch
    {
        Keys.Oem5 => "¥",
        Keys.Oem7 => "^",
        _ => new KeysConverter().ConvertToString(key) ?? key.ToString(),
    };

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

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!Visible) return;
        // 大きくなったら読み直す（小さく読んだものを引き伸ばさない）
        foreach (var b in _cache.Values) b.Dispose();
        _cache.Clear();
        _ = LoadAroundAsync();
    }

    // ---- 操作 ----

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Space
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Control && !e.Alt && e.KeyCode == MarkKey)
        {
            ToggleMarkRequested?.Invoke(this, _index);
            Invalidate();
        }
        else if (!e.Control && !e.Alt && e.KeyCode == MarkNextKey)
        {
            ToggleMarkRequested?.Invoke(this, _index);
            if (_index < _items.Count - 1) ShowIndex(_index + 1);
            else Invalidate();
        }
        else
        {
            switch (e.KeyCode)
            {
                case Keys.Left or Keys.Up when _index > 0: ShowIndex(_index - 1); break;
                case Keys.Right or Keys.Down when _index < _items.Count - 1: ShowIndex(_index + 1); break;
                case Keys.Home: ShowIndex(0); break;
                case Keys.End: ShowIndex(_items.Count - 1); break;
                case Keys.Escape: Close(); break;
                case Keys.I when !e.Control && !e.Alt: ToggleDetails(); break;
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
        if (e.KeyCode != Keys.Space || _spaceReleased) return;
        _spaceReleased = true;
        // 開いたときの Space を長く押していた = 「押している間だけ」見たい → 離したら閉じる
        if (_openedByKey && _openedFor.ElapsedMilliseconds >= HoldThresholdMs) Close();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta < 0 && _index < _items.Count - 1) ShowIndex(_index + 1);
        else if (e.Delta > 0 && _index > 0) ShowIndex(_index - 1);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        Close();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        // 別の操作（メニュー・ダイアログ等）に移ったら、押し続けの判定はやめる
        _spaceReleased = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loadCts?.Cancel();
            foreach (var b in _cache.Values) b.Dispose();
            _cache.Clear();
        }
        base.Dispose(disposing);
    }
}
