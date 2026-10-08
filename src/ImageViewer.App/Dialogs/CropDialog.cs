// 切り抜きダイアログ（image-sizechange の切り抜きタブから移植）
// - 画像の上をドラッグで範囲を選ぶ。枠の中をドラッグで移動、四隅で大きさを変える
// - アスペクト比（自由 / 1:1 / 4:3 / 3:2 / 16:9 / 指定）と縦横の入れ替え
// - 選択した画像を ◀ ▶（PageUp / PageDown）で切り替え。Enter で保存して次へ
// - 「次の画像も同じ位置で切り抜く」なら、切り替えても枠を引き継ぐ（大きさが違う画像には割合で合わせる）。
//   一括も「全部を今の範囲で切り抜き」になる（スクリーンショットのように構成が同じ画像向け）
// - 「保存」は元の画像を残して別の名前で、「上書き保存」は元の画像を置き換える（確かめない。アニメ・書き出せない形式などは上書きしない）
// - 持つのは表示中の 1 枚（切り抜き用の原寸）と、画面の大きさに縮小した表示用のビットマップだけ
using System.Drawing.Drawing2D;
using ImageViewer.Core.Editing;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Thumbnails;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageViewer.App.Theming;

namespace ImageViewer.App.Dialogs;

public sealed class CropDialog : ThemedForm
{
    private const int MinSizePx = 8; // 切り抜き枠の最小（画像の px）

    /// <summary>このアプリを起動している間は前回の設定を引き継ぐ</summary>
    private static int _lastAspect;
    private static decimal _lastCustomW = 16, _lastCustomH = 10;
    private static bool _lastFlip, _lastToCustomFolder, _lastKeepPosition;
    private static string _lastFolder = "";

    private readonly IReadOnlyList<string> _paths;
    private int _index = -1;
    private SixLabors.ImageSharp.Image<Rgba32>? _image; // 回転補正済みの原寸（切り抜き元）
    private Bitmap? _display;                           // キャンバスの大きさに縮小した表示用
    private CancellationTokenSource? _loadCts;
    private bool _busy;
    // 画像を読み込み中（_index はもう次の画像なのに _image はまだ前の画像）。この間は保存しない（前の画像の画素で上書きしないように）
    private bool _loading;

    // 画像 → キャンバスの変換
    private double _scale = 1, _offX, _offY;

    // 切り抜き枠（画像座標 x0, y0, x1, y1）とドラッグの状態
    private double[]? _rect;
    // 引き継ぐ元の枠: 最後に自分で決めた（動かした・大きさを変えた・比を変えて作り直した）枠と、その画像の大きさ。
    // 前の画像からではなくここから引き継ぐので、大きさの違う画像を行き来しても枠がずれていかない
    private ((double, double, double, double) Rect, int Width, int Height)? _anchor;
    private enum DragMode { None, Move, Resize }
    private DragMode _mode;
    private (double X, double Y) _fixed;   // 大きさを変えるときに動かない角
    private (double X, double Y) _moveOff;

    private readonly CanvasPanel _canvas = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 32, 32), Cursor = Cursors.Cross };
    private readonly Label _name = new() { AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
    private readonly Button _prev = new() { Text = "◀ 前", AutoSize = true };
    private readonly Button _next = new() { Text = "次 ▶", AutoSize = true };
    private readonly List<(RadioButton Radio, double? Ratio)> _aspectRadios = new();
    private readonly RadioButton _customAspect = new() { Text = "指定", AutoSize = true };
    private readonly NumericUpDown _customW = new() { Minimum = 1, Maximum = 999, Width = 52, TextAlign = HorizontalAlignment.Right };
    private readonly NumericUpDown _customH = new() { Minimum = 1, Maximum = 999, Width = 52, TextAlign = HorizontalAlignment.Right };
    private readonly CheckBox _flip = new() { Text = "縦横を入れ替え（4:3 → 3:4）", AutoSize = true };
    private readonly CheckBox _keepPosition = new() { Text = "次の画像も同じ位置で切り抜く", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly Label _selectionSize = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly RadioButton _toSame = new() { Text = "元と同じフォルダ", AutoSize = true };
    private readonly RadioButton _toCustom = new() { Text = "指定のフォルダ", AutoSize = true };
    private readonly TextBox _folder = new() { Width = 190 };
    private readonly Label _outputHint = new() { AutoSize = true, MaximumSize = new Size(190, 0) };
    // 保存のボタンは「保存（別の名前）」と「上書き保存（元の画像を置き換える）」を横に並べる
    private readonly Button _save = new() { Text = "保存", Width = 100, Height = 30 };
    private readonly Button _saveOver = new() { Text = "上書き保存", Width = 100, Height = 30 };
    private readonly Button _saveNext = new() { Text = "保存して次へ", Width = 100, Height = 30 };
    private readonly Button _saveNextOver = new() { Text = "上書きして次へ", Width = 100, Height = 30 };
    private readonly Label _saveAllCaption = new() { AutoSize = true, Margin = new Padding(3, 10, 3, 0) };
    private readonly Button _saveAll = new() { Text = "全部を保存", Width = 100, Height = 30 };
    private readonly Button _saveAllOver = new() { Text = "全部を上書き", Width = 100, Height = 30 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(210, 0), Margin = new Padding(3, 8, 3, 3) };
    private readonly System.Windows.Forms.Timer _resizeDelay = new() { Interval = 80 };
    private readonly ToolTip _toolTip = new();
    private const string CarriedNoticePrefix = "枠を決めた画像";

    /// <summary>保存したファイルの数（一覧の読み直しの判断用）</summary>
    public int SavedCount { get; private set; }

    private sealed class CanvasPanel : Panel
    {
        public CanvasPanel() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public CropDialog(IReadOnlyList<string> paths, int startIndex = 0)
    {
        _paths = paths;
        Text = paths.Count == 1 ? "切り抜き" : $"切り抜き（{paths.Count} 枚）";
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(1200, screen.Width * 9 / 10), Math.Min(860, screen.Height * 9 / 10));
        MinimumSize = new Size(700, 520);

        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6, 4, 6, 0) };
        _prev.Click += async (_, _) => await StepAsync(-1);
        _next.Click += async (_, _) => await StepAsync(1);
        _prev.Enabled = _next.Enabled = paths.Count > 1;
        top.Controls.AddRange(new Control[] { _prev, _next, _name });

        _canvas.Paint += Canvas_Paint;
        _canvas.MouseDown += Canvas_MouseDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseUp += (_, _) => _mode = DragMode.None;
        _canvas.Resize += (_, _) => { _resizeDelay.Stop(); _resizeDelay.Start(); };
        _resizeDelay.Tick += (_, _) => { _resizeDelay.Stop(); RebuildDisplay(); };

        Controls.Add(_canvas);
        Controls.Add(BuildSidePanel());
        Controls.Add(top);

        _customW.Value = _lastCustomW;
        _customH.Value = _lastCustomH;
        (_lastAspect < _aspectRadios.Count ? _aspectRadios[_lastAspect].Radio : _customAspect).Checked = true;
        _flip.Checked = _lastFlip;
        _keepPosition.Checked = _lastKeepPosition;
        _keepPosition.Visible = paths.Count > 1;
        _keepPosition.CheckedChanged += (_, _) => UpdateButtons();
        _folder.Text = _lastFolder;
        (_lastToCustomFolder && _lastFolder.Length > 0 ? _toCustom : _toSame).Checked = true;
        _outputHint.Text = "「保存」は「元の名前_crop」で保存します（同名があれば (2) などを付けます）。" +
                           "「上書き保存」は元のファイルを置き換えます（元には戻せません。HEIC・RAW など書き出せない形式やアニメーションは上書きしません）";
        _outputHint.ForeColor = Theme.Current.TextMuted;
        // 「指定」は別の行（別の親）にあるので、ラジオボタンの排他は自分で行う
        var allAspects = _aspectRadios.Select(a => a.Radio).Append(_customAspect).ToList();
        foreach (var rb in allAspects)
            rb.CheckedChanged += (_, _) =>
            {
                if (!rb.Checked) return;
                foreach (var other in allAspects) if (other != rb) other.Checked = false;
                OnAspectChanged();
            };
        _customW.ValueChanged += (_, _) => { if (_customAspect.Checked) OnAspectChanged(); };
        _customH.ValueChanged += (_, _) => { if (_customAspect.Checked) OnAspectChanged(); };
        _customW.Enter += (_, _) => _customAspect.Checked = true;
        _customH.Enter += (_, _) => _customAspect.Checked = true;
        _flip.CheckedChanged += (_, _) => OnAspectChanged();
        UpdateButtons();

        Shown += async (_, _) => await ShowIndexAsync(Math.Clamp(startIndex, 0, paths.Count - 1));
        FormClosed += (_, _) =>
        {
            _loadCts?.Cancel();
            _image?.Dispose();
            _display?.Dispose();
            _toolTip.Dispose();
            _lastAspect = _aspectRadios.FindIndex(a => a.Radio.Checked) is int i and >= 0 ? i : _aspectRadios.Count;
            _lastCustomW = _customW.Value;
            _lastCustomH = _customH.Value;
            _lastFlip = _flip.Checked;
            _lastKeepPosition = _keepPosition.Checked;
            _lastFolder = _folder.Text.Trim();
            _lastToCustomFolder = _toCustom.Checked;
        };
    }

    private Control BuildSidePanel()
    {
        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Width = 230, Padding = new Padding(6, 4, 6, 4),
        };

        var aspectStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        foreach (var (name, ratio) in Cropper.AspectPresets)
        {
            var rb = new RadioButton { Text = name, AutoSize = true };
            _aspectRadios.Add((rb, ratio));
            aspectStack.Controls.Add(rb);
        }
        var customRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        customRow.Controls.AddRange(new Control[] { _customAspect, _customW, new Label { Text = ":", AutoSize = true, Margin = new Padding(0, 6, 0, 3) }, _customH });
        aspectStack.Controls.Add(customRow);
        aspectStack.Controls.Add(_flip);
        var aspectBox = new GroupBox { Text = "アスペクト比", AutoSize = true, Width = 210, Padding = new Padding(8) };
        aspectBox.Controls.Add(aspectStack);
        side.Controls.Add(aspectBox);
        side.Controls.Add(_keepPosition);
        _toolTip.SetToolTip(_keepPosition, "スクリーンショットのように、大きさと構成が同じ画像をまとめて切り抜くとき用です。\n" +
                                           "大きさが違う画像には、画像に対する割合で合わせます");
        side.Controls.Add(_selectionSize);

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
        outStack.Controls.AddRange(new Control[]
        {
            _toSame, _toCustom, _folder, browse, _outputHint,
        });
        var outBox = new GroupBox { Text = "保存先", AutoSize = true, Width = 210, Padding = new Padding(8), Margin = new Padding(3, 8, 3, 3) };
        outBox.Controls.Add(outStack);
        side.Controls.Add(outBox);

        _save.Click += async (_, _) => await SaveCurrentAsync(advance: false, overwrite: false);
        _saveOver.Click += async (_, _) => await SaveCurrentAsync(advance: false, overwrite: true);
        _saveNext.Click += async (_, _) => await SaveCurrentAsync(advance: true, overwrite: false);
        _saveNextOver.Click += async (_, _) => await SaveCurrentAsync(advance: true, overwrite: true);
        _saveAll.Click += async (_, _) => await SaveAllAsync(overwrite: false);
        _saveAllOver.Click += async (_, _) => await SaveAllAsync(overwrite: true);
        _toolTip.SetToolTip(_saveNext, "Enter");
        _toolTip.SetToolTip(_saveNextOver, "Shift+Enter");
        side.Controls.AddRange(new Control[]
        {
            ButtonRow(_save, _saveOver), ButtonRow(_saveNext, _saveNextOver), _saveAllCaption, ButtonRow(_saveAll, _saveAllOver), _status,
        });
        return side;
    }

    private static FlowLayoutPanel ButtonRow(params Control[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(buttons);
        return row;
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

    /// <param name="carry">false なら枠を引き継がずに作り直す（上書きして読み直すとき。引き継ぐ元の枠はそのまま）</param>
    private async Task ShowIndexAsync(int index, bool carry = true)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        _index = index;
        _loading = true;
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
            _rect = null;
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
        _name.Text = $"[{index + 1}/{_paths.Count}] {Path.GetFileName(path)}  （{image.Width} × {image.Height}）";
        if (!carry)
        {
            ResetRect();
        }
        else if (_keepPosition.Checked && _anchor is { } from)
        {
            var (x0, y0, x1, y1) = Cropper.CarryRect(from.Rect, from.Width, from.Height, image.Width, image.Height);
            _rect = new[] { x0, y0, x1, y1 };
            bool resized = from.Width != image.Width || from.Height != image.Height;
            if (resized)
            {
                _status.ForeColor = Theme.Current.TextMuted;
                _status.Text = $"{CarriedNoticePrefix}（{from.Width} × {from.Height}）と大きさが違うので、枠は割合で合わせました";
            }
            else if (_status.Text.StartsWith(CarriedNoticePrefix, StringComparison.Ordinal))
            {
                _status.Text = ""; // 前の画像で出したお知らせは消す
            }
        }
        else
        {
            ResetRect();
            SetAnchor();
        }
        RebuildDisplay();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _save.Enabled = _saveOver.Enabled = _saveNext.Enabled = _saveNextOver.Enabled = !_busy && !_loading && _image != null;
        _saveNext.Visible = _saveNextOver.Visible = _paths.Count > 1;
        // 位置を引き継ぐなら「今の範囲で」（自由な比でもよい）、そうでなければ「同じ比で中央から」
        _saveAllCaption.Text = _keepPosition.Checked ? "全部を今の範囲で切り抜き:" : "全部を同じ比で中央から切り抜き:";
        _saveAll.Enabled = _saveAllOver.Enabled = !_busy && !_loading && (_keepPosition.Checked ? _anchor != null : CurrentAspect() != null);
        _prev.Enabled = _next.Enabled = !_busy && _paths.Count > 1;
    }

    // ---- アスペクト比 ----

    private double? CurrentAspect()
    {
        double? aspect = _customAspect.Checked
            ? (double)_customW.Value / (double)_customH.Value
            : _aspectRadios.FirstOrDefault(a => a.Radio.Checked).Ratio;
        return Cropper.Flip(aspect, _flip.Checked);
    }

    private void OnAspectChanged()
    {
        UpdateButtons();
        if (_image == null) return;
        ResetRect();
        SetAnchor();
        _canvas.Invalidate();
    }

    /// <summary>今のアスペクト比で、画像の中央に最大の枠を作る（自由なら全体）</summary>
    private void ResetRect()
    {
        if (_image == null) return;
        if (CurrentAspect() is double aspect)
        {
            var (x, y, w, h) = Cropper.CenterRect(_image.Width, _image.Height, aspect);
            _rect = new[] { x, y, x + w, y + h };
        }
        else
        {
            _rect = new[] { 0.0, 0.0, _image.Width, (double)_image.Height };
        }
    }

    /// <summary>今の枠を、次の画像へ引き継ぐ元にする</summary>
    private void SetAnchor()
    {
        if (_rect != null && _image != null) _anchor = ((_rect[0], _rect[1], _rect[2], _rect[3]), _image.Width, _image.Height);
    }

    // ---- 座標の変換 ----

    private (double X, double Y) ImageToCanvas(double ix, double iy) => (_offX + ix * _scale, _offY + iy * _scale);
    private (double X, double Y) CanvasToImage(double cx, double cy) => ((cx - _offX) / _scale, (cy - _offY) / _scale);
    private (double X, double Y) ClampToImage((double X, double Y) p) =>
        (Math.Clamp(p.X, 0, _image!.Width), Math.Clamp(p.Y, 0, _image.Height));
    private int HandleRadius => Math.Max(7, 7 * DeviceDpi / 96);

    // ---- 描画（重い縮小は RebuildDisplay だけ。ドラッグ中の描画は縮小済みの絵＋枠だけ） ----

    private void RebuildDisplay()
    {
        _display?.Dispose();
        _display = null;
        if (_image != null)
        {
            int cw = Math.Max(1, _canvas.ClientSize.Width), ch = Math.Max(1, _canvas.ClientSize.Height);
            _scale = Math.Min((double)cw / _image.Width, (double)ch / _image.Height);
            int dw = Math.Max(1, (int)Math.Round(_image.Width * _scale)), dh = Math.Max(1, (int)Math.Round(_image.Height * _scale));
            _offX = (cw - dw) / 2.0;
            _offY = (ch - dh) / 2.0;
            if (dw == _image.Width && dh == _image.Height)
                _display = ThumbnailGenerator.ToPArgbBitmap(_image);
            else
            {
                using var small = _image.Clone(x => x.Resize(dw, dh, KnownResamplers.Triangle));
                _display = ThumbnailGenerator.ToPArgbBitmap(small);
            }
        }
        _canvas.Invalidate();
    }

    private void Canvas_Paint(object? sender, PaintEventArgs e)
    {
        if (_display == null || _rect == null) return;
        var g = e.Graphics;
        g.DrawImageUnscaled(_display, (int)Math.Round(_offX), (int)Math.Round(_offY));

        var (x0, y0) = ImageToCanvas(_rect[0], _rect[1]);
        var (x1, y1) = ImageToCanvas(_rect[2], _rect[3]);
        float dx0 = (float)_offX, dy0 = (float)_offY, dx1 = dx0 + _display.Width, dy1 = dy0 + _display.Height;
        float fx0 = (float)x0, fy0 = (float)y0, fx1 = (float)x1, fy1 = (float)y1;

        // 範囲の外を暗くする
        using (var shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
        {
            if (fy0 > dy0) g.FillRectangle(shade, dx0, dy0, dx1 - dx0, fy0 - dy0);
            if (dy1 > fy1) g.FillRectangle(shade, dx0, fy1, dx1 - dx0, dy1 - fy1);
            if (fx0 > dx0) g.FillRectangle(shade, dx0, fy0, fx0 - dx0, fy1 - fy0);
            if (dx1 > fx1) g.FillRectangle(shade, fx1, fy0, dx1 - fx1, fy1 - fy0);
        }

        // 枠と三分割の線
        using (var frame = new Pen(Color.White, 1))
            g.DrawRectangle(frame, fx0, fy0, fx1 - fx0, fy1 - fy0);
        using (var guide = new Pen(Color.FromArgb(180, 255, 255, 255), 1) { DashPattern = new[] { 3f, 3f } })
        {
            for (int k = 1; k <= 2; k++)
            {
                float gx = fx0 + (fx1 - fx0) * k / 3, gy = fy0 + (fy1 - fy0) * k / 3;
                g.DrawLine(guide, gx, fy0, gx, fy1);
                g.DrawLine(guide, fx0, gy, fx1, gy);
            }
        }

        // 四隅のハンドル
        using var fill = new SolidBrush(Color.White);
        using var border = new Pen(Color.FromArgb(0, 120, 215), 1);
        int hs = HandleRadius - 3;
        foreach (var (hx, hy) in new[] { (fx0, fy0), (fx1, fy0), (fx1, fy1), (fx0, fy1) })
        {
            g.FillRectangle(fill, hx - hs, hy - hs, hs * 2, hs * 2);
            g.DrawRectangle(border, hx - hs, hy - hs, hs * 2, hs * 2);
        }

        _selectionSize.Text = $"範囲: {Math.Round(_rect[2] - _rect[0])} × {Math.Round(_rect[3] - _rect[1])} px";
    }

    // ---- マウス ----

    private void Canvas_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_image == null || _rect == null || e.Button != MouseButtons.Left) return;
        _canvas.Focus();
        var corners = new (double X, double Y)[] { (_rect[0], _rect[1]), (_rect[2], _rect[1]), (_rect[2], _rect[3]), (_rect[0], _rect[3]) };
        int hr = HandleRadius;
        for (int i = 0; i < 4; i++)
        {
            var (cx, cy) = ImageToCanvas(corners[i].X, corners[i].Y);
            if (Math.Abs(e.X - cx) <= hr && Math.Abs(e.Y - cy) <= hr)
            {
                _mode = DragMode.Resize;
                _fixed = corners[(i + 2) % 4]; // 対角を固定
                return;
            }
        }
        // 枠の中なら移動、外なら新しく作る
        var (ix, iy) = CanvasToImage(e.X, e.Y);
        if (_rect[0] <= ix && ix <= _rect[2] && _rect[1] <= iy && iy <= _rect[3])
        {
            _mode = DragMode.Move;
            _moveOff = (ix - _rect[0], iy - _rect[1]);
        }
        else
        {
            _fixed = ClampToImage((ix, iy));
            _mode = DragMode.Resize;
        }
    }

    private void Canvas_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_mode == DragMode.None || _image == null || _rect == null) return;
        if (_mode == DragMode.Move) DoMove(e);
        else DoResize(e);
        SetAnchor();
        _canvas.Invalidate();
    }

    private void DoMove(MouseEventArgs e)
    {
        var (ix, iy) = CanvasToImage(e.X, e.Y);
        double rw = _rect![2] - _rect[0], rh = _rect[3] - _rect[1];
        double x0 = Math.Clamp(ix - _moveOff.X, 0, _image!.Width - rw);
        double y0 = Math.Clamp(iy - _moveOff.Y, 0, _image.Height - rh);
        _rect = new[] { x0, y0, x0 + rw, y0 + rh };
    }

    private void DoResize(MouseEventArgs e)
    {
        int imgW = _image!.Width, imgH = _image.Height;
        var (fx, fy) = _fixed;
        var (mx, my) = ClampToImage(CanvasToImage(e.X, e.Y));
        int dirX = mx >= fx ? 1 : -1, dirY = my >= fy ? 1 : -1;
        double w = Math.Abs(mx - fx), h = Math.Abs(my - fy);
        if (CurrentAspect() is double a)
        {
            if (w / a >= h) h = w / a;
            else w = h * a;
            double availX = dirX > 0 ? imgW - fx : fx, availY = dirY > 0 ? imgH - fy : fy;
            double s = Math.Min(Math.Min(w > 0 ? availX / w : 1, h > 0 ? availY / h : 1), 1.0);
            w *= s;
            h *= s;
        }
        double nx = fx + dirX * w, ny = fy + dirY * h;
        double x0 = Math.Min(fx, nx), x1 = Math.Max(fx, nx), y0 = Math.Min(fy, ny), y1 = Math.Max(fy, ny);
        if (x1 - x0 >= MinSizePx && y1 - y0 >= MinSizePx) _rect = new[] { x0, y0, x1, y1 };
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
        if (_busy || _loading || _image == null || _rect == null) return;
        string src = _paths[_index];
        if (OutputFolderFor(src, overwrite) is not string folder) return;
        var box = Cropper.ClampBox(_rect[0], _rect[1], _rect[2], _rect[3], _image.Width, _image.Height);
        if (box.Width < 1 || box.Height < 1) return;

        var image = _image;
        SetBusy(true);
        try
        {
            string dst = await Task.Run(() =>
            {
                string d = overwrite ? src : Cropper.OutputPathFor(src, folder);
                Cropper.SaveCrop(image, box, src, d);
                return d;
            });
            SavedCount++;
            _status.ForeColor = Theme.Current.Text;
            _status.Text = overwrite
                ? $"上書きしました: {Path.GetFileName(dst)}（{box.Width} × {box.Height}）"
                : $"保存しました: {Path.GetFileName(dst)}（{box.Width} × {box.Height}）";
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
            await ShowIndexAsync(_index, carry: false); // 切り抜いた後の画像を出し直す
        }
    }

    /// <summary>位置を引き継ぐなら最後に決めた枠を全部へ（大きさが違う画像には割合で合わせる）、そうでなければ同じ比で中央から切り抜く</summary>
    private async Task SaveAllAsync(bool overwrite)
    {
        if (_busy || _loading) return;
        if (_keepPosition.Checked)
        {
            if (_anchor is not { } from) return;
            await SaveAllAsync((src, dst) => Cropper.CropCarried(src, from.Rect, from.Width, from.Height, dst), overwrite);
        }
        else
        {
            if (CurrentAspect() is not double aspect) return;
            await SaveAllAsync((src, dst) => Cropper.CropCenter(src, aspect, dst), overwrite);
        }
    }

    /// <summary>選んだ画像を全部、crop(元, 保存先) で切り抜いて保存する（overwrite なら元の画像を置き換える）</summary>
    private async Task SaveAllAsync(Action<string, string> crop, bool overwrite)
    {
        if (!overwrite && _toCustom.Checked && OutputFolderFor(_paths[0], overwrite) == null) return;
        var targets = _paths.Select(p => (Src: p, Folder: _toCustom.Checked && !overwrite ? _folder.Text.Trim() : Path.GetDirectoryName(p)!)).ToList();
        SetBusy(true);
        var errors = new List<string>();
        int ok = 0;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var (src, folder) = targets[i];
                _status.ForeColor = Theme.Current.Text;
                _status.Text = $"切り抜き中 {i + 1} / {targets.Count}: {Path.GetFileName(src)}";
                try
                {
                    await Task.Run(() => crop(src, overwrite ? src : Cropper.OutputPathFor(src, folder)));
                    ok++;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    errors.Add($"{Path.GetFileName(src)}: {ex.Message}");
                }
            }
        }
        finally
        {
            SavedCount += ok;
            SetBusy(false);
        }
        _status.ForeColor = errors.Count > 0 ? Theme.Current.Danger : Theme.Current.Text;
        _status.Text = $"{ok} 枚を{(overwrite ? "上書き" : "保存")}しました" + (errors.Count > 0 ? $"・{errors.Count} 枚は失敗しました" : "");
        if (errors.Count > 0)
            MessageBox.Show(this, string.Join("\n", errors.Take(15)), $"切り抜けなかった画像（{errors.Count} 枚）", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (overwrite && ok > 0 && _index >= 0) await ShowIndexAsync(_index, carry: false); // 表示中の画像も切り抜いた後のものにする
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        UpdateButtons();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy) e.Cancel = true; // 保存中は閉じない（切り抜き元の画像を使っている）
        base.OnFormClosing(e);
    }
}
