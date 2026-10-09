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

    private readonly ImageStepper _stepper;  // 画像送りと、表示中の画像（原寸。切り抜き元）
    private readonly EditSavePanel _saver;   // 保存先・保存のボタン・保存中の制御
    private readonly EditCanvas _canvas = new();
    private Bitmap? _display;                // キャンバスの大きさに縮小した表示用

    // 切り抜き枠（画像座標 x0, y0, x1, y1）とドラッグの状態
    private double[]? _rect;
    // 引き継ぐ元の枠: 最後に自分で決めた（動かした・大きさを変えた・比を変えて作り直した）枠と、その画像の大きさ。
    // 前の画像からではなくここから引き継ぐので、大きさの違う画像を行き来しても枠がずれていかない
    private ((double, double, double, double) Rect, int Width, int Height)? _anchor;
    private enum DragMode { None, Move, Resize }
    private DragMode _mode;
    private (double X, double Y) _fixed;   // 大きさを変えるときに動かない角
    private (double X, double Y) _moveOff;

    private readonly List<(RadioButton Radio, double? Ratio)> _aspectRadios = new();
    private readonly RadioButton _customAspect = new() { Text = "指定", AutoSize = true };
    private readonly NumericUpDown _customW = new() { Minimum = 1, Maximum = 999, Width = 52, TextAlign = HorizontalAlignment.Right };
    private readonly NumericUpDown _customH = new() { Minimum = 1, Maximum = 999, Width = 52, TextAlign = HorizontalAlignment.Right };
    private readonly CheckBox _flip = new() { Text = "縦横を入れ替え（4:3 → 3:4）", AutoSize = true };
    private readonly CheckBox _keepPosition = new() { Text = "次の画像も同じ位置で切り抜く", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly Label _selectionSize = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly ToolTip _toolTip = new();
    private const string CarriedNoticePrefix = "枠を決めた画像";

    /// <summary>保存したファイルの数（一覧の読み直しの判断用）</summary>
    public int SavedCount => _saver.SavedCount;

    public CropDialog(IReadOnlyList<string> paths, int startIndex = 0)
    {
        _stepper = new ImageStepper(paths);
        _stepper.StepRequested += async delta => await StepAsync(delta);
        _saver = new EditSavePanel(this, paths.Count,
            "「保存」は「元の名前_crop」で保存します（同名があれば (2) などを付けます）。" +
            "「上書き保存」は元のファイルを置き換えます（元には戻せません。HEIC・RAW など書き出せない形式やアニメーションは上書きしません）",
            _lastFolder, _lastToCustomFolder, withSaveAll: true);
        _saver.SaveRequested += async (advance, overwrite) => await SaveCurrentAsync(advance, overwrite);
        _saver.SaveAllRequested += async overwrite => await SaveAllAsync(overwrite);
        _saver.BusyChanged += UpdateButtons;

        _canvas.Paint += Canvas_Paint;
        _canvas.MouseDown += Canvas_MouseDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseUp += (_, _) => _mode = DragMode.None;
        _canvas.Settled += (_, _) => RebuildDisplay();
        EditDialogShell.Setup(this, "切り抜き", _stepper, _canvas, BuildSidePanel(), new Size(700, 520));

        _customW.Value = _lastCustomW;
        _customH.Value = _lastCustomH;
        (_lastAspect < _aspectRadios.Count ? _aspectRadios[_lastAspect].Radio : _customAspect).Checked = true;
        _flip.Checked = _lastFlip;
        _keepPosition.Checked = _lastKeepPosition;
        _keepPosition.Visible = paths.Count > 1;
        _keepPosition.CheckedChanged += (_, _) => UpdateButtons();
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
            _stepper.Dispose();
            _display?.Dispose();
            _toolTip.Dispose();
            _lastAspect = _aspectRadios.FindIndex(a => a.Radio.Checked) is int i and >= 0 ? i : _aspectRadios.Count;
            _lastCustomW = _customW.Value;
            _lastCustomH = _customH.Value;
            _lastFlip = _flip.Checked;
            _lastKeepPosition = _keepPosition.Checked;
            _lastFolder = _saver.FolderText;
            _lastToCustomFolder = _saver.ToCustom;
        };
    }

    private Control BuildSidePanel()
    {
        var side = EditDialogShell.SidePanel(230);

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

        side.Controls.Add(_saver.FolderBox);
        side.Controls.AddRange(_saver.Buttons);
        return side;
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

    /// <param name="carry">false なら枠を引き継がずに作り直す（上書きして読み直すとき。引き継ぐ元の枠はそのまま）</param>
    private async Task ShowIndexAsync(int index, bool carry = true)
    {
        var outcome = await _stepper.ShowAsync(index, UpdateButtons);
        if (outcome == ImageStepper.Outcome.Superseded) return;
        if (outcome == ImageStepper.Outcome.Failed)
        {
            _rect = null;
            RebuildDisplay();
            UpdateButtons();
            return;
        }
        var image = _stepper.Image!;
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
                _saver.ShowNotice($"{CarriedNoticePrefix}（{from.Width} × {from.Height}）と大きさが違うので、枠は割合で合わせました");
            else
                _saver.ClearNotice(CarriedNoticePrefix); // 前の画像で出したお知らせは消す
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
        _saver.SaveEnabled = !_saver.Busy && _stepper.Ready;
        // 位置を引き継ぐなら「今の範囲で」（自由な比でもよい）、そうでなければ「同じ比で中央から」
        _saver.SetSaveAll(visible: true,
            enabled: !_saver.Busy && !_stepper.Loading && (_keepPosition.Checked ? _anchor != null : CurrentAspect() != null),
            _keepPosition.Checked ? "全部を今の範囲で切り抜き:" : "全部を同じ比で中央から切り抜き:");
        _stepper.StepEnabled = !_saver.Busy;
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
        if (_stepper.Image == null) return;
        ResetRect();
        SetAnchor();
        _canvas.Invalidate();
    }

    /// <summary>今のアスペクト比で、画像の中央に最大の枠を作る（自由なら全体）</summary>
    private void ResetRect()
    {
        if (_stepper.Image == null) return;
        if (CurrentAspect() is double aspect)
        {
            var (x, y, w, h) = Cropper.CenterRect(_stepper.Image.Width, _stepper.Image.Height, aspect);
            _rect = new[] { x, y, x + w, y + h };
        }
        else
        {
            _rect = new[] { 0.0, 0.0, _stepper.Image.Width, (double)_stepper.Image.Height };
        }
    }

    /// <summary>今の枠を、次の画像へ引き継ぐ元にする</summary>
    private void SetAnchor()
    {
        if (_rect != null && _stepper.Image != null) _anchor = ((_rect[0], _rect[1], _rect[2], _rect[3]), _stepper.Image.Width, _stepper.Image.Height);
    }

    // ---- 描画（重い縮小は RebuildDisplay だけ。ドラッグ中の描画は縮小済みの絵＋枠だけ） ----

    private void RebuildDisplay()
    {
        _display?.Dispose();
        _display = null;
        if (_stepper.Image != null)
        {
            var size = _canvas.FitImage(_stepper.Image.Width, _stepper.Image.Height);
            if (size.Width == _stepper.Image.Width && size.Height == _stepper.Image.Height)
                _display = ThumbnailGenerator.ToPArgbBitmap(_stepper.Image);
            else
            {
                using var small = _stepper.Image.Clone(x => x.Resize(size.Width, size.Height, KnownResamplers.Triangle));
                _display = ThumbnailGenerator.ToPArgbBitmap(small);
            }
        }
        _canvas.Invalidate();
    }

    private void Canvas_Paint(object? sender, PaintEventArgs e)
    {
        if (_display == null || _rect == null) return;
        var g = e.Graphics;
        _canvas.DrawDisplay(g, _display);

        var (x0, y0) = _canvas.ImageToCanvas(_rect[0], _rect[1]);
        var (x1, y1) = _canvas.ImageToCanvas(_rect[2], _rect[3]);
        float dx0 = _canvas.ImageOrigin.X, dy0 = _canvas.ImageOrigin.Y, dx1 = dx0 + _display.Width, dy1 = dy0 + _display.Height;
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
        _canvas.DrawHandles(g, Corners(_rect));

        _selectionSize.Text = $"範囲: {Math.Round(_rect[2] - _rect[0])} × {Math.Round(_rect[3] - _rect[1])} px";
    }

    private static (double X, double Y)[] Corners(double[] rect) =>
        new[] { (rect[0], rect[1]), (rect[2], rect[1]), (rect[2], rect[3]), (rect[0], rect[3]) };

    // ---- マウス ----

    private void Canvas_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_stepper.Image == null || _rect == null || e.Button != MouseButtons.Left) return;
        _canvas.Focus();
        var corners = Corners(_rect);
        for (int i = 0; i < 4; i++)
        {
            if (!_canvas.HitsHandle(e.X, e.Y, corners[i])) continue;
            _mode = DragMode.Resize;
            _fixed = corners[(i + 2) % 4]; // 対角を固定
            return;
        }
        // 枠の中なら移動、外なら新しく作る
        var (ix, iy) = _canvas.CanvasToImage(e.X, e.Y);
        if (_rect[0] <= ix && ix <= _rect[2] && _rect[1] <= iy && iy <= _rect[3])
        {
            _mode = DragMode.Move;
            _moveOff = (ix - _rect[0], iy - _rect[1]);
        }
        else
        {
            _fixed = _canvas.ClampToImage((ix, iy));
            _mode = DragMode.Resize;
        }
    }

    private void Canvas_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_mode == DragMode.None || _stepper.Image == null || _rect == null) return;
        if (_mode == DragMode.Move) DoMove(e);
        else DoResize(e);
        SetAnchor();
        _canvas.Invalidate();
        _canvas.Update(); // マウスの移動が続いている間も、動かすたびに描き直す（描き直しは移動より後回しにされるので）
    }

    private void DoMove(MouseEventArgs e)
    {
        var (ix, iy) = _canvas.CanvasToImage(e.X, e.Y);
        double rw = _rect![2] - _rect[0], rh = _rect[3] - _rect[1];
        double x0 = Math.Clamp(ix - _moveOff.X, 0, _stepper.Image!.Width - rw);
        double y0 = Math.Clamp(iy - _moveOff.Y, 0, _stepper.Image.Height - rh);
        _rect = new[] { x0, y0, x0 + rw, y0 + rh };
    }

    private void DoResize(MouseEventArgs e)
    {
        int imgW = _stepper.Image!.Width, imgH = _stepper.Image.Height;
        var (fx, fy) = _fixed;
        var (mx, my) = _canvas.ClampToImage(_canvas.CanvasToImage(e.X, e.Y));
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

    /// <param name="overwrite">true なら元の画像を置き換える（確かめない）</param>
    private async Task SaveCurrentAsync(bool advance, bool overwrite)
    {
        if (_saver.Busy || !_stepper.Ready || _rect == null) return;
        string src = _stepper.CurrentPath;
        if (_saver.FolderFor(src, overwrite) is not string folder) return;
        var image = _stepper.Image!;
        var box = Cropper.ClampBox(_rect[0], _rect[1], _rect[2], _rect[3], image.Width, image.Height);
        if (box.Width < 1 || box.Height < 1) return;

        int index = _stepper.Index;
        bool saved = await _saver.SaveOneAsync(
            () => Cropper.SaveCrop(image, box, src, overwrite ? src : Cropper.OutputPathFor(src, folder)),
            overwrite, detail: $"（{box.Width} × {box.Height}）");
        if (!saved) return;
        if (advance)
        {
            if (index + 1 < _stepper.Count) await ShowIndexAsync(index + 1);
            else Close(); // 最後の 1 枚を保存したら閉じる
        }
        else if (overwrite)
        {
            await ShowIndexAsync(index, carry: false); // 切り抜いた後の画像を出し直す
        }
    }

    /// <summary>位置を引き継ぐなら最後に決めた枠を全部へ（大きさが違う画像には割合で合わせる）、そうでなければ同じ比で中央から切り抜く</summary>
    private async Task SaveAllAsync(bool overwrite)
    {
        if (_saver.Busy || _stepper.Loading) return;
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
    private async Task SaveAllAsync(Func<string, string, string> crop, bool overwrite)
    {
        int ok = await _saver.SaveAllAsync(_stepper.Paths, (src, folder) => crop(src, overwrite ? src : Cropper.OutputPathFor(src, folder)),
            overwrite, "切り抜き中", "切り抜けなかった画像");
        if (overwrite && ok > 0 && _stepper.Index >= 0) await ShowIndexAsync(_stepper.Index, carry: false); // 表示中の画像も切り抜いた後のものにする
    }
}
