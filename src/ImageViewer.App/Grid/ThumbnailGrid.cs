// サムネイルグリッド
// 見えているセル＋前後 1 画面分だけサムネイルを要求するので、メモリと処理量はファイル数ではなく画面の広さで決まる
using System.Drawing.Drawing2D;
using ImageViewer.Core.Thumbnails;

namespace ImageViewer.App.Grid;

public sealed class ThumbnailGrid : Control
{
    private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right, Enabled = false };
    private readonly SelectionModel _selection = new();
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
    }

    // ---- 公開 API ----

    public IReadOnlyList<FileInfo> Items => _items;
    public IReadOnlyList<int> SelectedIndices => _selection.SelectedIndices;
    public int SelectedCount => _selection.Count;

    public void SetItems(IReadOnlyList<FileInfo> items)
    {
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

        var area = new Rectangle(cell.X + _pad, cell.Y + _pad, _thumb, _thumb);
        switch (_thumbnails.TryGet(_keys[index], out var bmp))
        {
            case ThumbnailState.Ready:
                DrawFitted(g, bmp!, area);
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

        var textRect = new Rectangle(cell.X + _pad / 2, area.Bottom + _pad / 2, cell.Width - _pad, TextHeight);
        TextRenderer.DrawText(g, _items[index].Name, Font, textRect, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    /// <summary>枠内に中央寄せ。枠より小さければ等倍（拡大しない）</summary>
    private static void DrawFitted(Graphics g, Bitmap bmp, Rectangle area)
    {
        double scale = Math.Min(1.0, Math.Min((double)area.Width / bmp.Width, (double)area.Height / bmp.Height));
        int w = Math.Max(1, (int)Math.Round(bmp.Width * scale)), h = Math.Max(1, (int)Math.Round(bmp.Height * scale));
        var dest = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        g.InterpolationMode = scale < 1.0 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(bmp, dest);
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
        int index = CurrentLayout.IndexAt(e.X, e.Y + ScrollY);
        bool ctrl = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;

        if (e.Button == MouseButtons.Left)
        {
            if (index < 0)
            {
                if (!ctrl && !shift) _selection.Clear();
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

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int index = CurrentLayout.IndexAt(e.X, e.Y + ScrollY);
        if (e.Button == MouseButtons.Left && index >= 0) ItemActivated?.Invoke(this, index);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetScroll(ScrollY - e.Delta * CurrentLayout.RowHeight / 120);
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
        if (disposing) _thumbnails.ThumbnailReady -= OnThumbnailReady;
        base.Dispose(disposing);
    }
}
