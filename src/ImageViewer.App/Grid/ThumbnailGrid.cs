// サムネイルグリッド
// 見えているセル＋前後 1 画面分だけサムネイルを要求するので、メモリと処理量はファイル数ではなく画面の広さで決まる
// 並びは「サブフォルダのタイル（先頭）＋画像」。セル番号 i < FolderCount がフォルダ、それ以降が画像（画像番号 = i - FolderCount）。
// チェック・手動の並べ替え・コマンドの対象は画像だけ
using System.Drawing.Drawing2D;
using ImageViewer.App.Commands;
using ImageViewer.App.Theming;
using ImageViewer.Core.Ordering;
using ImageViewer.Core.Thumbnails;

namespace ImageViewer.App.Grid;

public sealed class ThumbnailGrid : Control
{
    private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right, Enabled = false };
    private readonly SelectionModel _selection = new();
    private readonly MarkSet _marks = new();
    private readonly System.Windows.Forms.Timer _autoScroll = new() { Interval = 30 };
    private readonly ThumbnailService _thumbnails;

    private IReadOnlyList<DirectoryInfo> _folders = Array.Empty<DirectoryInfo>();
    private IReadOnlyList<FileInfo> _items = Array.Empty<FileInfo>();
    private ThumbnailKey[] _keys = Array.Empty<ThumbnailKey>();
    private Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase); // パス → セル番号
    private Bitmap? _folderIcon;
    private bool _folderIconLoaded;

    private int F => _folders.Count;
    private int CellCount => _folders.Count + _items.Count;
    private bool IsFolder(int cell) => cell < _folders.Count;
    private string CellPath(int cell) => IsFolder(cell) ? _folders[cell].FullName : _items[cell - F].FullName;
    private string CellName(int cell) => IsFolder(cell) ? _folders[cell].Name : _items[cell - F].Name;

    // 見た目の寸法（px、DPI 反映済み）
    private int _thumb;
    private readonly int _pad, _gap;
    private int TextHeight => Font.Height + _pad;

    /// <summary>選択が変わった</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>チェックが変わった</summary>
    public event EventHandler? MarksChanged;

    /// <summary>ドラッグで並べ替えた（新しい並びは Items）</summary>
    public event EventHandler? OrderChanged;

    /// <summary>エクスプローラー等からフォルダが（フォルダのタイル以外の場所に）ドロップされた</summary>
    public event EventHandler<string>? FolderDropped;

    /// <summary>フォルダのタイルに画像・ファイルがドロップされた（移動 / コピーは本体が行う）</summary>
    public event EventHandler<FileDrop>? FilesDroppedOnFolder;

    /// <summary>エクスプローラー等へドラッグして、画像が移動された（元の場所から無くなった）</summary>
    public event EventHandler<IReadOnlyList<string>>? FilesMovedOut;

    /// <summary>画像をダブルクリックまたは Enter（1 枚表示を開く用）。引数は画像番号</summary>
    public event EventHandler<int>? ItemActivated;

    /// <summary>並び・中身が入れ替わった（フォルダ移動・再読み込み・並べ替え・リネーム）</summary>
    public event EventHandler? ContentsChanged;

    /// <summary>画像の上で Space（Quick Look を開く）。引数は画像番号</summary>
    public event EventHandler<int>? PeekRequested;

    /// <summary>チェックを付け外しするキー（その場に留まる）/ 付け外しして次へ進むキー</summary>
    public Keys MarkKey { get; set; } = Keys.Oem5;
    public Keys MarkNextKey { get; set; } = Keys.Oem7;

    /// <summary>フォルダのタイルをダブルクリックまたは Enter（そのフォルダへ移動）</summary>
    public event EventHandler<DirectoryInfo>? FolderActivated;

    /// <summary>サムネイルの表示サイズを変えられる範囲（論理 px。DPI は掛ける前）</summary>
    public const int MinThumbnailSize = 64, MaxThumbnailSize = 320, ThumbnailSizeStep = 16;

    /// <summary>
    /// 作るサムネイルの大きさの段階（論理 px）。表示サイズ以上で一番小さい段階で作り、縮小して描く。
    /// スライダーを動かしても段階をまたがない限り作り直さない
    /// </summary>
    private static readonly int[] GenerationSteps = { 96, 160, 256, 320 };

    /// <summary>表示サイズが変わった（Ctrl+ホイールで変えたときも。引数は論理 px）</summary>
    public event EventHandler<int>? ThumbnailSizeChanged;

    /// <param name="thumbnailSize">サムネイルの表示サイズ（論理 px）</param>
    public ThumbnailGrid(ThumbnailService thumbnails, int thumbnailSize = 160)
    {
        _thumbnails = thumbnails;
        _thumb = LogicalToDeviceUnits(Math.Clamp(thumbnailSize, MinThumbnailSize, MaxThumbnailSize));
        _thumbnails.SetSize(GenerationSizeFor(_thumb));
        _pad = LogicalToDeviceUnits(6);
        _gap = LogicalToDeviceUnits(8);

        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AllowDrop = true;
        // 日本語入力がオンだと ¥ や ^ が文字入力に取られてキーとして届かないので、このコントロールでは使わない
        ImeMode = ImeMode.Disable;
        BackColor = Theme.Current.Background;
        ForeColor = Theme.Current.Text;

        Controls.Add(_scroll);
        _scroll.HandleCreated += (_, _) => Theme.ApplyNativeTheme(_scroll);
        _scroll.ValueChanged += (_, _) =>
        {
            Invalidate();
            RequestThumbnails();
        };
        _thumbnails.ThumbnailReady += OnThumbnailReady;
        _autoScroll.Tick += OnAutoScrollTick;
    }

    // ---- 公開 API ----

    /// <summary>今の配色で描き直す</summary>
    public void ApplyTheme()
    {
        BackColor = Theme.Current.Background;
        ForeColor = Theme.Current.Text;
        Theme.ApplyNativeTheme(_scroll);
        Invalidate();
    }

    /// <summary>画像（フォルダのタイルは含まない）</summary>
    public IReadOnlyList<FileInfo> Items => _items;
    public IReadOnlyList<DirectoryInfo> Folders => _folders;

    /// <summary>選択中の画像（画面の並び順）。コマンドの対象</summary>
    public IReadOnlyList<FileInfo> SelectedImages =>
        _selection.SelectedIndices.Where(i => !IsFolder(i)).Select(i => _items[i - F]).ToList();

    /// <summary>選択中のフォルダのタイル</summary>
    public IReadOnlyList<DirectoryInfo> SelectedFolders =>
        _selection.SelectedIndices.Where(IsFolder).Select(i => _folders[i]).ToList();

    /// <summary>選択中の画像の番号（Items での位置）</summary>
    private IEnumerable<int> SelectedImageIndices => _selection.SelectedIndices.Where(i => !IsFolder(i)).Select(i => i - F);

    public int SelectedCount => _selection.Count;

    /// <summary>画像だけを入れ替える（フォルダのタイルはそのまま）。並べ替え・リネーム後に使う</summary>
    public void SetItems(IReadOnlyList<FileInfo> items, bool reload = false, IReadOnlyDictionary<string, string>? renamed = null) =>
        SetContents(_folders, items, reload, renamed);

    /// <param name="reload">同じフォルダの読み直し（F5・並べ替え・リネーム後）なら true。チェック・選択・スクロール位置を引き継ぐ</param>
    /// <param name="renamed">名前を変えたファイル（元のパス → 新しいパス）。チェックと選択を付け替える</param>
    public void SetContents(IReadOnlyList<DirectoryInfo> folders, IReadOnlyList<FileInfo> items, bool reload = false,
        IReadOnlyDictionary<string, string>? renamed = null)
    {
        EndBand();
        string Map(string path) => renamed != null && renamed.TryGetValue(path, out var to) ? to : path;
        var selectedPaths = reload ? _selection.SelectedIndices.Select(i => Map(CellPath(i))).ToList() : new List<string>();
        int focusCell = _selection.Focus;
        string? focusPath = reload && focusCell >= 0 && focusCell < CellCount ? Map(CellPath(focusCell)) : null;
        if (renamed != null) _marks.Remap(renamed);
        int scrollY = reload ? ScrollY : 0;

        _folders = folders;
        _items = items;
        _keys = items.Select(ThumbnailKey.From).ToArray();
        _indexByPath = new Dictionary<string, int>(CellCount, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < CellCount; i++) _indexByPath[CellPath(i)] = i;

        _selection.Reset(CellCount);
        if (reload)
        {
            _marks.Retain(items.Select(f => f.FullName));
            _selection.Select(selectedPaths.Where(_indexByPath.ContainsKey).Select(p => _indexByPath[p]));
            if (focusPath != null && _indexByPath.TryGetValue(focusPath, out int f)) _selection.SetFocus(f);
        }
        else
        {
            _marks.Clear();
        }

        UpdateScrollBar();
        SetScroll(scrollY);
        Invalidate();
        RequestThumbnails();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        MarksChanged?.Invoke(this, EventArgs.Empty);
        ContentsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll()
    {
        _selection.SelectAll();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- サムネイルの大きさ ----

    /// <summary>表示サイズ（論理 px）</summary>
    public int ThumbnailSize
    {
        get => DeviceToLogical(_thumb);
        set => SetThumbnailSize(value, raiseEvent: false);
    }

    private int DeviceToLogical(int px) => (int)Math.Round(px * 96.0 / DeviceDpi);

    private int GenerationSizeFor(int devicePx)
    {
        foreach (int step in GenerationSteps)
        {
            int d = LogicalToDeviceUnits(step);
            if (d >= devicePx) return d;
        }
        return devicePx;
    }

    private void SetThumbnailSize(int logical, bool raiseEvent)
    {
        logical = Math.Clamp(logical, MinThumbnailSize, MaxThumbnailSize);
        int device = LogicalToDeviceUnits(logical);
        if (device == _thumb) return;

        // 表示の先頭にあった項目が、大きさを変えた後も先頭に来るようにする
        var (first, _) = CurrentLayout.VisibleRange(ScrollY, ClientSize.Height);
        _thumb = device;
        _thumbnails.SetSize(GenerationSizeFor(device)); // 段階が同じなら何もしない（作ってあるものを縮小して描く）
        _folderIcon?.Dispose();
        _folderIcon = null;
        _folderIconLoaded = false;

        UpdateScrollBar();
        if (CellCount > 0) SetScroll(CurrentLayout.CellBounds(Math.Min(first, CellCount - 1)).Top - _gap);
        Invalidate();
        RequestThumbnails();
        if (raiseEvent) ThumbnailSizeChanged?.Invoke(this, logical);
    }

    /// <summary>画像（Items での番号）だけを選択して見える位置へ</summary>
    public void SelectImage(int imageIndex)
    {
        if (imageIndex < 0 || imageIndex >= _items.Count) return;
        _selection.Click(F + imageIndex);
        EnsureVisible(F + imageIndex);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsImageMarked(int imageIndex) => imageIndex >= 0 && imageIndex < _items.Count && _marks.IsMarked(_items[imageIndex].FullName);

    public void ToggleImageMark(int imageIndex)
    {
        if (imageIndex < 0 || imageIndex >= _items.Count) return;
        _marks.Toggle(_items[imageIndex].FullName);
        OnMarksChanged();
    }

    /// <summary>指定したパスの項目（フォルダのタイルも）だけを選択して見える位置へ</summary>
    public void SelectPath(string path)
    {
        if (!_indexByPath.TryGetValue(path, out int i)) return;
        _selection.Click(i);
        EnsureVisible(i);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>指定したパスの項目（フォルダのタイルも）を選択して、先頭を見える位置へ。貼り付けたものの選択に使う</summary>
    public void SelectPaths(IEnumerable<string> paths)
    {
        _selection.Select(paths.Where(_indexByPath.ContainsKey).Select(p => _indexByPath[p]));
        if (_selection.Count > 0) EnsureVisible(_selection.Focus);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- チェック ----

    public int MarkedCount => _marks.Count;

    /// <summary>チェックした画像（画面の並び順）</summary>
    public IReadOnlyList<FileInfo> MarkedImages => _items.Where(f => _marks.IsMarked(f.FullName)).ToList();

    /// <summary>選択を解除する（フォーカスの位置は残す）</summary>
    public void ClearSelection()
    {
        if (_selection.Count == 0) return;
        _selection.Clear();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>セルにチェックがあるか（フォルダは常に false）</summary>
    private bool IsMarked(int cell) => !IsFolder(cell) && _marks.IsMarked(_items[cell - F].FullName);

    /// <summary>現在位置（フォーカス枠）の画像のチェックを付け外し。位置は動かさない（フォルダでは何もしない）</summary>
    public void ToggleFocusedMark()
    {
        int focus = _selection.Focus;
        if (focus < F || focus >= CellCount) return;
        _marks.Toggle(_items[focus - F].FullName);
        OnMarksChanged();
    }

    /// <summary>
    /// ¥ の動き: 画像を 2 枚以上選んでいれば選択中の画像をまとめて付け外し（1 枚でも無しがあれば全部に付け、
    /// 全部付いていれば全部外す）。1 枚以下なら今の位置の画像を付け外し
    /// </summary>
    public void ToggleMarks()
    {
        var selected = SelectedImages;
        if (selected.Count >= 2)
        {
            _marks.ToggleGroup(selected.Select(f => f.FullName).ToList());
            OnMarksChanged();
        }
        else
        {
            ToggleFocusedMark();
        }
    }

    /// <summary>選択中の画像にまとめてチェックを付ける / 外す</summary>
    public void SetMarkOnSelected(bool marked)
    {
        _marks.Set(SelectedImages.Select(f => f.FullName), marked);
        OnMarksChanged();
    }

    public void MarkAll()
    {
        _marks.Set(_items.Select(f => f.FullName), true);
        OnMarksChanged();
    }

    public void InvertMarks()
    {
        _marks.Invert(_items.Select(f => f.FullName));
        OnMarksChanged();
    }

    public void ClearMarks()
    {
        _marks.Clear();
        OnMarksChanged();
    }

    /// <summary>チェックした画像を選択に変える（コマンドは選択中の画像を対象にするので、チェック分を処理する前に使う）</summary>
    public void SelectMarked()
    {
        _selection.Select(Enumerable.Range(F, _items.Count).Where(IsMarked));
        EnsureVisible(_selection.Focus);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMarksChanged()
    {
        Invalidate();
        MarksChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- 配置・スクロール ----

    private GridLayout CurrentLayout => new(CellCount, ClientSize.Width - _scroll.Width,
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
        void Add(int cell)
        {
            if (!IsFolder(cell)) order.Add(_keys[cell - F]);
        }
        for (int i = first; i < first + count; i++) Add(i);
        for (int i = first + count; i < Math.Min(CellCount, first + count + page); i++) Add(i);
        for (int i = first - 1; i >= Math.Max(0, first - page); i--) Add(i);
        _thumbnails.Schedule(order);
    }

    private void OnThumbnailReady(ThumbnailKey key)
    {
        if (!_indexByPath.TryGetValue(key.Path, out int i) || IsFolder(i) || i - F >= _keys.Length || _keys[i - F] != key) return;
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
        if (IsFolder(i)) image = FolderIconBounds(image);
        else if (_thumbnails.TryGet(_keys[i - F], out var bmp) == ThumbnailState.Ready) image = Fit(bmp!.Size, image);
        if (image.Contains(p)) return i;

        var name = NameArea(cell);
        int textWidth = Math.Min(name.Width, TextRenderer.MeasureText(CellName(i), Font).Width);
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
            using var fill = new SolidBrush(Color.FromArgb(40, Theme.Current.SelectionBorder));
            using var border = new Pen(Theme.Current.SelectionBorder);
            g.FillRectangle(fill, band);
            g.DrawRectangle(border, band.X, band.Y, Math.Max(0, band.Width - 1), Math.Max(0, band.Height - 1));
        }

        if (_dropIndex >= 0)
        {
            var marker = _dropMarker;
            marker.Offset(0, -ScrollY);
            using var brush = new SolidBrush(Theme.Current.SelectionBorder);
            g.FillRectangle(brush, marker);
        }
    }

    private void DrawCell(Graphics g, int index, Rectangle cell)
    {
        var p = Theme.Current;
        bool selected = _selection.IsSelected(index) || index == _dropFolder; // ドロップ先のフォルダも選択と同じ色で示す
        var nameColor = selected ? p.SelectionText : p.Text;
        if (selected)
        {
            // 角の丸い地と、フォーカスがあるときは青い枠（ほかの部品を操作中は地の色だけ）
            float radius = LogicalToDeviceUnits(6);
            var r = new RectangleF(cell.X + 0.5f, cell.Y + 0.5f, cell.Width - 1, cell.Height - 1);
            Chrome.Icons.FillRounded(g, r, radius, p.SelectionFill);
            if (Focused)
            {
                var oldMode = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Chrome.Icons.RoundedRect(RectangleF.Inflate(r, -0.75f, -0.75f), radius);
                using var border = new Pen(p.SelectionBorder, 1.5f);
                g.DrawPath(border, path);
                g.SmoothingMode = oldMode;
            }
        }
        if (Focused && index == _selection.Focus)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(cell, -3, -3), p.Text, selected ? p.SelectionFill : p.Background);

        var area = ThumbArea(cell);
        if (IsFolder(index))
        {
            DrawFolderIcon(g, FolderIconBounds(area));
            TextRenderer.DrawText(g, CellName(index), Font, NameArea(cell), nameColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            return;
        }

        switch (_thumbnails.TryGet(_keys[index - F], out var bmp))
        {
            case ThumbnailState.Ready:
                var dest = Fit(bmp!.Size, area);
                g.InterpolationMode = dest.Width < bmp.Width ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(bmp, dest);
                break;
            case ThumbnailState.Failed:
                using (var placeholder = new SolidBrush(p.Placeholder)) g.FillRectangle(placeholder, area);
                TextRenderer.DrawText(g, _items[index - F].Extension.TrimStart('.').ToUpperInvariant() + "\n読めません",
                    Font, area, p.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                break;
            default:
                using (var placeholder = new SolidBrush(p.Placeholder)) g.FillRectangle(placeholder, Rectangle.Inflate(area, -_thumb / 8, -_thumb / 8));
                break;
        }

        if (IsMarked(index)) DrawCheckBadge(g, area);

        TextRenderer.DrawText(g, CellName(index), Font, NameArea(cell), nameColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    /// <summary>フォルダのアイコンの位置（サムネイル枠の中央、枠の 6 割の大きさ）</summary>
    private Rectangle FolderIconBounds(Rectangle area)
    {
        int size = _thumb * 6 / 10;
        return new Rectangle(area.X + (area.Width - size) / 2, area.Y + (area.Height - size) / 2, size, size);
    }

    /// <summary>
    /// フォルダのアイコン。フォルダごとには取らず、普通のフォルダのアイコンを 1 回だけ取って使い回す（軽さ優先）。
    /// 取れなければ簡単なフォルダの形を描く
    /// </summary>
    private void DrawFolderIcon(Graphics g, Rectangle r)
    {
        if (!_folderIconLoaded)
        {
            _folderIconLoaded = true;
            _folderIcon = ShellThumbnail.TryGetIcon(AppContext.BaseDirectory, r.Width);
        }
        if (_folderIcon != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_folderIcon, Fit(_folderIcon.Size, r));
            return;
        }
        using var body = new SolidBrush(Color.FromArgb(240, 196, 80));
        using var tab = new SolidBrush(Color.FromArgb(224, 170, 50));
        g.FillRectangle(tab, r.X, r.Y + r.Height / 6, r.Width * 2 / 5, r.Height / 6);
        g.FillRectangle(body, r.X, r.Y + r.Height / 4, r.Width, r.Height * 5 / 8);
    }

    /// <summary>サムネイル枠の左上に丸いチェックの印（画像の上に重ねても見えるよう白い縁取り付き）</summary>
    private void DrawCheckBadge(Graphics g, Rectangle area)
    {
        int d = LogicalToDeviceUnits(22);
        var r = new Rectangle(area.X, area.Y, d, d);
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(Theme.Current.Check))
            g.FillEllipse(fill, r);
        using (var ring = new Pen(Color.White, Math.Max(1.5f, d / 12f)))
            g.DrawEllipse(ring, r);
        using (var tick = new Pen(Color.White, Math.Max(2f, d / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(tick, new[]
            {
                new PointF(r.X + d * 0.27f, r.Y + d * 0.52f),
                new PointF(r.X + d * 0.44f, r.Y + d * 0.68f),
                new PointF(r.X + d * 0.74f, r.Y + d * 0.34f),
            });
        g.SmoothingMode = oldMode;
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
            else
            {
                if (shift) _selection.ShiftClick(index, keepOthers: ctrl);
                else if (ctrl) _selection.CtrlClick(index);
                else if (_selection.IsSelected(index)) _pendingClick = index; // 複数選択のままドラッグできるよう、単独選択は離したときに
                else _selection.Click(index);
                // Ctrl で選択を外した画像はドラッグの対象にしない
                if (_selection.IsSelected(index))
                {
                    _dragCandidate = true;
                    _dragOrigin = e.Location;
                }
            }
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
        if (_dragCandidate && (e.Button & MouseButtons.Left) != 0)
        {
            var drag = SystemInformation.DragSize;
            if (Math.Abs(e.X - _dragOrigin.X) >= drag.Width || Math.Abs(e.Y - _dragOrigin.Y) >= drag.Height)
                StartDrag();
            return;
        }
        if (!_selection.IsBanding) return;
        _bandMouse = e.Location;
        UpdateBand();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        EndBand();
        _dragCandidate = false;
        if (_pendingClick >= 0)
        {
            // ドラッグせずに離した: 通常のクリックとして、その画像だけを選択
            _selection.Click(_pendingClick);
            _pendingClick = -1;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
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
        if (e.Button == MouseButtons.Left && index >= 0) Activate(index);
    }

    private void Activate(int cell)
    {
        if (IsFolder(cell)) FolderActivated?.Invoke(this, _folders[cell]);
        else ItemActivated?.Invoke(this, cell - F);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) != 0)
        {
            // Ctrl+ホイールでサムネイルの大きさ（エクスプローラーと同じ）
            SetThumbnailSize(ThumbnailSize + Math.Sign(e.Delta) * ThumbnailSizeStep, raiseEvent: true);
            return;
        }
        SetScroll(ScrollY - e.Delta * CurrentLayout.RowHeight / 120);
        if (_selection.IsBanding) UpdateBand();
    }

    // ---- ドラッグ＆ドロップ（並べ替え・フォルダへの移動 / コピー・ほかのアプリへファイルを渡す） ----
    // 画像どうしの間に落とせば並べ替え、フォルダのタイルに落とせばそのフォルダへ移動 / コピー、
    // エクスプローラー等に落とせばそちらで移動 / コピー（どちらもエクスプローラーと同じく、同じドライブなら移動・Ctrl でコピー）

    private const string ReorderFormat = "ImaGeViewer.Reorder";
    private readonly string _dragToken = Guid.NewGuid().ToString("N"); // 自分から出たドラッグかの判定用
    private bool _dragCandidate;
    private Point _dragOrigin;
    private int _pendingClick = -1;
    private int _dropIndex = -1;
    private Rectangle _dropMarker;
    private bool _dragOverSelf;
    private int _dropFolder = -1;   // ドロップ先として強調しているフォルダのタイル
    private bool _droppedInside;    // 自分から出たドラッグをこのグリッドに落とした

    private void StartDrag()
    {
        _dragCandidate = false;
        _pendingClick = -1;
        var paths = SelectedImages.Select(f => f.FullName).ToArray();
        if (paths.Length == 0) return; // フォルダだけを掴んだときは何もしない

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        data.SetData(ReorderFormat, _dragToken);
        _droppedInside = false;
        try
        {
            DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            _dragOverSelf = false;
            SetDropIndex(-1, Rectangle.Empty);
            SetDropFolder(-1);
        }
        // エクスプローラー等が移動した分は一覧から外す。移動したかは戻り値では分からない（移動を自分で行った相手は
        // 「何もしていない」を返す）ので、ファイルが残っているかで確かめる。
        // このグリッドのフォルダ・ツリーへ落とした分は、ドロップを終えてから本体が移動して反映する
        if (!_droppedInside)
        {
            var gone = paths.Where(p => !File.Exists(p)).ToList();
            if (gone.Count > 0) FilesMovedOut?.Invoke(this, gone);
        }
    }

    /// <summary>ドラッグ中のマウスの下にあるフォルダのタイル（無ければ -1）。タイルのどこでも受け付ける</summary>
    private int FolderCellAt(Point client)
    {
        int i = CurrentLayout.IndexAt(client.X, client.Y + ScrollY);
        return i >= 0 && IsFolder(i) ? i : -1;
    }

    private void SetDropFolder(int cell)
    {
        if (cell == _dropFolder) return;
        _dropFolder = cell;
        Invalidate();
    }

    private bool IsOwnDrag(DragEventArgs e) =>
        e.Data?.GetDataPresent(ReorderFormat) == true && e.Data.GetData(ReorderFormat) as string == _dragToken;

    private static string? DroppedFolder(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } p && Directory.Exists(p[0]) ? p[0] : null;

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        OnDragOver(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        var client = PointToClient(new Point(e.X, e.Y));
        bool own = IsOwnDrag(e);

        // 上下の端に近づいたら自動スクロール（DragOver はマウスを止めていても繰り返し来る）
        int edge = Math.Max(_thumb / 3, LogicalToDeviceUnits(24));
        if (client.Y < edge) SetScroll(ScrollY - Math.Max(4, (edge - client.Y) / 2));
        else if (client.Y > ClientSize.Height - edge) SetScroll(ScrollY + Math.Max(4, (client.Y - ClientSize.Height + edge) / 2));

        // フォルダのタイルの上: そのフォルダへ移動 / コピー
        int folderCell = FolderCellAt(client);
        if (folderCell >= 0)
        {
            e.Effect = FileTransfer.ChooseEffect(e, FileTransfer.DraggedPaths(e), _folders[folderCell].FullName);
            SetDropFolder(e.Effect != DragDropEffects.None ? folderCell : -1);
            SetDropIndex(-1, Rectangle.Empty);
            _dragOverSelf = false; // 移動 / コピーのカーソルを出す
            return;
        }
        SetDropFolder(-1);

        if (!own)
        {
            e.Effect = DroppedFolder(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            return;
        }
        _dragOverSelf = true;
        e.Effect = DragDropEffects.Copy;

        var (index, marker) = CurrentLayout.InsertionAt(client.X, client.Y + ScrollY, Math.Max(2, LogicalToDeviceUnits(3)));
        // フォルダのタイルの間には入れられない（並べ替えは画像どうしだけ）
        if (index < F || (index == F && F > 0 && IsBeforeFirstImageRowStart(marker))) SetDropIndex(-1, Rectangle.Empty);
        else SetDropIndex(index, marker);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _dragOverSelf = false;
        SetDropIndex(-1, Rectangle.Empty);
        SetDropFolder(-1);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        bool own = IsOwnDrag(e);
        if (own) _droppedInside = true;
        int folderCell = _dropFolder;
        SetDropFolder(-1);
        if (folderCell >= 0 && folderCell < _folders.Count)
        {
            SetDropIndex(-1, Rectangle.Empty);
            var paths = FileTransfer.DraggedPaths(e);
            string target = _folders[folderCell].FullName;
            var effect = FileTransfer.ChooseEffect(e, paths, target);
            if (effect != DragDropEffects.None)
            {
                // ドラッグ元（エクスプローラー等）を待たせないよう、実際の移動 / コピーはドロップを終えてから
                var drop = new FileDrop(paths.ToList(), target, effect == DragDropEffects.Move);
                BeginInvoke(() => FilesDroppedOnFolder?.Invoke(this, drop));
            }
            // 移動はこちらで行うので、ドラッグ元には「移動した」を返さない（返すとドラッグ元が元のファイルを消すことがある）
            e.Effect = effect == DragDropEffects.Move ? DragDropEffects.None : effect;
            return;
        }
        if (!own)
        {
            if (DroppedFolder(e) is string folder) FolderDropped?.Invoke(this, folder);
            return;
        }
        int insertAt = _dropIndex;
        SetDropIndex(-1, Rectangle.Empty);
        if (insertAt < 0) return;

        var order = ManualOrder.Move(_items.Count, SelectedImageIndices, insertAt - F);
        if (order.Select((from, to) => from == to).All(same => same)) return; // 動いていない
        SetItems(order.Select(i => _items[i]).ToList(), reload: true);
        OrderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>グリッド内へのドラッグ中はコピーの「+」ではなく通常の矢印にする（並べ替えなので）</summary>
    protected override void OnGiveFeedback(GiveFeedbackEventArgs e)
    {
        base.OnGiveFeedback(e);
        if (!_dragOverSelf) return;
        e.UseDefaultCursors = false;
        Cursor.Current = Cursors.Default;
    }

    /// <summary>
    /// 挿入位置が最初の画像の直前でも、縦線が最後のフォルダの右側（同じ行の途中）に出るなら
    /// それは「フォルダの後ろ」なので受け付けない（最初の画像の左に出るときだけ受け付ける）
    /// </summary>
    private bool IsBeforeFirstImageRowStart(Rectangle marker)
    {
        var firstImage = CurrentLayout.CellBounds(F);
        return marker.Top != firstImage.Top || marker.Right > firstImage.Left + _gap;
    }

    private void SetDropIndex(int index, Rectangle marker)
    {
        if (index == _dropIndex && marker == _dropMarker) return;
        _dropIndex = index;
        _dropMarker = marker;
        Invalidate();
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
        if (CellCount == 0) return;
        var layout = CurrentLayout;
        int cols = layout.Columns;
        int pageItems = Math.Max(1, ClientSize.Height / layout.RowHeight) * cols;
        int focus = _selection.Focus;

        // チェック: MarkKey（既定 ¥）はその場で付け外し（Shift 付きは選択中の画像にチェック）、
        // MarkNextKey（既定 ^）は付け外しして次へ。どちらも設定で変えられる
        if (!e.Control && !e.Alt && (e.KeyCode == MarkKey || e.KeyCode == MarkNextKey))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            bool multiple = SelectedImages.Count >= 2;
            int lastSelected = multiple ? _selection.SelectedIndices.Max() : -1;
            if (e.KeyCode == MarkKey)
            {
                if (e.Shift) SetMarkOnSelected(true);
                else ToggleMarks();
                return;
            }
            // ^: 付け外ししてから次へ（複数選択なら、選んだ範囲の次の画像へ）
            ToggleMarks();
            if (multiple) _selection.MoveTo(lastSelected + 1, shift: false, ctrl: false);
            else _selection.Move(1, shift: false, ctrl: false);
            EnsureVisible(_selection.Focus);
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Space when !e.Control && !e.Alt && focus >= F:
                // Quick Look（押したまま / 短く押す の判定は開いた側で行う）
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (!e.Shift) PeekRequested?.Invoke(this, focus - F);
                return;
            case Keys.Left: _selection.Move(-1, e.Shift, e.Control); break;
            case Keys.Right: _selection.Move(1, e.Shift, e.Control); break;
            case Keys.Up: _selection.Move(-cols, e.Shift, e.Control); break;
            case Keys.Down: _selection.Move(cols, e.Shift, e.Control); break;
            case Keys.PageUp: _selection.Move(-pageItems, e.Shift, e.Control); break;
            case Keys.PageDown: _selection.Move(pageItems, e.Shift, e.Control); break;
            case Keys.Home: _selection.MoveTo(0, e.Shift, e.Control); break;
            case Keys.End: _selection.MoveTo(CellCount - 1, e.Shift, e.Control); break;
            case Keys.Space when e.Control && focus >= 0: _selection.CtrlClick(focus); break;
            case Keys.Enter when focus >= 0:
                Activate(focus);
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
            _folderIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
