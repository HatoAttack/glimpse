// モザイク・ぼかしダイアログ（切り抜きダイアログと同じ作り）
// - 画像の上をドラッグで範囲を選ぶ（いくつでも）。形は 四角 / 円・楕円 / 自由（ドラッグでなぞった形）。
//   範囲の中をドラッグで移動、四隅で大きさを変える（自由な形も外枠ごと伸び縮みする）。選んだ範囲は Delete か右クリックで消す
// - モザイク / ぼかし / ボックスぼかし / 塗りつぶし（黒）と強さ（1〜10。塗りつぶしは使わない）。表示はかけた後の見た目（縮小した画像にかけるので、細かい所は保存したものと少し違う）
// - 選択した画像を ◀ ▶（PageUp / PageDown）で切り替え。Enter で保存して次へ。範囲は画像ごとに覚える
// - 「次の画像も同じ範囲にかける」なら、切り替えても範囲を引き継ぐ（大きさが違う画像には割合で合わせる）。
//   一括の「全部に同じ範囲でかける」も出る（スクリーンショットの同じ所を隠すとき向け）
// - 「保存」は元の画像を残して別の名前で、「上書き保存」は元の画像を置き換える（確かめない。アニメ・書き出せない形式などは上書きしない）
// - 持つのは表示中の 1 枚（原寸）と、画面の大きさに縮小した画像（かける前）と表示用のビットマップだけ
using System.Drawing.Drawing2D;
using ImageViewer.Core.Editing;
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

    private readonly ImageStepper _stepper;  // 画像送りと、表示中の画像（原寸。保存元）
    private readonly EditSavePanel _saver;   // 保存先・保存のボタン・保存中の制御
    private readonly EditCanvas _canvas = new();
    private SixLabors.ImageSharp.Image<Rgba32>? _small; // キャンバスの大きさに縮小した、かける前の画像
    private Bitmap? _display;                           // _small にかけた表示用

    // 範囲（画像座標）。後のものほど上（クリックで先に当たる）
    private List<MaskRegion> _rects => _edits.Current;
    private int _selected = -1;
    // 「同じ範囲にかける」がオフのときに、画像ごとに選んだ範囲（行き来しても消えないように）
    private readonly PendingEdits<MaskRegion> _edits = new();
    // 引き継ぐ元: 最後に自分で範囲を変えた画像の範囲と、その画像の大きさ
    private (List<MaskRegion> Rects, int Width, int Height)? _anchor;
    private enum DragMode { None, Move, Resize, Draw }
    private DragMode _mode;
    private bool _creating;                // 新しく作っている範囲（小さすぎれば離したときに消す）
    private (double X, double Y) _fixed;   // 大きさを変えるときに動かない角
    private (double X, double Y) _moveOff;
    private List<(double X, double Y)>? _path; // 自由な形をなぞっている途中の点（画像座標）

    private readonly RadioButton _shapeRect = new() { Text = "四角", AutoSize = true };
    private readonly RadioButton _shapeEllipse = new() { Text = "円・楕円", AutoSize = true };
    private readonly RadioButton _shapeFree = new() { Text = "自由（なぞる）", AutoSize = true };
    private readonly RadioButton _mosaic = new() { Text = "モザイク", AutoSize = true };
    private readonly RadioButton _blur = new() { Text = "ぼかし", AutoSize = true };
    private readonly RadioButton _boxBlur = new() { Text = "ボックスぼかし", AutoSize = true };
    private readonly RadioButton _fill = new() { Text = "塗りつぶし（黒）", AutoSize = true };
    private readonly TrackBar _level = new()
    {
        Minimum = Masker.MinLevel, Maximum = Masker.MaxLevel, TickFrequency = 1, LargeChange = 1, Width = 180, AutoSize = false, Height = 32,
    };
    private readonly Label _levelText = new() { AutoSize = true };
    private readonly Button _removeSelected = new() { Text = "選んだ範囲を消す (Del)", Width = 200, Height = 26 };
    private readonly Button _removeAll = new() { Text = "範囲をすべて消す", Width = 200, Height = 26 };
    private readonly CheckBox _keepPosition = new() { Text = "次の画像も同じ範囲にかける", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly Label _selectionInfo = new() { AutoSize = true, MaximumSize = new Size(210, 0), Margin = new Padding(3, 8, 3, 3) };
    private readonly ToolTip _toolTip = new();
    private const string CarriedNoticePrefix = "範囲を決めた画像";

    /// <summary>保存したファイルの数（一覧の読み直しの判断用）</summary>
    public int SavedCount => _saver.SavedCount;

    public MaskDialog(IReadOnlyList<string> paths, int startIndex = 0)
    {
        _stepper = new ImageStepper(paths);
        _stepper.StepRequested += async delta => await StepAsync(delta);
        _saver = new EditSavePanel(this, paths.Count,
            "「保存」は「元の名前_mosaic」（ぼかしは _blur、ボックスぼかしは _boxblur、塗りつぶしは _fill）で保存します（同名があれば (2) などを付けます）。" +
            "「上書き保存」は元のファイルを置き換えます（元には戻せません。HEIC・RAW など書き出せない形式やアニメーションは上書きしません）",
            _lastFolder, _lastToCustomFolder, withSaveAll: true);
        _saver.SaveRequested += async (advance, overwrite) => await SaveCurrentAsync(advance, overwrite);
        _saver.SaveAllRequested += async overwrite => await SaveAllCarriedAsync(overwrite);
        _saver.BusyChanged += UpdateButtons;

        _canvas.Paint += Canvas_Paint;
        _canvas.MouseDown += Canvas_MouseDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseUp += Canvas_MouseUp;
        _canvas.Settled += (_, _) => RebuildDisplay();
        EditDialogShell.Setup(this, "モザイク・ぼかし", _stepper, _canvas, BuildSidePanel(), new Size(700, 560));

        (_lastShape switch { MaskShape.Ellipse => _shapeEllipse, MaskShape.Freehand => _shapeFree, _ => _shapeRect }).Checked = true;
        (_lastEffect switch { MaskEffect.Blur => _blur, MaskEffect.BoxBlur => _boxBlur, MaskEffect.Fill => _fill, _ => _mosaic }).Checked = true;
        _level.Value = Math.Clamp(_lastLevel, Masker.MinLevel, Masker.MaxLevel);
        foreach (var rb in new[] { _mosaic, _blur, _boxBlur, _fill })
            rb.CheckedChanged += (_, _) => { if (rb.Checked) OnEffectChanged(); };
        _level.ValueChanged += (_, _) => OnEffectChanged();
        _keepPosition.Checked = _lastKeepPosition;
        _keepPosition.Visible = paths.Count > 1;
        _keepPosition.CheckedChanged += (_, _) =>
        {
            // オンにしたら、いま見えている範囲を引き継ぐ元にする（前に別の画像で決めた範囲を使わないように）
            if (_keepPosition.Checked) SetAnchor();
            UpdateButtons();
        };
        UpdateLevelText();
        UpdateButtons();

        Shown += async (_, _) => await ShowIndexAsync(Math.Clamp(startIndex, 0, paths.Count - 1));
        FormClosed += (_, _) =>
        {
            _stepper.Dispose();
            _small?.Dispose();
            _display?.Dispose();
            _toolTip.Dispose();
            _lastEffect = CurrentEffect;
            _lastShape = CurrentShape;
            _lastLevel = _level.Value;
            _lastKeepPosition = _keepPosition.Checked;
            _lastFolder = _saver.FolderText;
            _lastToCustomFolder = _saver.ToCustom;
        };
    }

    private MaskEffect CurrentEffect =>
        _blur.Checked ? MaskEffect.Blur : _boxBlur.Checked ? MaskEffect.BoxBlur : _fill.Checked ? MaskEffect.Fill : MaskEffect.Mosaic;

    /// <summary>これから作る範囲の形（作った範囲の形は変えない）</summary>
    private MaskShape CurrentShape => _shapeEllipse.Checked ? MaskShape.Ellipse : _shapeFree.Checked ? MaskShape.Freehand : MaskShape.Rectangle;

    private Control BuildSidePanel()
    {
        var side = EditDialogShell.SidePanel(230);
        side.Controls.Add(EditDialogShell.Group("範囲の形", vertical: true, _shapeRect, _shapeEllipse, _shapeFree));
        _toolTip.SetToolTip(_shapeFree, "隠したい所のまわりをドラッグでなぞると、その形の範囲になります（離すと始点と終点をつなぎます）");

        var effectRow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        effectRow.Controls.AddRange(new Control[] { _mosaic, _blur, _boxBlur, _fill });
        _toolTip.SetToolTip(_fill, "範囲を黒一色で塗ります。文字や番号を確実に読めなくしたいとき向けです");
        side.Controls.Add(EditDialogShell.Group("かけ方", vertical: true, effectRow, _levelText, _level));

        _removeSelected.Click += (_, _) => RemoveSelected();
        _removeAll.Click += (_, _) => RemoveAll();
        side.Controls.AddRange(new Control[] { _selectionInfo, _removeSelected, _removeAll, _keepPosition });
        _toolTip.SetToolTip(_keepPosition, "スクリーンショットのように、大きさと構成が同じ画像の同じ所を隠すとき用です。\n" +
                                           "大きさが違う画像には、画像に対する割合で合わせます");

        side.Controls.Add(_saver.FolderBox);
        side.Controls.AddRange(_saver.Buttons);
        return side;
    }

    private void OnEffectChanged()
    {
        UpdateLevelText();
        RenderPreview();
    }

    private void UpdateLevelText()
    {
        // 塗りつぶしは強さを使わない
        _level.Enabled = CurrentEffect != MaskEffect.Fill;
        if (!_level.Enabled)
        {
            _levelText.Text = "強さ: （塗りつぶしでは使いません）";
            return;
        }
        string size = _stepper.Image == null ? "" : CurrentEffect == MaskEffect.Mosaic
            ? $"（1 マス {Masker.EffectSize(_stepper.Image.Width, _stepper.Image.Height, _level.Value)} px）"
            : "";
        _levelText.Text = $"強さ: {_level.Value}{size}";
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

    /// <param name="carry">false なら範囲を引き継がずに空にする（上書きして読み直すとき。引き継ぐ元の範囲はそのまま）</param>
    private async Task ShowIndexAsync(int index, bool carry = true)
    {
        if (_stepper.Ready) _edits.Remember(_stepper.Index);
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
        if (!carry)
        {
            _edits.Discard(index);
        }
        else if (_keepPosition.Checked && _anchor is { } from)
        {
            _edits.Set(Masker.CarryRegions(from.Rects, from.Width, from.Height, image.Width, image.Height));
            bool resized = from.Width != image.Width || from.Height != image.Height;
            if (resized && _rects.Count > 0)
                _saver.ShowNotice($"{CarriedNoticePrefix}（{from.Width} × {from.Height}）と大きさが違うので、範囲は割合で合わせました");
            else
                _saver.ClearNotice(CarriedNoticePrefix); // 前の画像で出したお知らせは消す
        }
        else
        {
            _edits.Recall(index);
        }
        UpdateLevelText();
        RebuildDisplay();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool ready = !_saver.Busy && _stepper.Ready;
        _saver.SaveEnabled = ready && _rects.Count > 0;
        _saver.SetSaveAll(visible: _stepper.Count > 1 && _keepPosition.Checked, enabled: ready && _rects.Count > 0, "全部に同じ範囲でかける:");
        _removeSelected.Enabled = !_saver.Busy && _selected >= 0;
        _removeAll.Enabled = !_saver.Busy && _rects.Count > 0;
        _stepper.StepEnabled = !_saver.Busy;
        _selectionInfo.Text = _stepper.Image == null ? ""
            : _rects.Count == 0 ? "画像の上をドラッグして、隠す範囲を選んでください（いくつでも選べます）"
            : _selected >= 0
                ? $"範囲 {_rects.Count} 個（選んだ範囲: {Math.Round(_rects[_selected].Width)} × {Math.Round(_rects[_selected].Height)} px）"
                : $"範囲 {_rects.Count} 個";
    }

    /// <summary>今の範囲を、次の画像へ引き継ぐ元にする</summary>
    private void SetAnchor()
    {
        if (_stepper.Image != null)
            _anchor = (_rects.ToList(), _stepper.Image.Width, _stepper.Image.Height);
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
        if (_saver.Busy || _selected < 0 || _selected >= _rects.Count) return;
        _rects.RemoveAt(_selected);
        _selected = -1;
        OnRectsChanged();
    }

    private void RemoveAll()
    {
        if (_saver.Busy || _rects.Count == 0) return;
        _rects.Clear();
        _selected = -1;
        OnRectsChanged();
    }

    // ---- 描画（重い縮小は RebuildDisplay だけ。範囲を変えたときは縮小済みの画像にかけ直すだけ） ----

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

    /// <summary>縮小した画像に、今の範囲・かけ方・強さ（表示の倍率に合わせる）でかけて、表示用のビットマップを作り直す</summary>
    private void RenderPreview()
    {
        _display?.Dispose();
        _display = null;
        if (_small != null && _stepper.Image != null)
        {
            var regions = _rects.Select(r => r.Scale(_canvas.Zoom, _canvas.Zoom)).ToList();
            if (regions.Count == 0)
            {
                _display = ThumbnailGenerator.ToPArgbBitmap(_small);
            }
            else
            {
                int size = (int)Math.Round(Masker.EffectSize(_stepper.Image.Width, _stepper.Image.Height, _level.Value) * _canvas.Zoom);
                using var preview = _small.Clone();
                try
                {
                    Masker.Apply(preview, regions, CurrentEffect, Math.Max(2, size));
                }
                catch (ArgumentException)
                {
                    // 見た目だけのプレビューなので、かけられなかったら（極端に小さい範囲など）かける前のまま出す。アプリは止めない
                }
                _display = ThumbnailGenerator.ToPArgbBitmap(preview);
            }
        }
        _canvas.Invalidate();
    }

    private void Canvas_Paint(object? sender, PaintEventArgs e)
    {
        if (_display == null) return;
        var g = e.Graphics;
        _canvas.DrawDisplay(g, _display);

        using var frame = new Pen(Color.FromArgb(200, 255, 255, 255), 1) { DashPattern = new[] { 4f, 3f } };
        using var selectedFrame = new Pen(Color.White, 1);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < _rects.Count; i++)
        {
            var r = _rects[i];
            var (x0, y0) = _canvas.ImageToCanvas(r.X0, r.Y0);
            var (x1, y1) = _canvas.ImageToCanvas(r.X1, r.Y1);
            float fx0 = (float)x0, fy0 = (float)y0, fx1 = (float)x1, fy1 = (float)y1;
            var pen = i == _selected ? selectedFrame : frame;
            switch (r.Shape)
            {
                case MaskShape.Ellipse:
                    g.DrawEllipse(pen, fx0, fy0, fx1 - fx0, fy1 - fy0);
                    break;
                case MaskShape.Freehand:
                    var polygon = r.Polygon().Select(p => _canvas.ToCanvasPoint(p.X, p.Y)).ToArray();
                    if (polygon.Length >= 2) g.DrawPolygon(pen, polygon);
                    break;
                default:
                    g.DrawRectangle(pen, fx0, fy0, fx1 - fx0, fy1 - fy0);
                    break;
            }
            if (i != _selected) continue;
            // 選んだ範囲だけ四隅のハンドルを出す（四角以外は外枠も薄く出して、どこを引っ張れば伸びるか分かるように）
            if (r.Shape != MaskShape.Rectangle) g.DrawRectangle(frame, fx0, fy0, fx1 - fx0, fy1 - fy0);
            _canvas.DrawHandles(g, CornersOf(r));
        }
        // なぞっている途中の線
        if (_path is { Count: >= 2 } path)
            g.DrawLines(selectedFrame, path.Select(p => _canvas.ToCanvasPoint(p.X, p.Y)).ToArray());
    }

    private static (double X, double Y)[] CornersOf(MaskRegion r) => new[] { (r.X0, r.Y0), (r.X1, r.Y0), (r.X1, r.Y1), (r.X0, r.Y1) };

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
        if (_stepper.Image == null || _saver.Busy || _stepper.Loading) return;
        _canvas.Focus();
        var (ix, iy) = _canvas.CanvasToImage(e.X, e.Y);

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
            var corners = CornersOf(_rects[_selected]);
            for (int i = 0; i < 4; i++)
            {
                if (!_canvas.HitsHandle(e.X, e.Y, corners[i])) continue;
                _mode = DragMode.Resize;
                _creating = false;
                _fixed = corners[(i + 2) % 4]; // 対角を固定
                return;
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
            _path = new() { _canvas.ClampToImage((ix, iy)) };
            _mode = DragMode.Draw;
        }
        else
        {
            _fixed = _canvas.ClampToImage((ix, iy));
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
        if (_mode == DragMode.Draw && _path != null && _stepper.Image != null)
        {
            // 画面で 2px 以上動いたら点を足す（点が多すぎると重くなるだけなので）
            var last = _canvas.ImageToCanvas(_path[^1].X, _path[^1].Y);
            if (Math.Abs(e.X - last.X) + Math.Abs(e.Y - last.Y) < 2) return;
            _path.Add(_canvas.ClampToImage(_canvas.CanvasToImage(e.X, e.Y)));
            _canvas.Invalidate();
            _canvas.Update(); // 下の移動・大きさの変更と同じ理由
            return;
        }
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
        var (ix, iy) = _canvas.CanvasToImage(e.X, e.Y);
        var r = _rects[_selected];
        double rw = r.Width, rh = r.Height;
        double x0 = Math.Clamp(ix - _moveOff.X, 0, _stepper.Image!.Width - rw);
        double y0 = Math.Clamp(iy - _moveOff.Y, 0, _stepper.Image.Height - rh);
        _rects[_selected] = r.WithBounds(x0, y0, x0 + rw, y0 + rh);
    }

    private void DoResize(MouseEventArgs e)
    {
        var (fx, fy) = _fixed;
        var (mx, my) = _canvas.ClampToImage(_canvas.CanvasToImage(e.X, e.Y));
        double x0 = Math.Min(fx, mx), x1 = Math.Max(fx, mx), y0 = Math.Min(fy, my), y1 = Math.Max(fy, my);
        // 作っている途中は小さくてもよい（離したときに確かめる）。作った範囲を小さくしすぎることはできない
        if (_creating || (x1 - x0 >= MinSizePx && y1 - y0 >= MinSizePx)) _rects[_selected] = _rects[_selected].WithBounds(x0, y0, x1, y1);
    }

    // ---- 保存 ----

    /// <param name="overwrite">true なら元の画像を置き換える（確かめない）</param>
    private async Task SaveCurrentAsync(bool advance, bool overwrite)
    {
        if (_saver.Busy || !_stepper.Ready) return;
        string src = _stepper.CurrentPath;
        var regions = _rects.ToList();
        if (regions.Count == 0)
        {
            _saver.ShowNotice("隠す範囲を選んでから保存してください");
            return;
        }
        if (_saver.FolderFor(src, overwrite) is not string folder) return;

        var image = _stepper.Image!;
        var effect = CurrentEffect;
        int level = _level.Value;
        int index = _stepper.Index;
        bool saved = await _saver.SaveOneAsync(
            () => Masker.SaveMasked(image, regions, effect, level, src, overwrite ? src : Masker.OutputPathFor(src, folder, effect)),
            overwrite, saved: () =>
            {
                if (!overwrite) return;
                // 元の画像にはもうかけてあるので、覚えている範囲は捨てる（次の画像へ移ってから戻ってきたときに二重にかけないように）。
                // 引き継ぐ元（_anchor）は残すので、「次の画像も同じ範囲にかける」はそのまま続けられる
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
            await ShowIndexAsync(index, carry: false); // かけた後の画像を出し直す（同じ範囲に二重にかけないよう範囲は空に）
        }
    }

    /// <summary>
    /// いま表示している画像の範囲を全部の画像へ引き継いでかける（大きさが違う画像には割合で合わせる）。
    /// 引き継ぐ元（_anchor）ではなく見えている範囲を使うので、見えていない範囲にかけてしまうことはない
    /// </summary>
    private async Task SaveAllCarriedAsync(bool overwrite)
    {
        if (_saver.Busy || !_stepper.Ready || _rects.Count == 0) return;
        var from = (Rects: _rects.ToList(), Width: _stepper.Image!.Width, Height: _stepper.Image.Height);
        var effect = CurrentEffect;
        int level = _level.Value;
        int ok = await _saver.SaveAllAsync(_stepper.Paths,
            (src, folder) => Masker.MaskCarried(src, from.Rects, from.Width, from.Height, effect, level,
                overwrite ? src : Masker.OutputPathFor(src, folder, effect)),
            overwrite, "保存中", "かけられなかった画像");
        if (overwrite && ok > 0 && _stepper.Index >= 0)
        {
            _edits.DiscardAll(); // 上書きした画像に覚えていた範囲は、もう使わない
            _selected = -1;
            await ShowIndexAsync(_stepper.Index, carry: false); // 表示中の画像もかけた後のものにする
        }
    }
}
