// モザイク・ぼかしダイアログ（切り抜きダイアログと同じ作り）
// - 画像の上をドラッグで範囲を選ぶ（いくつでも）。形は 四角 / 円・楕円 / 自由（ドラッグでなぞった形）。
//   範囲の中をドラッグで移動、四隅で大きさを変える（自由な形も外枠ごと伸び縮みする）。選んだ範囲は Delete か右クリックで消す
// - モザイク / ぼかしと強さ（1〜10）。表示はかけた後の見た目（縮小した画像にかけるので、細かい所は保存したものと少し違う）
// - 選択した画像を ◀ ▶（PageUp / PageDown）で切り替え。Enter で保存して次へ。範囲は画像ごとに覚える
// - 「次の画像も同じ範囲にかける」なら、切り替えても範囲を引き継ぐ（大きさが違う画像には割合で合わせる）。
//   一括の「全部に同じ範囲でかける」も出る（スクリーンショットの同じ所を隠すとき向け）
// - 「保存」は元の画像を残して別の名前で、「上書き保存」は元の画像を置き換える（確かめない。アニメ・書き出せない形式などは上書きしない）
// - 持つのは表示中の 1 枚（原寸）と、画面の大きさに縮小した画像（かける前）と表示用のビットマップだけ
using System.Drawing.Drawing2D;
using ImageViewer.Core.Editing;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Thumbnails;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageViewer.App.Theming;

namespace ImageViewer.App.Dialogs;

public sealed class MaskDialog : ThemedForm
{
    private const int MinSizePx = 4; // 範囲の最小（画像の px）。これより小さくドラッグしたものは作らない

    /// <summary>このアプリを起動している間は前回の設定を引き継ぐ</summary>
    private static MaskEffect _lastEffect = MaskEffect.Mosaic;
    private static MaskShape _lastShape = MaskShape.Rectangle;
    private static int _lastLevel = Masker.DefaultLevel;
    private static bool _lastToCustomFolder, _lastKeepPosition;
    private static string _lastFolder = "";

    private readonly IReadOnlyList<string> _paths;
    private int _index = -1;
    private SixLabors.ImageSharp.Image<Rgba32>? _image; // 回転補正済みの原寸（保存元）
    private SixLabors.ImageSharp.Image<Rgba32>? _small; // キャンバスの大きさに縮小した、かける前の画像
    private Bitmap? _display;                           // _small にかけた表示用
    private CancellationTokenSource? _loadCts;
    private bool _busy;
    // 画像を読み込み中（_index はもう次の画像なのに _image はまだ前の画像）。この間は保存しない（前の画像の画素で上書きしないように）
    private bool _loading;

    // 画像 → キャンバスの変換
    private double _scale = 1, _offX, _offY;

    // 範囲（画像座標）。後のものほど上（クリックで先に当たる）
    private List<MaskRegion> _rects = new();
    private int _selected = -1;
    // 「同じ範囲にかける」がオフのときに、画像ごとに選んだ範囲（行き来しても消えないように）
    private readonly Dictionary<int, List<MaskRegion>> _rectsByIndex = new();
    // 引き継ぐ元: 最後に自分で範囲を変えた画像の範囲と、その画像の大きさ
    private (List<MaskRegion> Rects, int Width, int Height)? _anchor;
    private enum DragMode { None, Move, Resize, Draw }
    private DragMode _mode;
    private bool _creating;                // 新しく作っている範囲（小さすぎれば離したときに消す）
    private (double X, double Y) _fixed;   // 大きさを変えるときに動かない角
    private (double X, double Y) _moveOff;
    private List<(double X, double Y)>? _path; // 自由な形をなぞっている途中の点（画像座標）

    private readonly CanvasPanel _canvas = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 32, 32), Cursor = Cursors.Cross };
    private readonly Label _name = new() { AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
    private readonly Button _prev = new() { Text = "◀ 前", AutoSize = true };
    private readonly Button _next = new() { Text = "次 ▶", AutoSize = true };
    private readonly RadioButton _shapeRect = new() { Text = "四角", AutoSize = true };
    private readonly RadioButton _shapeEllipse = new() { Text = "円・楕円", AutoSize = true };
    private readonly RadioButton _shapeFree = new() { Text = "自由（なぞる）", AutoSize = true };
    private readonly RadioButton _mosaic = new() { Text = "モザイク", AutoSize = true };
    private readonly RadioButton _blur = new() { Text = "ぼかし", AutoSize = true };
    private readonly TrackBar _level = new()
    {
        Minimum = Masker.MinLevel, Maximum = Masker.MaxLevel, TickFrequency = 1, LargeChange = 1, Width = 180, AutoSize = false, Height = 32,
    };
    private readonly Label _levelText = new() { AutoSize = true };
    private readonly Button _removeSelected = new() { Text = "選んだ範囲を消す (Del)", Width = 200, Height = 26 };
    private readonly Button _removeAll = new() { Text = "範囲をすべて消す", Width = 200, Height = 26 };
    private readonly CheckBox _keepPosition = new() { Text = "次の画像も同じ範囲にかける", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly Label _selectionInfo = new() { AutoSize = true, MaximumSize = new Size(210, 0), Margin = new Padding(3, 8, 3, 3) };
    private readonly RadioButton _toSame = new() { Text = "元と同じフォルダ", AutoSize = true };
    private readonly RadioButton _toCustom = new() { Text = "指定のフォルダ", AutoSize = true };
    private readonly TextBox _folder = new() { Width = 190 };
    private readonly Label _outputHint = new() { AutoSize = true, MaximumSize = new Size(190, 0) };
    // 保存のボタンは「保存（別の名前）」の下に「上書き保存（元の画像を置き換える）」を置く
    private readonly Button _save = new() { Text = "保存", Width = 200, Height = 30 };
    private readonly Button _saveOver = new() { Text = "上書き保存", Width = 200, Height = 30 };
    private readonly Button _saveNext = new() { Text = "保存して次へ (Enter)", Width = 200, Height = 30 };
    private readonly Button _saveNextOver = new() { Text = "上書きして次へ (Shift+Enter)", Width = 200, Height = 30 };
    private readonly Label _saveAllCaption = new() { Text = "全部に同じ範囲でかける:", AutoSize = true, Margin = new Padding(3, 10, 3, 0) };
    private readonly Button _saveAll = new() { Text = "全部を保存", Width = 200, Height = 30 };
    private readonly Button _saveAllOver = new() { Text = "全部を上書き", Width = 200, Height = 30 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(210, 0), Margin = new Padding(3, 8, 3, 3) };
    private readonly System.Windows.Forms.Timer _resizeDelay = new() { Interval = 80 };
    private readonly ToolTip _toolTip = new();
    private const string CarriedNoticePrefix = "範囲を決めた画像";

    /// <summary>保存したファイルの数（一覧の読み直しの判断用）</summary>
    public int SavedCount { get; private set; }

    private sealed class CanvasPanel : Panel
    {
        public CanvasPanel() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public MaskDialog(IReadOnlyList<string> paths, int startIndex = 0)
    {
        _paths = paths;
        Text = paths.Count == 1 ? "モザイク・ぼかし" : $"モザイク・ぼかし（{paths.Count} 枚）";
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

        (_lastShape switch { MaskShape.Ellipse => _shapeEllipse, MaskShape.Freehand => _shapeFree, _ => _shapeRect }).Checked = true;
        (_lastEffect == MaskEffect.Blur ? _blur : _mosaic).Checked = true;
        _level.Value = Math.Clamp(_lastLevel, Masker.MinLevel, Masker.MaxLevel);
        _mosaic.CheckedChanged += (_, _) => OnEffectChanged();
        _level.ValueChanged += (_, _) => OnEffectChanged();
        _keepPosition.Checked = _lastKeepPosition;
        _keepPosition.Visible = paths.Count > 1;
        _keepPosition.CheckedChanged += (_, _) =>
        {
            // オンにしたら、いま見えている範囲を引き継ぐ元にする（前に別の画像で決めた範囲を使わないように）
            if (_keepPosition.Checked) SetAnchor();
            UpdateButtons();
        };
        _folder.Text = _lastFolder;
        (_lastToCustomFolder && _lastFolder.Length > 0 ? _toCustom : _toSame).Checked = true;
        _outputHint.Text = "「保存」は「元の名前_mosaic」（ぼかしは _blur）で保存します（同名があれば (2) などを付けます）。" +
                           "「上書き保存」は元のファイルを置き換えます（元には戻せません。HEIC・RAW など書き出せない形式やアニメーションは上書きしません）";
        _outputHint.ForeColor = Theme.Current.TextMuted;
        UpdateLevelText();
        UpdateButtons();

        Shown += async (_, _) => await ShowIndexAsync(Math.Clamp(startIndex, 0, paths.Count - 1));
        FormClosed += (_, _) =>
        {
            _loadCts?.Cancel();
            _image?.Dispose();
            _small?.Dispose();
            _display?.Dispose();
            _toolTip.Dispose();
            _lastEffect = CurrentEffect;
            _lastShape = CurrentShape;
            _lastLevel = _level.Value;
            _lastKeepPosition = _keepPosition.Checked;
            _lastFolder = _folder.Text.Trim();
            _lastToCustomFolder = _toCustom.Checked;
        };
    }

    private MaskEffect CurrentEffect => _blur.Checked ? MaskEffect.Blur : MaskEffect.Mosaic;

    /// <summary>これから作る範囲の形（作った範囲の形は変えない）</summary>
    private MaskShape CurrentShape => _shapeEllipse.Checked ? MaskShape.Ellipse : _shapeFree.Checked ? MaskShape.Freehand : MaskShape.Rectangle;

    private Control BuildSidePanel()
    {
        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Width = 230, Padding = new Padding(6, 4, 6, 4),
        };

        var shapeStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        shapeStack.Controls.AddRange(new Control[] { _shapeRect, _shapeEllipse, _shapeFree });
        var shapeBox = new GroupBox { Text = "範囲の形", AutoSize = true, Width = 210, Padding = new Padding(8) };
        shapeBox.Controls.Add(shapeStack);
        side.Controls.Add(shapeBox);
        _toolTip.SetToolTip(_shapeFree, "隠したい所のまわりをドラッグでなぞると、その形の範囲になります（離すと始点と終点をつなぎます）");

        var effectRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        effectRow.Controls.AddRange(new Control[] { _mosaic, _blur });
        var effectStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        effectStack.Controls.AddRange(new Control[] { effectRow, _levelText, _level });
        var effectBox = new GroupBox { Text = "かけ方", AutoSize = true, Width = 210, Padding = new Padding(8) };
        effectBox.Controls.Add(effectStack);
        side.Controls.Add(effectBox);

        _removeSelected.Click += (_, _) => RemoveSelected();
        _removeAll.Click += (_, _) => RemoveAll();
        side.Controls.AddRange(new Control[] { _selectionInfo, _removeSelected, _removeAll, _keepPosition });
        _toolTip.SetToolTip(_keepPosition, "スクリーンショットのように、大きさと構成が同じ画像の同じ所を隠すとき用です。\n" +
                                           "大きさが違う画像には、画像に対する割合で合わせます");

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
        _saveAll.Click += async (_, _) => await SaveAllCarriedAsync(overwrite: false);
        _saveAllOver.Click += async (_, _) => await SaveAllCarriedAsync(overwrite: true);
        side.Controls.AddRange(new Control[] { _save, _saveOver, _saveNext, _saveNextOver, _saveAllCaption, _saveAll, _saveAllOver, _status });
        return side;
    }

    private void OnEffectChanged()
    {
        UpdateLevelText();
        RenderPreview();
    }

    private void UpdateLevelText()
    {
        string size = _image == null ? "" : CurrentEffect == MaskEffect.Mosaic
            ? $"（1 マス {Masker.EffectSize(_image.Width, _image.Height, _level.Value)} px）"
            : "";
        _levelText.Text = $"強さ: {_level.Value}{size}";
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

    /// <param name="carry">false なら範囲を引き継がずに空にする（上書きして読み直すとき。引き継ぐ元の範囲はそのまま）</param>
    private async Task ShowIndexAsync(int index, bool carry = true)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        if (_index >= 0 && _image != null && !_loading) _rectsByIndex[_index] = _rects;
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
            _rects = new();
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
        if (!carry)
        {
            _rects = new();
            _rectsByIndex.Remove(index);
        }
        else if (_keepPosition.Checked && _anchor is { } from)
        {
            _rects = Masker.CarryRegions(from.Rects, from.Width, from.Height, image.Width, image.Height);
            bool resized = from.Width != image.Width || from.Height != image.Height;
            if (resized && _rects.Count > 0)
            {
                _status.ForeColor = Theme.Current.TextMuted;
                _status.Text = $"{CarriedNoticePrefix}（{from.Width} × {from.Height}）と大きさが違うので、範囲は割合で合わせました";
            }
            else if (_status.Text.StartsWith(CarriedNoticePrefix, StringComparison.Ordinal))
            {
                _status.Text = ""; // 前の画像で出したお知らせは消す
            }
        }
        else
        {
            _rects = _rectsByIndex.TryGetValue(index, out var saved) ? saved : new();
        }
        UpdateLevelText();
        RebuildDisplay();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool ready = !_busy && !_loading && _image != null;
        _save.Enabled = _saveOver.Enabled = _saveNext.Enabled = _saveNextOver.Enabled = ready && _rects.Count > 0;
        _saveNext.Visible = _saveNextOver.Visible = _paths.Count > 1;
        _saveAllCaption.Visible = _saveAll.Visible = _saveAllOver.Visible = _paths.Count > 1 && _keepPosition.Checked;
        _saveAll.Enabled = _saveAllOver.Enabled = ready && _rects.Count > 0;
        _removeSelected.Enabled = !_busy && _selected >= 0;
        _removeAll.Enabled = !_busy && _rects.Count > 0;
        _prev.Enabled = _next.Enabled = !_busy && _paths.Count > 1;
        _selectionInfo.Text = _image == null ? ""
            : _rects.Count == 0 ? "画像の上をドラッグして、隠す範囲を選んでください（いくつでも選べます）"
            : _selected >= 0
                ? $"範囲 {_rects.Count} 個（選んだ範囲: {Math.Round(_rects[_selected].Width)} × {Math.Round(_rects[_selected].Height)} px）"
                : $"範囲 {_rects.Count} 個";
    }

    /// <summary>今の範囲を、次の画像へ引き継ぐ元にする</summary>
    private void SetAnchor()
    {
        if (_image != null)
            _anchor = (_rects.ToList(), _image.Width, _image.Height);
    }

    /// <summary>範囲を変えた後: 引き継ぐ元を更新して、表示とボタンを合わせる</summary>
    private void OnRectsChanged()
    {
        SetAnchor();
        RenderPreview();
        UpdateButtons();
    }

    private void RemoveSelected()
    {
        if (_busy || _selected < 0 || _selected >= _rects.Count) return;
        _rects.RemoveAt(_selected);
        _selected = -1;
        OnRectsChanged();
    }

    private void RemoveAll()
    {
        if (_busy || _rects.Count == 0) return;
        _rects.Clear();
        _selected = -1;
        OnRectsChanged();
    }

    // ---- 座標の変換 ----

    private (double X, double Y) ImageToCanvas(double ix, double iy) => (_offX + ix * _scale, _offY + iy * _scale);
    private (double X, double Y) CanvasToImage(double cx, double cy) => ((cx - _offX) / _scale, (cy - _offY) / _scale);
    private (double X, double Y) ClampToImage((double X, double Y) p) =>
        (Math.Clamp(p.X, 0, _image!.Width), Math.Clamp(p.Y, 0, _image.Height));
    private int HandleRadius => Math.Max(7, 7 * DeviceDpi / 96);

    // ---- 描画（重い縮小は RebuildDisplay だけ。範囲を変えたときは縮小済みの画像にかけ直すだけ） ----

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

    /// <summary>縮小した画像に、今の範囲・かけ方・強さ（表示の倍率に合わせる）でかけて、表示用のビットマップを作り直す</summary>
    private void RenderPreview()
    {
        _display?.Dispose();
        _display = null;
        if (_small != null && _image != null)
        {
            var regions = _rects.Select(r => r.Scale(_scale, _scale)).ToList();
            if (regions.Count == 0)
            {
                _display = ThumbnailGenerator.ToPArgbBitmap(_small);
            }
            else
            {
                int size = (int)Math.Round(Masker.EffectSize(_image.Width, _image.Height, _level.Value) * _scale);
                using var preview = _small.Clone();
                Masker.Apply(preview, regions, CurrentEffect, Math.Max(2, size));
                _display = ThumbnailGenerator.ToPArgbBitmap(preview);
            }
        }
        _canvas.Invalidate();
    }

    private void Canvas_Paint(object? sender, PaintEventArgs e)
    {
        if (_display == null) return;
        var g = e.Graphics;
        g.DrawImageUnscaled(_display, (int)Math.Round(_offX), (int)Math.Round(_offY));

        using var frame = new Pen(Color.FromArgb(200, 255, 255, 255), 1) { DashPattern = new[] { 4f, 3f } };
        using var selectedFrame = new Pen(Color.White, 1);
        using var fill = new SolidBrush(Color.White);
        using var border = new Pen(Color.FromArgb(0, 120, 215), 1);
        int hs = HandleRadius - 3;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < _rects.Count; i++)
        {
            var r = _rects[i];
            var (x0, y0) = ImageToCanvas(r.X0, r.Y0);
            var (x1, y1) = ImageToCanvas(r.X1, r.Y1);
            float fx0 = (float)x0, fy0 = (float)y0, fx1 = (float)x1, fy1 = (float)y1;
            var pen = i == _selected ? selectedFrame : frame;
            switch (r.Shape)
            {
                case MaskShape.Ellipse:
                    g.DrawEllipse(pen, fx0, fy0, fx1 - fx0, fy1 - fy0);
                    break;
                case MaskShape.Freehand:
                    var polygon = r.Polygon().Select(p => ToCanvasPoint(p.X, p.Y)).ToArray();
                    if (polygon.Length >= 2) g.DrawPolygon(pen, polygon);
                    break;
                default:
                    g.DrawRectangle(pen, fx0, fy0, fx1 - fx0, fy1 - fy0);
                    break;
            }
            if (i != _selected) continue;
            // 選んだ範囲だけ四隅のハンドルを出す（四角以外は外枠も薄く出して、どこを引っ張れば伸びるか分かるように）
            if (r.Shape != MaskShape.Rectangle) g.DrawRectangle(frame, fx0, fy0, fx1 - fx0, fy1 - fy0);
            // 選んだ範囲だけ四隅のハンドルを出す
            foreach (var (hx, hy) in new[] { (fx0, fy0), (fx1, fy0), (fx1, fy1), (fx0, fy1) })
            {
                g.FillRectangle(fill, hx - hs, hy - hs, hs * 2, hs * 2);
                g.DrawRectangle(border, hx - hs, hy - hs, hs * 2, hs * 2);
            }
        }
        // なぞっている途中の線
        if (_path is { Count: >= 2 } path)
            g.DrawLines(selectedFrame, path.Select(p => ToCanvasPoint(p.X, p.Y)).ToArray());
    }

    private PointF ToCanvasPoint(double ix, double iy)
    {
        var (cx, cy) = ImageToCanvas(ix, iy);
        return new PointF((float)cx, (float)cy);
    }

    // ---- マウス ----

    /// <summary>キャンバス上の点にある範囲（上にあるものから。無ければ -1）</summary>
    private int HitRect(double ix, double iy)
    {
        for (int i = _rects.Count - 1; i >= 0; i--)
            if (_rects[i].Contains(ix, iy)) return i;
        return -1;
    }

    private void Canvas_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_image == null || _busy || _loading) return;
        _canvas.Focus();
        var (ix, iy) = CanvasToImage(e.X, e.Y);

        // 右クリック: その範囲を消す
        if (e.Button == MouseButtons.Right)
        {
            int hit = HitRect(ix, iy);
            if (hit < 0) return;
            _rects.RemoveAt(hit);
            _selected = -1;
            OnRectsChanged();
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        // 選んだ範囲の四隅なら大きさを変える
        if (_selected >= 0)
        {
            var r = _rects[_selected];
            var corners = new (double X, double Y)[] { (r.X0, r.Y0), (r.X1, r.Y0), (r.X1, r.Y1), (r.X0, r.Y1) };
            int hr = HandleRadius;
            for (int i = 0; i < 4; i++)
            {
                var (cx, cy) = ImageToCanvas(corners[i].X, corners[i].Y);
                if (Math.Abs(e.X - cx) <= hr && Math.Abs(e.Y - cy) <= hr)
                {
                    _mode = DragMode.Resize;
                    _creating = false;
                    _fixed = corners[(i + 2) % 4]; // 対角を固定
                    return;
                }
            }
        }

        // 範囲の中なら選んで移動、外なら新しく作る（自由な形はなぞり始める）
        int target = HitRect(ix, iy);
        if (target >= 0)
        {
            _selected = target;
            _mode = DragMode.Move;
            _moveOff = (ix - _rects[target].X0, iy - _rects[target].Y0);
        }
        else if (CurrentShape == MaskShape.Freehand)
        {
            _selected = -1;
            _path = new() { ClampToImage((ix, iy)) };
            _mode = DragMode.Draw;
        }
        else
        {
            _fixed = ClampToImage((ix, iy));
            _rects.Add(new MaskRegion(CurrentShape, _fixed.X, _fixed.Y, _fixed.X, _fixed.Y));
            _selected = _rects.Count - 1;
            _mode = DragMode.Resize;
            _creating = true;
        }
        UpdateButtons();
        _canvas.Invalidate();
    }

    private void Canvas_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_mode == DragMode.Draw && _path != null && _image != null)
        {
            // 画面で 2px 以上動いたら点を足す（点が多すぎると重くなるだけなので）
            var last = ImageToCanvas(_path[^1].X, _path[^1].Y);
            if (Math.Abs(e.X - last.X) + Math.Abs(e.Y - last.Y) < 2) return;
            _path.Add(ClampToImage(CanvasToImage(e.X, e.Y)));
            _canvas.Invalidate();
            return;
        }
        if (_mode == DragMode.None || _image == null || _selected < 0) return;
        if (_mode == DragMode.Move) DoMove(e);
        else DoResize(e);
        RenderPreview();
        UpdateButtons();
    }

    private void Canvas_MouseUp(object? sender, MouseEventArgs e)
    {
        if (_mode == DragMode.None) return;
        if (_mode == DragMode.Draw)
        {
            // なぞった形を範囲にする（小さすぎるもの・点が足りないものは作らない）
            if (_path != null && MaskRegion.FromPath(_path) is { } drawn && drawn.Width >= MinSizePx && drawn.Height >= MinSizePx)
            {
                _rects.Add(drawn);
                _selected = _rects.Count - 1;
            }
            _path = null;
        }
        _mode = DragMode.None;
        // 小さすぎる範囲（クリックしただけ など）は作らない。クリックで選択を外すのと同じになる
        if (_creating && _selected >= 0)
        {
            var r = _rects[_selected];
            if (r.Width < MinSizePx || r.Height < MinSizePx)
            {
                _rects.RemoveAt(_selected);
                _selected = -1;
            }
        }
        _creating = false;
        OnRectsChanged();
    }

    private void DoMove(MouseEventArgs e)
    {
        var (ix, iy) = CanvasToImage(e.X, e.Y);
        var r = _rects[_selected];
        double rw = r.Width, rh = r.Height;
        double x0 = Math.Clamp(ix - _moveOff.X, 0, _image!.Width - rw);
        double y0 = Math.Clamp(iy - _moveOff.Y, 0, _image.Height - rh);
        _rects[_selected] = r.WithBounds(x0, y0, x0 + rw, y0 + rh);
    }

    private void DoResize(MouseEventArgs e)
    {
        var (fx, fy) = _fixed;
        var (mx, my) = ClampToImage(CanvasToImage(e.X, e.Y));
        double x0 = Math.Min(fx, mx), x1 = Math.Max(fx, mx), y0 = Math.Min(fy, my), y1 = Math.Max(fy, my);
        // 作っている途中は小さくてもよい（離したときに確かめる）。作った範囲を小さくしすぎることはできない
        if (_creating || (x1 - x0 >= MinSizePx && y1 - y0 >= MinSizePx)) _rects[_selected] = _rects[_selected].WithBounds(x0, y0, x1, y1);
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
        var regions = _rects.ToList();
        if (regions.Count == 0)
        {
            _status.ForeColor = Theme.Current.TextMuted;
            _status.Text = "隠す範囲を選んでから保存してください";
            return;
        }
        if (OutputFolderFor(src, overwrite) is not string folder) return;

        var image = _image;
        var effect = CurrentEffect;
        int level = _level.Value;
        SetBusy(true);
        try
        {
            string dst = await Task.Run(() =>
            {
                string d = overwrite ? src : Masker.OutputPathFor(src, folder, effect);
                Masker.SaveMasked(image, regions, effect, level, src, d);
                return d;
            });
            SavedCount++;
            _status.ForeColor = Theme.Current.Text;
            _status.Text = overwrite ? $"上書きしました: {Path.GetFileName(dst)}" : $"保存しました: {Path.GetFileName(dst)}";
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
            await ShowIndexAsync(_index, carry: false); // かけた後の画像を出し直す（同じ範囲に二重にかけないよう範囲は空に）
        }
    }

    /// <summary>
    /// いま表示している画像の範囲を全部の画像へ引き継いでかける（大きさが違う画像には割合で合わせる）。
    /// 引き継ぐ元（_anchor）ではなく見えている範囲を使うので、見えていない範囲にかけてしまうことはない
    /// </summary>
    private async Task SaveAllCarriedAsync(bool overwrite)
    {
        if (_busy || _loading || _image == null || _rects.Count == 0) return;
        var from = (Rects: _rects.ToList(), Width: _image.Width, Height: _image.Height);
        if (!overwrite && _toCustom.Checked && OutputFolderFor(_paths[0], overwrite) == null) return;
        var targets = _paths.Select(p => (Src: p, Folder: _toCustom.Checked && !overwrite ? _folder.Text.Trim() : Path.GetDirectoryName(p)!)).ToList();
        var effect = CurrentEffect;
        int level = _level.Value;
        SetBusy(true);
        var errors = new List<string>();
        int ok = 0;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var (src, folder) = targets[i];
                _status.ForeColor = Theme.Current.Text;
                _status.Text = $"保存中 {i + 1} / {targets.Count}: {Path.GetFileName(src)}";
                try
                {
                    await Task.Run(() => Masker.MaskCarried(src, from.Rects, from.Width, from.Height, effect, level,
                        overwrite ? src : Masker.OutputPathFor(src, folder, effect)));
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
            MessageBox.Show(this, string.Join("\n", errors.Take(15)), $"かけられなかった画像（{errors.Count} 枚）", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (overwrite && ok > 0 && _index >= 0)
        {
            _rectsByIndex.Clear(); // 上書きした画像に覚えていた範囲は、もう使わない
            await ShowIndexAsync(_index, carry: false); // 表示中の画像もかけた後のものにする
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
