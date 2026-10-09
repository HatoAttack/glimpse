// 枠・矢印ダイアログ（モザイク・ぼかしダイアログと同じ作り）
// - 画像の上をドラッグで四角の枠か矢印を描く（いくつでも）。矢印は離した所が先端。Shift を押しながらで 45° ごとの向きにそろえる
// - 線の上をクリックで選ぶ。選んだものはドラッグで移動、枠は四隅・矢印は両端で形を変える。Delete か右クリックで消す
// - 色・太さ・角の丸み（枠）・先端の大きさ（矢印）・影は、選んだものがあればそれを変え、無ければ次に描くものの見た目になる
// - 選択した画像を ◀ ▶（PageUp / PageDown）で切り替え。Enter で保存して次へ。描いたものは画像ごとに覚える
// - 「保存」は元の画像を残して別の名前で、「上書き保存」は元の画像を置き換える（確かめない。アニメ・書き出せない形式などは上書きしない）
// - 表示は描いた後の見た目（縮小した画像に同じ処理で描く）
using ImageViewer.Core.Editing;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Thumbnails;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageViewer.App.Theming;

namespace ImageViewer.App.Dialogs;

public sealed class AnnotateDialog : ThemedForm
{
    private const int MinSizePx = 4; // 枠の辺・矢印の長さの最小（画像の px）。これより小さくドラッグしたものは作らない
    private const int HeadScale = 10; // 先端の大きさのスライダーは 0.1 倍刻み

    /// <summary>このアプリを起動している間は前回の設定を引き継ぐ（太さが 0 のうちは、初めの画像の大きさから決める）</summary>
    private static AnnotationKind _lastKind = AnnotationKind.Frame;
    private static Color _lastColor = Color.FromArgb(230, 30, 30);
    private static int _lastThickness, _lastRadius;
    private static int _lastHead = (int)(Annotator.DefaultHeadSize * HeadScale);
    private static bool _lastShadow = true;
    private static bool _lastToCustomFolder;
    private static string _lastFolder = "";

    private readonly IReadOnlyList<string> _paths;
    private int _index = -1;
    private SixLabors.ImageSharp.Image<Rgba32>? _image; // 回転補正済みの原寸（保存元）
    private SixLabors.ImageSharp.Image<Rgba32>? _small; // キャンバスの大きさに縮小した、描く前の画像
    private Bitmap? _display;                           // _small に描いた表示用
    private CancellationTokenSource? _loadCts;
    private bool _busy;
    // 画像を読み込み中（_index はもう次の画像なのに _image はまだ前の画像）。この間は保存しない（前の画像の画素で上書きしないように）
    private bool _loading;

    // 画像 → キャンバスの変換
    private double _scale = 1, _offX, _offY;

    // 描いたもの（画像座標）。後のものほど上（クリックで先に当たる）
    private List<Annotation> _items => _edits.Current;
    private int _selected = -1;
    private readonly PendingEdits<Annotation> _edits = new();
    private enum DragMode { None, Move, Resize }
    private DragMode _mode;
    private bool _creating;                // 新しく描いている途中（小さすぎれば離したときに消す）
    private (double X, double Y) _fixed;   // 形を変えるときに動かない点（枠は対角、矢印は反対の端）
    private bool _dragTip;                 // 矢印: 動かしているのが先端か
    private (double X, double Y) _moveLast;
    private Color _color = _lastColor;
    private bool _syncing;                 // 選んだものの見た目を部品へ写している途中（変更として扱わない）

    private readonly CanvasPanel _canvas = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 32, 32), Cursor = Cursors.Cross };
    private readonly Label _name = new() { AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
    private readonly Button _prev = new() { Text = "◀ 前", AutoSize = true };
    private readonly Button _next = new() { Text = "次 ▶", AutoSize = true };
    private readonly RadioButton _toolFrame = new() { Text = "四角の枠", AutoSize = true };
    private readonly RadioButton _toolArrow = new() { Text = "矢印", AutoSize = true };
    private readonly SwatchPanel _swatch = new() { Width = 40, Height = 24, Margin = new Padding(3, 3, 6, 3), Cursor = Cursors.Hand };
    private readonly Button _pickColor = new() { Text = "色を選ぶ...", AutoSize = true };
    private readonly Label _thicknessText = new() { AutoSize = true };
    private readonly TrackBar _thickness = Slider(1, 100);
    private readonly Label _radiusText = new() { AutoSize = true };
    private readonly TrackBar _radius = Slider(0, 200);
    private readonly Label _headText = new() { AutoSize = true };
    private readonly TrackBar _head = Slider((int)(Annotator.MinHeadSize * HeadScale), (int)(Annotator.MaxHeadSize * HeadScale));
    private readonly CheckBox _shadow = new() { Text = "影を付ける", AutoSize = true };
    private readonly Button _removeSelected = new() { Text = "選んだものを消す (Del)", Width = 200, Height = 26 };
    private readonly Button _removeAll = new() { Text = "すべて消す", Width = 200, Height = 26 };
    private readonly Label _selectionInfo = new() { AutoSize = true, MaximumSize = new Size(210, 0), Margin = new Padding(3, 8, 3, 3) };
    private readonly RadioButton _toSame = new() { Text = "元と同じフォルダ", AutoSize = true };
    private readonly RadioButton _toCustom = new() { Text = "指定のフォルダ", AutoSize = true };
    private readonly TextBox _folder = new() { Width = 190 };
    private readonly Label _outputHint = new() { AutoSize = true, MaximumSize = new Size(190, 0) };
    private readonly Button _save = new() { Text = "保存", Width = 200, Height = 30 };
    private readonly Button _saveOver = new() { Text = "上書き保存", Width = 200, Height = 30 };
    private readonly Button _saveNext = new() { Text = "保存して次へ (Enter)", Width = 200, Height = 30 };
    private readonly Button _saveNextOver = new() { Text = "上書きして次へ (Shift+Enter)", Width = 200, Height = 30 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(210, 0), Margin = new Padding(3, 8, 3, 3) };
    private readonly System.Windows.Forms.Timer _resizeDelay = new() { Interval = 80 };
    private readonly ToolTip _toolTip = new();

    /// <summary>保存したファイルの数（一覧の読み直しの判断用）</summary>
    public int SavedCount { get; private set; }

    private sealed class CanvasPanel : Panel
    {
        public CanvasPanel() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>今の色の見本（テーマの配色に塗り替えられないように自分で描く）</summary>
    private sealed class SwatchPanel : Control
    {
        public Color Swatch { get; set; }

        public SwatchPanel() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        protected override void OnPaint(PaintEventArgs e)
        {
            using var fill = new SolidBrush(Swatch);
            using var border = new Pen(Theme.Current.Border);
            e.Graphics.FillRectangle(fill, ClientRectangle);
            e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }

    private static TrackBar Slider(int min, int max) => new()
    {
        Minimum = min, Maximum = max, TickStyle = TickStyle.None, LargeChange = Math.Max(1, (max - min) / 10), Width = 180, AutoSize = false, Height = 26,
    };

    public AnnotateDialog(IReadOnlyList<string> paths, int startIndex = 0)
    {
        _paths = paths;
        Text = paths.Count == 1 ? "枠・矢印" : $"枠・矢印（{paths.Count} 枚）";
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(1200, screen.Width * 9 / 10), Math.Min(860, screen.Height * 9 / 10));
        MinimumSize = new Size(700, 560);

        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6, 4, 6, 0) };
        _prev.Click += async (_, _) => await StepAsync(-1);
        _next.Click += async (_, _) => await StepAsync(1);
        _prev.Enabled = _next.Enabled = paths.Count > 1;
        top.Controls.AddRange(new Control[] { _prev, _next, _name });

        _canvas.Paint += Canvas_Paint;
        _canvas.MouseDown += Canvas_MouseDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseUp += Canvas_MouseUp;
        _canvas.Resize += (_, _) => { _resizeDelay.Stop(); _resizeDelay.Start(); };
        _resizeDelay.Tick += (_, _) => { _resizeDelay.Stop(); RebuildDisplay(); };

        Controls.Add(_canvas);
        Controls.Add(BuildSidePanel());
        Controls.Add(top);

        _syncing = true;
        (_lastKind == AnnotationKind.Arrow ? _toolArrow : _toolFrame).Checked = true;
        _swatch.Swatch = _color;
        _thickness.Value = Math.Clamp(Math.Max(1, _lastThickness), _thickness.Minimum, _thickness.Maximum);
        _radius.Value = Math.Clamp(_lastRadius, _radius.Minimum, _radius.Maximum);
        _head.Value = Math.Clamp(_lastHead, _head.Minimum, _head.Maximum);
        _shadow.Checked = _lastShadow;
        _syncing = false;
        foreach (var rb in new[] { _toolFrame, _toolArrow })
            rb.CheckedChanged += (_, _) => { if (rb.Checked) UpdateStyleControls(); };
        foreach (var slider in new[] { _thickness, _radius, _head })
            slider.ValueChanged += (_, _) => OnStyleChanged();
        _shadow.CheckedChanged += (_, _) => OnStyleChanged();
        _swatch.Click += (_, _) => PickColor();
        _pickColor.Click += (_, _) => PickColor();
        _folder.Text = _lastFolder;
        (_lastToCustomFolder && _lastFolder.Length > 0 ? _toCustom : _toSame).Checked = true;
        _outputHint.Text = "「保存」は「元の名前_mark」で保存します（同名があれば (2) などを付けます）。" +
                           "「上書き保存」は元のファイルを置き換えます（元には戻せません。HEIC・RAW など書き出せない形式やアニメーションは上書きしません）";
        _outputHint.ForeColor = Theme.Current.TextMuted;
        UpdateStyleControls();
        UpdateButtons();

        Shown += async (_, _) => await ShowIndexAsync(Math.Clamp(startIndex, 0, paths.Count - 1));
        FormClosed += (_, _) =>
        {
            _loadCts?.Cancel();
            _image?.Dispose();
            _small?.Dispose();
            _display?.Dispose();
            _toolTip.Dispose();
            _lastKind = CurrentTool;
            _lastColor = _color;
            if (_styleReady)
            {
                _lastThickness = _thickness.Value;
                _lastRadius = _radius.Value;
            }
            _lastHead = _head.Value;
            _lastShadow = _shadow.Checked;
            _lastFolder = _folder.Text.Trim();
            _lastToCustomFolder = _toCustom.Checked;
        };
    }

    // 太さ・角の丸みの初めの値を決めたか（前回の設定が無ければ、初めの画像を読んでから大きさに合わせて決める）
    private bool _styleReady = _lastThickness > 0;

    /// <summary>これから描くもの（描いたものの種類は変えない）</summary>
    private AnnotationKind CurrentTool => _toolArrow.Checked ? AnnotationKind.Arrow : AnnotationKind.Frame;

    private Control BuildSidePanel()
    {
        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Width = 250, Padding = new Padding(6, 4, 6, 4), // 縦のスクロールバーが出ても横にはみ出さない幅
        };

        var toolStack = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        toolStack.Controls.AddRange(new Control[] { _toolFrame, _toolArrow });
        var toolBox = new GroupBox { Text = "描くもの", AutoSize = true, Width = 210, Padding = new Padding(8) };
        toolBox.Controls.Add(toolStack);
        side.Controls.Add(toolBox);
        _toolTip.SetToolTip(_toolArrow, "指したい所へ向かってドラッグします（離した所が先端）。\nShift を押しながらドラッグすると、向きを 45° ごとにそろえます");

        var colorRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        colorRow.Controls.AddRange(new Control[] { _swatch, _pickColor });
        var styleStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        styleStack.Controls.AddRange(new Control[] { colorRow, _thicknessText, _thickness, _radiusText, _radius, _headText, _head, _shadow });
        var styleBox = new GroupBox { Text = "見た目", AutoSize = true, Width = 210, Padding = new Padding(8) };
        styleBox.Controls.Add(styleStack);
        side.Controls.Add(styleBox);
        _toolTip.SetToolTip(_radius, "四角の枠の角の丸み。枠の短い辺の半分より大きくはなりません");
        _toolTip.SetToolTip(_head, "矢印の先端の大きさ（線の太さの何倍か）");

        _removeSelected.Click += (_, _) => RemoveSelected();
        _removeAll.Click += (_, _) => RemoveAll();
        side.Controls.AddRange(new Control[] { _selectionInfo, _removeSelected, _removeAll });

        var browse = new Button { Text = "参照...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "保存先のフォルダ", InitialDirectory = _folder.Text };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _folder.Text = dlg.SelectedPath;
            _toCustom.Checked = true;
        };
        _folder.Enter += (_, _) => _toCustom.Checked = true;
        var outStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        outStack.Controls.AddRange(new Control[] { _toSame, _toCustom, _folder, browse, _outputHint });
        var outBox = new GroupBox { Text = "保存先", AutoSize = true, Width = 210, Padding = new Padding(8), Margin = new Padding(3, 8, 3, 3) };
        outBox.Controls.Add(outStack);
        side.Controls.Add(outBox);

        _save.Click += async (_, _) => await SaveCurrentAsync(advance: false, overwrite: false);
        _saveOver.Click += async (_, _) => await SaveCurrentAsync(advance: false, overwrite: true);
        _saveNext.Click += async (_, _) => await SaveCurrentAsync(advance: true, overwrite: false);
        _saveNextOver.Click += async (_, _) => await SaveCurrentAsync(advance: true, overwrite: true);
        side.Controls.AddRange(new Control[] { _save, _saveOver, _saveNext, _saveNextOver, _status });
        return side;
    }

    // ---- 見た目（色・太さ・角の丸み・先端の大きさ・影） ----

    private static Rgba32 ToRgba(Color c) => new(c.R, c.G, c.B, c.A);

    /// <summary>部品の今の値で、枠か矢印を作る</summary>
    private Annotation NewItem(AnnotationKind kind, double x0, double y0, double x1, double y1) =>
        new(kind, x0, y0, x1, y1, ToRgba(_color), _thickness.Value, _radius.Value, (double)_head.Value / HeadScale, _shadow.Checked);

    private void PickColor()
    {
        using var dlg = new ColorDialog { Color = _color, FullOpen = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _color = Color.FromArgb(255, dlg.Color);
        _swatch.Swatch = _color;
        _swatch.Invalidate();
        OnStyleChanged();
    }

    /// <summary>見た目の部品を変えた: 選んだものがあればそれに反映する（無ければ次に描くものから使う）</summary>
    private void OnStyleChanged()
    {
        UpdateStyleControls();
        if (_syncing || _busy || _selected < 0 || _selected >= _items.Count) return;
        var a = _items[_selected];
        _items[_selected] = NewItem(a.Kind, a.X0, a.Y0, a.X1, a.Y1);
        RenderPreview();
    }

    /// <summary>選んだものの見た目を部品へ写す（続けて調整できるように）</summary>
    private void LoadStyleFrom(Annotation a)
    {
        _syncing = true;
        _color = Color.FromArgb(a.Color.A, a.Color.R, a.Color.G, a.Color.B);
        _swatch.Swatch = _color;
        _swatch.Invalidate();
        _thickness.Value = Math.Clamp((int)Math.Round(a.Thickness), _thickness.Minimum, _thickness.Maximum);
        // 使わない値（枠の先端の大きさ・矢印の角の丸み）は、次に描くもののために今の部品の値を残す
        if (a.Kind == AnnotationKind.Frame) _radius.Value = Math.Clamp((int)Math.Round(a.CornerRadius), _radius.Minimum, _radius.Maximum);
        else _head.Value = Math.Clamp((int)Math.Round(a.HeadSize * HeadScale), _head.Minimum, _head.Maximum);
        _shadow.Checked = a.Shadow;
        _syncing = false;
        UpdateStyleControls();
    }

    private void UpdateStyleControls()
    {
        // 選んだものがあればその種類、無ければこれから描くものの種類で使う部品を決める
        var kind = _selected >= 0 && _selected < _items.Count ? _items[_selected].Kind : CurrentTool;
        _radius.Enabled = _radiusText.Enabled = kind == AnnotationKind.Frame;
        _head.Enabled = _headText.Enabled = kind == AnnotationKind.Arrow;
        _thicknessText.Text = $"太さ: {_thickness.Value} px";
        _radiusText.Text = _radius.Value == 0 ? "角の丸み: なし" : $"角の丸み: {_radius.Value} px";
        _headText.Text = $"先端の大きさ: 太さの {(double)_head.Value / HeadScale:0.0} 倍";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Enter when !_busy && !(ActiveControl is TextBox or NumericUpDown):
                _ = SaveCurrentAsync(advance: true, overwrite: false);
                return true;
            case Keys.Shift | Keys.Enter when !_busy && !(ActiveControl is TextBox or NumericUpDown):
                _ = SaveCurrentAsync(advance: true, overwrite: true);
                return true;
            case Keys.Delete or Keys.Back when !(ActiveControl is TextBox):
                RemoveSelected();
                return true;
            case Keys.PageDown:
                _ = StepAsync(1);
                return true;
            case Keys.PageUp:
                _ = StepAsync(-1);
                return true;
            case Keys.Escape:
                Close();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- 画像の切り替え ----

    private async Task StepAsync(int delta)
    {
        if (_paths.Count < 2 || _busy) return;
        await ShowIndexAsync(((_index + delta) % _paths.Count + _paths.Count) % _paths.Count);
    }

    /// <param name="keepItems">false なら描いたものを空にする（上書きして読み直すとき。同じものを二重に描かないように）</param>
    private async Task ShowIndexAsync(int index, bool keepItems = true)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        if (_index >= 0 && _image != null && !_loading) _edits.Remember(_index);
        _index = index;
        _loading = true;
        _mode = DragMode.None;
        UpdateButtons();
        string path = _paths[index];
        _name.Text = $"[{index + 1}/{_paths.Count}] {Path.GetFileName(path)}  （読み込み中…）";
        SixLabors.ImageSharp.Image<Rgba32> image;
        try
        {
            image = await Task.Run(() => ImageLoader.Load(path), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (cts.IsCancellationRequested) return;
            _loading = false;
            _image?.Dispose();
            _image = null;
            _edits.Set(new());
            _selected = -1;
            RebuildDisplay();
            _name.Text = $"[{index + 1}/{_paths.Count}] {Path.GetFileName(path)}  （読み込めません: {ex.Message}）";
            UpdateButtons();
            return;
        }
        if (cts.IsCancellationRequested)
        {
            image.Dispose();
            return;
        }
        _loading = false;
        _image?.Dispose();
        _image = image;
        _selected = -1;
        _name.Text = $"[{index + 1}/{_paths.Count}] {Path.GetFileName(path)}  （{image.Width} × {image.Height}）";
        if (keepItems) _edits.Recall(index);
        else _edits.Discard(index);
        if (!_styleReady)
        {
            // 前回の設定が無いので、初めの画像の大きさに合った太さ・角の丸みから始める
            _styleReady = true;
            _syncing = true;
            int thickness = Math.Clamp(Annotator.DefaultThickness(image.Width, image.Height), _thickness.Minimum, _thickness.Maximum);
            _thickness.Value = thickness;
            _radius.Value = Math.Clamp(thickness * 2, _radius.Minimum, _radius.Maximum);
            _syncing = false;
        }
        UpdateStyleControls();
        RebuildDisplay();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool ready = !_busy && !_loading && _image != null;
        _save.Enabled = _saveOver.Enabled = _saveNext.Enabled = _saveNextOver.Enabled = ready && _items.Count > 0;
        _saveNext.Visible = _saveNextOver.Visible = _paths.Count > 1;
        _removeSelected.Enabled = !_busy && _selected >= 0;
        _removeAll.Enabled = !_busy && _items.Count > 0;
        _prev.Enabled = _next.Enabled = !_busy && _paths.Count > 1;
        _selectionInfo.Text = _image == null ? ""
            : _items.Count == 0 ? "画像の上をドラッグして、枠や矢印を描いてください（いくつでも描けます）"
            : _selected < 0 ? $"枠・矢印 {_items.Count} 個（線の上をクリックすると選べます）"
            : _items[_selected] is { Kind: AnnotationKind.Frame } f
                ? $"枠・矢印 {_items.Count} 個（選んだ枠: {Math.Round(f.Width)} × {Math.Round(f.Height)} px）"
                : $"枠・矢印 {_items.Count} 個（選んだ矢印: 長さ {Math.Round(_items[_selected].Length)} px）";
    }

    /// <summary>描いたものを変えた後: 表示とボタンを合わせる</summary>
    private void OnItemsChanged()
    {
        UpdateStyleControls();
        RenderPreview();
        UpdateButtons();
    }

    private void RemoveSelected()
    {
        if (_busy || _selected < 0 || _selected >= _items.Count) return;
        _items.RemoveAt(_selected);
        _selected = -1;
        OnItemsChanged();
    }

    private void RemoveAll()
    {
        if (_busy || _items.Count == 0) return;
        _items.Clear();
        _selected = -1;
        OnItemsChanged();
    }

    // ---- 座標の変換 ----

    private (double X, double Y) ImageToCanvas(double ix, double iy) => (_offX + ix * _scale, _offY + iy * _scale);
    private (double X, double Y) CanvasToImage(double cx, double cy) => ((cx - _offX) / _scale, (cy - _offY) / _scale);
    private (double X, double Y) ClampToImage((double X, double Y) p) =>
        (Math.Clamp(p.X, 0, _image!.Width), Math.Clamp(p.Y, 0, _image.Height));
    private int HandleRadius => Math.Max(7, 7 * DeviceDpi / 96);

    // ---- 描画（重い縮小は RebuildDisplay だけ。描いたものを変えたときは縮小済みの画像に描き直すだけ） ----

    private void RebuildDisplay()
    {
        _small?.Dispose();
        _small = null;
        if (_image != null)
        {
            int cw = Math.Max(1, _canvas.ClientSize.Width), ch = Math.Max(1, _canvas.ClientSize.Height);
            _scale = Math.Min((double)cw / _image.Width, (double)ch / _image.Height);
            int dw = Math.Max(1, (int)Math.Round(_image.Width * _scale)), dh = Math.Max(1, (int)Math.Round(_image.Height * _scale));
            _offX = (cw - dw) / 2.0;
            _offY = (ch - dh) / 2.0;
            _small = dw == _image.Width && dh == _image.Height
                ? _image.Clone()
                : _image.Clone(x => x.Resize(dw, dh, KnownResamplers.Triangle));
        }
        RenderPreview();
    }

    /// <summary>縮小した画像に、今の枠・矢印を表示の倍率に合わせて描いて、表示用のビットマップを作り直す</summary>
    private void RenderPreview()
    {
        _display?.Dispose();
        _display = null;
        if (_small != null && _image != null)
        {
            if (_items.Count == 0)
            {
                _display = ThumbnailGenerator.ToPArgbBitmap(_small);
            }
            else
            {
                using var preview = _small.Clone();
                Annotator.Draw(preview, _items.Select(a => a.Scale(_scale)));
                _display = ThumbnailGenerator.ToPArgbBitmap(preview);
            }
        }
        _canvas.Invalidate();
    }

    /// <summary>選んだものの、つかんで形を変える点（枠は四隅、矢印は始点と先端）</summary>
    private static (double X, double Y)[] HandlesOf(Annotation a) => a.Kind == AnnotationKind.Frame
        ? new[] { (a.X0, a.Y0), (a.X1, a.Y0), (a.X1, a.Y1), (a.X0, a.Y1) }
        : new[] { (a.X0, a.Y0), (a.X1, a.Y1) };

    private void Canvas_Paint(object? sender, PaintEventArgs e)
    {
        if (_display == null) return;
        var g = e.Graphics;
        g.DrawImageUnscaled(_display, (int)Math.Round(_offX), (int)Math.Round(_offY));
        if (_selected < 0 || _selected >= _items.Count) return;

        // 選んだものだけ、つかむ点を出す
        using var fill = new SolidBrush(Color.White);
        using var border = new Pen(Color.FromArgb(0, 120, 215), 1);
        int hs = HandleRadius - 3;
        foreach (var (hx, hy) in HandlesOf(_items[_selected]))
        {
            var (cx, cy) = ImageToCanvas(hx, hy);
            g.FillRectangle(fill, (float)cx - hs, (float)cy - hs, hs * 2, hs * 2);
            g.DrawRectangle(border, (float)cx - hs, (float)cy - hs, hs * 2, hs * 2);
        }
    }

    // ---- マウス ----

    /// <summary>点が線の上（少し外れていてもよい）にあるもの（上にあるものから。無ければ -1）</summary>
    private int HitItem(double ix, double iy)
    {
        double tolerance = HandleRadius / _scale;
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Distance(ix, iy) <= tolerance) return i;
        return -1;
    }

    private void Canvas_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_image == null || _busy || _loading) return;
        _canvas.Focus();
        var (ix, iy) = CanvasToImage(e.X, e.Y);

        // 右クリック: それを消す
        if (e.Button == MouseButtons.Right)
        {
            int hit = HitItem(ix, iy);
            if (hit < 0) return;
            _items.RemoveAt(hit);
            _selected = -1;
            OnItemsChanged();
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        // 選んだもののつかむ点なら形を変える
        if (_selected >= 0)
        {
            var a = _items[_selected];
            var handles = HandlesOf(a);
            int hr = HandleRadius;
            for (int i = 0; i < handles.Length; i++)
            {
                var (cx, cy) = ImageToCanvas(handles[i].X, handles[i].Y);
                if (Math.Abs(e.X - cx) > hr || Math.Abs(e.Y - cy) > hr) continue;
                _mode = DragMode.Resize;
                _creating = false;
                _fixed = handles[(i + handles.Length / 2) % handles.Length]; // 枠は対角、矢印は反対の端を固定
                _dragTip = i == 1;
                return;
            }
        }

        // 線の上なら選んで移動、何も無い所なら新しく描く
        int target = HitItem(ix, iy);
        if (target >= 0)
        {
            _selected = target;
            _mode = DragMode.Move;
            _moveLast = (ix, iy);
            LoadStyleFrom(_items[target]);
        }
        else
        {
            _fixed = ClampToImage((ix, iy));
            _items.Add(NewItem(CurrentTool, _fixed.X, _fixed.Y, _fixed.X, _fixed.Y));
            _selected = _items.Count - 1;
            _mode = DragMode.Resize;
            _creating = true;
            _dragTip = true;
        }
        UpdateStyleControls();
        UpdateButtons();
        _canvas.Invalidate();
    }

    private void Canvas_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_mode == DragMode.None || _image == null || _selected < 0) return;
        if (_mode == DragMode.Move) DoMove(e);
        else DoResize(e);
        RenderPreview();
        UpdateButtons();
        // マウスの移動が続いている間は描き直しが後回しになる（移動のほうが先に処理される）ので、ここで描き直しまで済ませる。
        // こうしないと、描くものが増えて 1 回の処理が長くなるほど、こまが飛んでちらついて見える
        _canvas.Update();
    }

    private void Canvas_MouseUp(object? sender, MouseEventArgs e)
    {
        if (_mode == DragMode.None) return;
        _mode = DragMode.None;
        // 小さすぎるもの（クリックしただけ など）は作らない。クリックで選択を外すのと同じになる
        if (_creating && _selected >= 0 && TooSmall(_items[_selected]))
        {
            _items.RemoveAt(_selected);
            _selected = -1;
        }
        _creating = false;
        OnItemsChanged();
    }

    private static bool TooSmall(Annotation a) =>
        a.Kind == AnnotationKind.Frame ? a.Width < MinSizePx || a.Height < MinSizePx : a.Length < MinSizePx;

    private void DoMove(MouseEventArgs e)
    {
        var (ix, iy) = CanvasToImage(e.X, e.Y);
        var a = _items[_selected];
        // 画像の外へ出ない範囲で動かす
        double dx = Math.Clamp(ix - _moveLast.X, -Math.Min(a.X0, a.X1), _image!.Width - Math.Max(a.X0, a.X1));
        double dy = Math.Clamp(iy - _moveLast.Y, -Math.Min(a.Y0, a.Y1), _image.Height - Math.Max(a.Y0, a.Y1));
        _items[_selected] = a.Offset(dx, dy);
        _moveLast = (_moveLast.X + dx, _moveLast.Y + dy);
    }

    private void DoResize(MouseEventArgs e)
    {
        var (fx, fy) = _fixed;
        var a = _items[_selected];
        var (mx, my) = CanvasToImage(e.X, e.Y);
        if (a.Kind == AnnotationKind.Frame)
        {
            (mx, my) = ClampToImage((mx, my));
            double x0 = Math.Min(fx, mx), x1 = Math.Max(fx, mx), y0 = Math.Min(fy, my), y1 = Math.Max(fy, my);
            // 描いている途中は小さくてもよい（離したときに確かめる）。描いた枠を小さくしすぎることはできない
            if (_creating || (x1 - x0 >= MinSizePx && y1 - y0 >= MinSizePx)) _items[_selected] = a with { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 };
            return;
        }

        // 矢印: Shift を押している間は、向きを 45° ごとにそろえる（長さはそのまま）
        if ((ModifierKeys & Keys.Shift) != 0)
        {
            double length = Math.Sqrt((mx - fx) * (mx - fx) + (my - fy) * (my - fy));
            double angle = Math.Round(Math.Atan2(my - fy, mx - fx) / (Math.PI / 4)) * (Math.PI / 4);
            (mx, my) = (fx + Math.Cos(angle) * length, fy + Math.Sin(angle) * length);
        }
        (mx, my) = ClampToImage((mx, my));
        if (!_creating && Math.Sqrt((mx - fx) * (mx - fx) + (my - fy) * (my - fy)) < MinSizePx) return;
        _items[_selected] = _dragTip ? a with { X0 = fx, Y0 = fy, X1 = mx, Y1 = my } : a with { X0 = mx, Y0 = my, X1 = fx, Y1 = fy };
    }

    // ---- 保存 ----

    /// <summary>保存先のフォルダ（指定のフォルダが正しくなければメッセージを出して null）</summary>
    private string? OutputFolderFor(string source, bool overwrite)
    {
        if (_toSame.Checked || overwrite) return Path.GetDirectoryName(source)!;
        string folder = _folder.Text.Trim();
        if (folder.Length > 0 && Path.IsPathFullyQualified(folder)) return folder;
        MessageBox.Show(this, "保存先のフォルダを C:\\… の形で指定してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return null;
    }

    /// <param name="overwrite">true なら元の画像を置き換える（確かめない）</param>
    private async Task SaveCurrentAsync(bool advance, bool overwrite)
    {
        if (_busy || _loading || _image == null) return;
        string src = _paths[_index];
        var items = _items.ToList();
        if (items.Count == 0)
        {
            _status.ForeColor = Theme.Current.TextMuted;
            _status.Text = "枠か矢印を描いてから保存してください";
            return;
        }
        if (OutputFolderFor(src, overwrite) is not string folder) return;

        var image = _image;
        SetBusy(true);
        try
        {
            string dst = await Task.Run(() => Annotator.SaveAnnotated(image, items, src, overwrite ? src : Annotator.OutputPathFor(src, folder)));
            SavedCount++;
            _status.ForeColor = Theme.Current.Text;
            _status.Text = overwrite ? $"上書きしました: {Path.GetFileName(dst)}" : $"保存しました: {Path.GetFileName(dst)}";
            if (overwrite)
            {
                // 元の画像にはもう描いてあるので、覚えているものは捨てる（戻ってきたときに二重に描かないように）
                _edits.Discard(_index);
                _selected = -1;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.ForeColor = Theme.Current.Danger;
            _status.Text = $"保存できませんでした: {ex.Message}";
            return;
        }
        finally
        {
            SetBusy(false);
        }
        if (advance)
        {
            if (_index + 1 < _paths.Count) await ShowIndexAsync(_index + 1);
            else Close(); // 最後の 1 枚を保存したら閉じる
        }
        else if (overwrite)
        {
            await ShowIndexAsync(_index, keepItems: false); // 描いた後の画像を出し直す（同じものを二重に描かないよう空に）
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        UpdateButtons();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy) e.Cancel = true; // 保存中は閉じない（元の画像を使っている）
        base.OnFormClosing(e);
    }
}
