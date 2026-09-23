// サムネイルグリッド
// 見えているセル＋前後 1 画面分だけサムネイルを要求するので、メモリと処理量はファイル数ではなく画面の広さで決まる
using System.Drawing.Drawing2D;
using ImageViewer.Core.Thumbnails;

namespace ImageViewer.App.Grid;

public sealed class ThumbnailGrid : Control
{
    private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right, Enabled = false };
    private readonly SelectionModel _selection = new();
    private readonly System.Windows.Forms.Timer _autoScroll = new() { Interval = 30 };
    private readonly ThumbnailService _thumbnails;

    private IReadOnlyList<FileInfo> _items = Array.Empty<FileInfo>();
    private ThumbnailKey[] _keys = Array.Empty<ThumbnailKey>();
    private Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase);

    // 見た目の寸法（px、DPI 反映済み）
    private readonly int _thumb, _pad, _gap;
    private int TextHeight => Font.Height + _pad;

    /// <summary>選択が変わった</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>ダブルクリックまたは Enter（1 枚表示を開く用）</summary>
    public event EventHandler<int>? ItemActivated;

    public ThumbnailGrid(ThumbnailService thumbnails)
    {
        _thumbnails = thumbnails;
        _thumb = thumbnails.Size;
        _pad = LogicalToDeviceUnits(6);
        _gap = LogicalToDeviceUnits(8);

        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;

        Controls.Add(_scroll);
        _scroll.ValueChanged += (_, _) =>
        {
            Invalidate();
            RequestThumbnails();
        };
        _thumbnails.ThumbnailReady += OnThumbnailReady;
        _autoScroll.Tick += OnAutoScrollTick;
    }

    // ---- 公開 API ----

    public IReadOnlyList<FileInfo> Items => _items;
    public IReadOnlyList<int> SelectedIndices => _selection.SelectedIndices;
    public int SelectedCount => _selection.Count;

    public void SetItems(IReadOnlyList<FileInfo> items)
    {
        EndBand();
        _items = items;
        _keys = items.Select(ThumbnailKey.From).ToArray();
        _indexByPath = new Dictionary<string, int>(items.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Count; i++) _indexByPath[items[i].FullName] = i;
        _selection.Reset(items.Count);
        UpdateScrollBar();
        _scroll.Value = 0;
        Invalidate();
        RequestThumbnails();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll()
    {
        _selection.SelectAll();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- 配置・スクロール ----

    private GridLayout CurrentLayout => new(_items.Count, ClientSize.Width - _scroll.Width,
        _thumb + _pad * 2, _thumb + _pad * 2 + TextHeight, _gap);

    private int ScrollY => _scroll.Value;

    private void UpdateScrollBar()
    {
        int content = CurrentLayout.ContentHeight, view = Math.Max(1, ClientSize.Height);
        _scroll.Enabled = content > view;
        _scroll.Minimum = 0;
        _scroll.Maximum = Math.Max(0, content - 1);
        _scroll.LargeChange = view;
        _scroll.SmallChange = Math.Max(1, CurrentLayout.RowHeight);
        SetScroll(ScrollY); // 範囲が縮んだときに収める
    }

    private void SetScroll(int y)
    {
        int max = Math.Max(0, CurrentLayout.ContentHeight - ClientSize.Height);
        _scroll.Value = Math.Clamp(y, 0, max);
    }

    private void EnsureVisible(int index)
    {
        if (index < 0) return;
        var r = CurrentLayout.CellBounds(index);
        if (r.Top - _gap < ScrollY) SetScroll(r.Top - _gap);
        else if (r.Bottom + _gap > ScrollY + ClientSize.Height) SetScroll(r.Bottom + _gap - ClientSize.Height);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollBar();
        RequestThumbnails();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateScrollBar();
        Invalidate();
    }

    // ---- サムネイル要求 ----

    /// <summary>表示中 → 下方向 1 画面 → 上方向 1 画面 の順で要求（それ以外の待ちは捨てられる）</summary>
    private void RequestThumbnails()
    {
        if (_items.Count == 0)
        {
            _thumbnails.Schedule(Array.Empty<ThumbnailKey>());
            return;
        }
        var layout = CurrentLayout;
        var (first, count) = layout.VisibleRange(ScrollY, ClientSize.Height);
        int page = Math.Max(count, layout.Columns);
        var order = new List<ThumbnailKey>(count + page * 2);
        for (int i = first; i < first + count; i++) order.Add(_keys[i]);
        for (int i = first + count; i < Math.Min(_items.Count, first + count + page); i++) order.Add(_keys[i]);
        for (int i = first - 1; i >= Math.Max(0, first - page); i--) order.Add(_keys[i]);
        _thumbnails.Schedule(order);
    }

    private void OnThumbnailReady(ThumbnailKey key)
    {
        if (!_indexByPath.TryGetValue(key.Path, out int i) || i >= _keys.Length || _keys[i] != key) return;
        var r = CurrentLayout.CellBounds(i);
        r.Offset(0, -ScrollY);
        if (r.IntersectsWith(ClientRectangle)) Invalidate(r);
    }

    // ---- セル内の配置（描画と当たり判定で共通） ----

    private Rectangle ThumbArea(Rectangle cell) => new(cell.X + _pad, cell.Y + _pad, _thumb, _thumb);

    private Rectangle NameArea(Rectangle cell) =>
        new(cell.X + _pad / 2, cell.Y + _pad + _thumb + _pad / 2, cell.Width - _pad, TextHeight);

    /// <summary>枠内に中央寄せした画像の位置。枠より小さければ等倍（拡大しない）</summary>
    private static Rectangle Fit(Size image, Rectangle area)
    {
        double scale = Math.Min(1.0, Math.Min((double)area.Width / image.Width, (double)area.Height / image.Height));
        int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    /// <summary>
    /// クリックで項目を掴める場所（画像そのものと名前の文字）。セル内でもそれ以外の余白は「空き」扱いにして、
    /// そこからドラッグすると範囲選択になる（エクスプローラーの大アイコン表示と同じ）
    /// </summary>
    private int HitTest(Point client)
    {
        var p = new Point(client.X, client.Y + ScrollY);
        var layout = CurrentLayout;
        int i = layout.IndexAt(p.X, p.Y);
        if (i < 0) return -1;
        var cell = layout.CellBounds(i);

        var image = ThumbArea(cell);
        if (_thumbnails.TryGet(_keys[i], out var bmp) == ThumbnailState.Ready) image = Fit(bmp!.Size, image);
        if (image.Contains(p)) return i;

        var name = NameArea(cell);
        int textWidth = Math.Min(name.Width, TextRenderer.MeasureText(_items[i].Name, Font).Width);
        var text = new Rectangle(name.X + (name.Width - textWidth) / 2, name.Y, textWidth, name.Height);
        return text.Contains(p) ? i : -1;
    }

    // ---- 描画 ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var layout = CurrentLayout;
        var (first, count) = layout.VisibleRange(ScrollY, ClientSize.Height);
        for (int i = first; i < first + count; i++)
        {
            var cell = layout.CellBounds(i);
            cell.Offset(0, -ScrollY);
            if (cell.IntersectsWith(e.ClipRectangle)) DrawCell(g, i, cell);
        }

        if (_bandVisible)
        {
            var band = BandRect;
            band.Offset(0, -ScrollY);
            using var fill = new SolidBrush(Color.FromArgb(50, SystemColors.Highlight));
            using var border = new Pen(SystemColors.Highlight);
            g.FillRectangle(fill, band);
            g.DrawRectangle(border, band.X, band.Y, Math.Max(0, band.Width - 1), Math.Max(0, band.Height - 1));
        }
    }

    private void DrawCell(Graphics g, int index, Rectangle cell)
    {
        bool selected = _selection.IsSelected(index);
        if (selected)
        {
            using var fill = new SolidBrush(Color.FromArgb(Focused ? 70 : 40, SystemColors.Highlight));
            g.FillRectangle(fill, cell);
            using var border = new Pen(SystemColors.Highlight);
            g.DrawRectangle(border, cell.X, cell.Y, cell.Width - 1, cell.Height - 1);
        }
        if (Focused && index == _selection.Focus)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(cell, -2, -2));

        var area = ThumbArea(cell);
        switch (_thumbnails.TryGet(_keys[index], out var bmp))
        {
            case ThumbnailState.Ready:
                var dest = Fit(bmp!.Size, area);
                g.InterpolationMode = dest.Width < bmp.Width ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(bmp, dest);
                break;
            case ThumbnailState.Failed:
                g.FillRectangle(SystemBrushes.ControlLight, area);
                TextRenderer.DrawText(g, _items[index].Extension.TrimStart('.').ToUpperInvariant() + "\n読めません",
                    Font, area, SystemColors.GrayText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                break;
            default:
                g.FillRectangle(SystemBrushes.ControlLight, Rectangle.Inflate(area, -_thumb / 8, -_thumb / 8));
                break;
        }

        TextRenderer.DrawText(g, _items[index].Name, Font, NameArea(cell), ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    // ---- マウス ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int index = HitTest(e.Location);
        bool ctrl = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;

        if (e.Button == MouseButtons.Left)
        {
            if (index < 0)
            {
                // 空きからのドラッグは範囲選択。動かさずに離せば、修飾キーなしなら選択解除だけになる
                BeginBand(e.Location, ctrl ? BandMode.Toggle : shift ? BandMode.Add : BandMode.Replace);
            }
            else if (shift) _selection.ShiftClick(index, keepOthers: ctrl);
            else if (ctrl) _selection.CtrlClick(index);
            else _selection.Click(index);
        }
        else if (e.Button == MouseButtons.Right && index >= 0 && !_selection.IsSelected(index))
        {
            // 未選択の画像を右クリックしたら、それを選択してからメニューを出す（エクスプローラーと同じ）
            _selection.Click(index);
        }
        else
        {
            return;
        }
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selection.IsBanding) return;
        _bandMouse = e.Location;
        UpdateBand();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) EndBand();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) EndBand(); // Alt+Tab 等でキャプチャを失ったら範囲選択を終える
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int index = HitTest(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0) ItemActivated?.Invoke(this, index);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetScroll(ScrollY - e.Delta * CurrentLayout.RowHeight / 120);
        if (_selection.IsBanding) UpdateBand();
    }

    // ---- ドラッグ範囲選択 ----
    // 枠の始点はコンテンツ座標で持つので、ドラッグ中にスクロールしても始点は画像に張り付いたまま

    private Point _bandStart;     // コンテンツ座標
    private Point _bandMouse;     // クライアント座標（最新のマウス位置）
    private bool _bandVisible;    // ドラッグ量がしきい値を超えて枠を出したか

    private Rectangle BandRect
    {
        get
        {
            var end = BandEnd;
            int x = Math.Min(_bandStart.X, end.X), y = Math.Min(_bandStart.Y, end.Y);
            // 幅 0 の縦線でも当たるよう最低 1px
            return new Rectangle(x, y, Math.Abs(end.X - _bandStart.X) + 1, Math.Abs(end.Y - _bandStart.Y) + 1);
        }
    }

    /// <summary>枠の終点（コンテンツ座標）。画面外にはみ出た分はコンテンツの範囲に収める</summary>
    private Point BandEnd => new(
        Math.Clamp(_bandMouse.X, 0, ClientSize.Width - _scroll.Width - 1),
        Math.Clamp(_bandMouse.Y + ScrollY, 0, Math.Max(0, CurrentLayout.ContentHeight - 1)));

    private void BeginBand(Point client, BandMode mode)
    {
        _selection.BeginBand(mode);
        _bandMouse = client;
        _bandStart = new Point(client.X, client.Y + ScrollY);
        _bandVisible = false;
        Capture = true;
        _autoScroll.Start();
    }

    private void UpdateBand()
    {
        if (!_bandVisible)
        {
            var drag = SystemInformation.DragSize;
            var end = BandEnd;
            if (Math.Abs(end.X - _bandStart.X) < drag.Width && Math.Abs(end.Y - _bandStart.Y) < drag.Height) return;
            _bandVisible = true;
        }
        _selection.UpdateBand(CurrentLayout.IndicesIntersecting(BandRect));
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EndBand()
    {
        if (!_selection.IsBanding) return;
        _autoScroll.Stop();
        _selection.EndBand();
        _bandVisible = false;
        Capture = false;
        Invalidate();
    }

    /// <summary>ドラッグ中にマウスが上下の端を越えたら、越えた量に応じた速さでスクロール</summary>
    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (!_selection.IsBanding) return;
        int over = _bandMouse.Y < 0 ? _bandMouse.Y : _bandMouse.Y > ClientSize.Height ? _bandMouse.Y - ClientSize.Height : 0;
        if (over == 0) return;
        int step = Math.Sign(over) * Math.Clamp(Math.Abs(over), 4, CurrentLayout.RowHeight);
        int before = ScrollY;
        SetScroll(ScrollY + step);
        if (ScrollY != before) UpdateBand();
    }

    // ---- キーボード ----

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        var layout = CurrentLayout;
        int cols = layout.Columns;
        int pageItems = Math.Max(1, ClientSize.Height / layout.RowHeight) * cols;
        int focus = _selection.Focus;

        switch (e.KeyCode)
        {
            case Keys.Left: _selection.Move(-1, e.Shift, e.Control); break;
            case Keys.Right: _selection.Move(1, e.Shift, e.Control); break;
            case Keys.Up: _selection.Move(-cols, e.Shift, e.Control); break;
            case Keys.Down: _selection.Move(cols, e.Shift, e.Control); break;
            case Keys.PageUp: _selection.Move(-pageItems, e.Shift, e.Control); break;
            case Keys.PageDown: _selection.Move(pageItems, e.Shift, e.Control); break;
            case Keys.Home: _selection.MoveTo(0, e.Shift, e.Control); break;
            case Keys.End: _selection.MoveTo(_items.Count - 1, e.Shift, e.Control); break;
            case Keys.Space when e.Control && focus >= 0: _selection.CtrlClick(focus); break;
            case Keys.Enter when focus >= 0:
                ItemActivated?.Invoke(this, focus);
                e.Handled = true;
                return;
            default:
                return;
        }
        e.Handled = true;
        EnsureVisible(_selection.Focus);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnails.ThumbnailReady -= OnThumbnailReady;
            _autoScroll.Dispose();
        }
        base.Dispose(disposing);
    }
}
