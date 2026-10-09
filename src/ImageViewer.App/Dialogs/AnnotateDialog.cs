// 枠・矢印ダイアログ（モザイク・ぼかしダイアログと同じ作り）
// - 画像の上をドラッグで四角の枠か矢印を描く（いくつでも）。矢印は離した所が先端。Shift を押しながらで 45° ごとの向きにそろえる
// - 線の上をクリックで選ぶ。選んだものはドラッグで移動、枠は四隅・矢印は両端で形を変える。Delete か右クリックで消す
// - 色・太さ・角の丸み（枠）・先端の大きさ（矢印）・影は、選んだものがあればそれを変え、無ければ次に描くものの見た目になる
// - 選択した画像を ◀ ▶（PageUp / PageDown）で切り替え。Enter で保存して次へ。描いたものは画像ごとに覚える
// - 「保存」は元の画像を残して別の名前で、「上書き保存」は元の画像を置き換える（確かめない。アニメ・書き出せない形式などは上書きしない）
// - 表示は描いた後の見た目（縮小した画像に同じ処理で描く）
using ImageViewer.Core.Editing;
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

    private readonly ImageStepper _stepper;  // 画像送りと、表示中の画像（原寸。保存元）
    private readonly EditSavePanel _saver;   // 保存先・保存のボタン・保存中の制御
    private readonly EditCanvas _canvas = new();
    private SixLabors.ImageSharp.Image<Rgba32>? _small; // キャンバスの大きさに縮小した、描く前の画像
    private Bitmap? _display;                           // _small に描いた表示用

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
    private readonly ToolTip _toolTip = new();

    /// <summary>保存したファイルの数（一覧の読み直しの判断用）</summary>
    public int SavedCount => _saver.SavedCount;

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
        _stepper = new ImageStepper(paths);
        _stepper.StepRequested += async delta => await StepAsync(delta);
        _saver = new EditSavePanel(this, paths.Count,
            "「保存」は「元の名前_mark」で保存します（同名があれば (2) などを付けます）。" +
            "「上書き保存」は元のファイルを置き換えます（元には戻せません。HEIC・RAW など書き出せない形式やアニメーションは上書きしません）",
            _lastFolder, _lastToCustomFolder, withSaveAll: false);
        _saver.SaveRequested += async (advance, overwrite) => await SaveCurrentAsync(advance, overwrite);
        _saver.BusyChanged += UpdateButtons;

        _canvas.Paint += Canvas_Paint;
        _canvas.MouseDown += Canvas_MouseDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseUp += Canvas_MouseUp;
        _canvas.Settled += (_, _) => RebuildDisplay();
        EditDialogShell.Setup(this, "枠・矢印", _stepper, _canvas, BuildSidePanel(), new Size(700, 560));

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
        UpdateStyleControls();
        UpdateButtons();

        Shown += async (_, _) => await ShowIndexAsync(Math.Clamp(startIndex, 0, paths.Count - 1));
        FormClosed += (_, _) =>
        {
            _stepper.Dispose();
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
            _lastFolder = _saver.FolderText;
            _lastToCustomFolder = _saver.ToCustom;
        };
    }

    // 太さ・角の丸みの初めの値を決めたか（前回の設定が無ければ、初めの画像を読んでから大きさに合わせて決める）
    private bool _styleReady = _lastThickness > 0;

    /// <summary>これから描くもの（描いたものの種類は変えない）</summary>
    private AnnotationKind CurrentTool => _toolArrow.Checked ? AnnotationKind.Arrow : AnnotationKind.Frame;

    private Control BuildSidePanel()
    {
        var side = EditDialogShell.SidePanel(250); // 縦のスクロールバーが出ても横にはみ出さない幅
        side.Controls.Add(EditDialogShell.Group("描くもの", vertical: false, _toolFrame, _toolArrow));
        _toolTip.SetToolTip(_toolArrow, "指したい所へ向かってドラッグします（離した所が先端）。\nShift を押しながらドラッグすると、向きを 45° ごとにそろえます");

        var colorRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        colorRow.Controls.AddRange(new Control[] { _swatch, _pickColor });
        side.Controls.Add(EditDialogShell.Group("見た目", vertical: true, colorRow, _thicknessText, _thickness, _radiusText, _radius, _headText, _head, _shadow));
        _toolTip.SetToolTip(_radius, "四角の枠の角の丸み。枠の短い辺の半分より大きくはなりません");
        _toolTip.SetToolTip(_head, "矢印の先端の大きさ（線の太さの何倍か）");

        _removeSelected.Click += (_, _) => RemoveSelected();
        _removeAll.Click += (_, _) => RemoveAll();
        side.Controls.AddRange(new Control[] { _selectionInfo, _removeSelected, _removeAll });

        side.Controls.Add(_saver.FolderBox);
        side.Controls.AddRange(_saver.Buttons);
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
        if (_syncing || _saver.Busy || _selected < 0 || _selected >= _items.Count) return;
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
            case Keys.Enter when !_saver.Busy && !(ActiveControl is TextBox or NumericUpDown):
                _ = SaveCurrentAsync(advance: true, overwrite: false);
                return true;
            case Keys.Shift | Keys.Enter when !_saver.Busy && !(ActiveControl is TextBox or NumericUpDown):
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
        if (_stepper.Count < 2 || _saver.Busy) return;
        await ShowIndexAsync(_stepper.IndexAt(delta));
    }

    /// <param name="keepItems">false なら描いたものを空にする（上書きして読み直すとき。同じものを二重に描かないように）</param>
    private async Task ShowIndexAsync(int index, bool keepItems = true)
    {
        if (_stepper.Ready) _edits.Remember(_stepper.Index);
        _mode = DragMode.None;
        var outcome = await _stepper.ShowAsync(index, UpdateButtons);
        if (outcome == ImageStepper.Outcome.Superseded) return;
        _selected = -1;
        if (outcome == ImageStepper.Outcome.Failed)
        {
            _edits.Set(new());
            RebuildDisplay();
            UpdateButtons();
            return;
        }
        var image = _stepper.Image!;
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
        _saver.SaveEnabled = !_saver.Busy && _stepper.Ready && _items.Count > 0;
        _removeSelected.Enabled = !_saver.Busy && _selected >= 0;
        _removeAll.Enabled = !_saver.Busy && _items.Count > 0;
        _stepper.StepEnabled = !_saver.Busy;
        _selectionInfo.Text = _stepper.Image == null ? ""
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
        if (_saver.Busy || _selected < 0 || _selected >= _items.Count) return;
        _items.RemoveAt(_selected);
        _selected = -1;
        OnItemsChanged();
    }

    private void RemoveAll()
    {
        if (_saver.Busy || _items.Count == 0) return;
        _items.Clear();
        _selected = -1;
        OnItemsChanged();
    }

    // ---- 描画（重い縮小は RebuildDisplay だけ。描いたものを変えたときは縮小済みの画像に描き直すだけ） ----

    private void RebuildDisplay()
    {
        _small?.Dispose();
        _small = null;
        if (_stepper.Image != null)
        {
            var size = _canvas.FitImage(_stepper.Image.Width, _stepper.Image.Height);
            _small = size.Width == _stepper.Image.Width && size.Height == _stepper.Image.Height
                ? _stepper.Image.Clone()
                : _stepper.Image.Clone(x => x.Resize(size.Width, size.Height, KnownResamplers.Triangle));
        }
        RenderPreview();
    }

    /// <summary>縮小した画像に、今の枠・矢印を表示の倍率に合わせて描いて、表示用のビットマップを作り直す</summary>
    private void RenderPreview()
    {
        _display?.Dispose();
        _display = null;
        if (_small != null && _stepper.Image != null)
        {
            if (_items.Count == 0)
            {
                _display = ThumbnailGenerator.ToPArgbBitmap(_small);
            }
            else
            {
                using var preview = _small.Clone();
                Annotator.Draw(preview, _items.Select(a => a.Scale(_canvas.Zoom)));
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
        _canvas.DrawDisplay(g, _display);
        // 選んだものだけ、つかむ点を出す
        if (_selected >= 0 && _selected < _items.Count) _canvas.DrawHandles(g, HandlesOf(_items[_selected]));
    }

    // ---- マウス ----

    /// <summary>点が線の上（少し外れていてもよい）にあるもの（上にあるものから。無ければ -1）</summary>
    private int HitItem(double ix, double iy)
    {
        double tolerance = _canvas.HandleRadius / _canvas.Zoom;
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Distance(ix, iy) <= tolerance) return i;
        return -1;
    }

    private void Canvas_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_stepper.Image == null || _saver.Busy || _stepper.Loading) return;
        _canvas.Focus();
        var (ix, iy) = _canvas.CanvasToImage(e.X, e.Y);

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
            for (int i = 0; i < handles.Length; i++)
            {
                if (!_canvas.HitsHandle(e.X, e.Y, handles[i])) continue;
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
            _fixed = _canvas.ClampToImage((ix, iy));
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
        if (_mode == DragMode.None || _stepper.Image == null || _selected < 0) return;
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
        var (ix, iy) = _canvas.CanvasToImage(e.X, e.Y);
        var a = _items[_selected];
        // 画像の外へ出ない範囲で動かす
        double dx = Math.Clamp(ix - _moveLast.X, -Math.Min(a.X0, a.X1), _stepper.Image!.Width - Math.Max(a.X0, a.X1));
        double dy = Math.Clamp(iy - _moveLast.Y, -Math.Min(a.Y0, a.Y1), _stepper.Image.Height - Math.Max(a.Y0, a.Y1));
        _items[_selected] = a.Offset(dx, dy);
        _moveLast = (_moveLast.X + dx, _moveLast.Y + dy);
    }

    private void DoResize(MouseEventArgs e)
    {
        var (fx, fy) = _fixed;
        var a = _items[_selected];
        var (mx, my) = _canvas.CanvasToImage(e.X, e.Y);
        if (a.Kind == AnnotationKind.Frame)
        {
            (mx, my) = _canvas.ClampToImage((mx, my));
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
        (mx, my) = _canvas.ClampToImage((mx, my));
        if (!_creating && Math.Sqrt((mx - fx) * (mx - fx) + (my - fy) * (my - fy)) < MinSizePx) return;
        _items[_selected] = _dragTip ? a with { X0 = fx, Y0 = fy, X1 = mx, Y1 = my } : a with { X0 = mx, Y0 = my, X1 = fx, Y1 = fy };
    }

    // ---- 保存 ----

    /// <param name="overwrite">true なら元の画像を置き換える（確かめない）</param>
    private async Task SaveCurrentAsync(bool advance, bool overwrite)
    {
        if (_saver.Busy || !_stepper.Ready) return;
        string src = _stepper.CurrentPath;
        var items = _items.ToList();
        if (items.Count == 0)
        {
            _saver.ShowNotice("枠か矢印を描いてから保存してください");
            return;
        }
        if (_saver.FolderFor(src, overwrite) is not string folder) return;

        var image = _stepper.Image!;
        int index = _stepper.Index;
        bool saved = await _saver.SaveOneAsync(
            () => Annotator.SaveAnnotated(image, items, src, overwrite ? src : Annotator.OutputPathFor(src, folder)),
            overwrite, saved: () =>
            {
                if (!overwrite) return;
                // 元の画像にはもう描いてあるので、覚えているものは捨てる（戻ってきたときに二重に描かないように）
                _edits.Discard(index);
                _selected = -1;
            });
        if (!saved) return;
        if (advance)
        {
            if (index + 1 < _stepper.Count) await ShowIndexAsync(index + 1);
            else Close(); // 最後の 1 枚を保存したら閉じる
        }
        else if (overwrite)
        {
            await ShowIndexAsync(index, keepItems: false); // 描いた後の画像を出し直す（同じものを二重に描かないよう空に）
        }
    }
}
