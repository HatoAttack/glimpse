// 枠・矢印・文字ダイアログ（モザイク・ぼかしダイアログと同じ作り）
// - 画像の上をドラッグで四角の枠か矢印を描く（いくつでも）。矢印は離した所が先端。Shift を押しながらで 45° ごとの向きにそろえる
// - 文字と番号はクリックした所に置く。文字は右の欄に打つ（背景は なし / 帯 / 吹き出し）。番号は置くたびに 1 ずつ増える。
//   吹き出しは、置く所から指したい所へドラッグする（矢印と同じく、離した所がしっぽの先）
// - 線の上（文字・番号は本体）をクリックで選ぶ。選んだものはドラッグで移動、枠は四隅・矢印は両端で形を変える。
//   文字は四隅で本体の大きさを変える（文字が収まる大きさより小さくはならない。文字の大きさは右の欄で変える）。番号は四隅で大きさを変える。
//   吹き出しはしっぽの先も動かせる（本体を動かしても、しっぽの先は指している所に残る）。Delete か右クリックで消す
// - 色・太さ・角の丸み・先端の大きさ・影・文字の大きさなどは、選んだものがあればそれを変え、無ければ次に描くものの見た目になる
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
    private const int MinFontSize = 8, MaxFontSize = 400; // 文字の大きさ（画像の px）
    private const string DefaultText = "テキスト";       // 何も打たずに文字を置いたときの中身（置いたらすぐ打ち直せる）

    /// <summary>このアプリを起動している間は前回の設定を引き継ぐ（太さが 0 のうちは、初めの画像の大きさから決める）</summary>
    private static AnnotationKind _lastKind = AnnotationKind.Frame;
    private static Color _lastColor = Color.FromArgb(230, 30, 30);
    private static int _lastThickness, _lastRadius;
    private static int _lastHead = (int)(Annotator.DefaultHeadSize * HeadScale);
    private static int _lastFontSize;
    private static TextBackground _lastBackground = TextBackground.None;
    private static Color _lastFill = Color.White;
    private static Color _lastTextColor = Color.FromArgb(230, 30, 30);
    private static string _lastFont = "";
    private static TextLineAlign _lastAlign = TextLineAlign.Center;
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
    private enum DragMode { None, Move, Resize, Box, Scale, Tail } // Box: 文字の本体の大きさ、Scale: 番号の大きさ、Tail: 吹き出しのしっぽの先
    private DragMode _mode;
    private bool _creating;                // 新しく描いている途中（小さすぎれば離したときに消す）
    private (double X, double Y) _fixed;   // 形を変えるときに動かない点（枠・文字は対角、矢印は反対の端）
    private bool _fixedRight, _fixedBottom; // 文字・番号: 動かない角が本体の右 / 下か
    private bool _dragTip;                 // 矢印: 動かしているのが先端か
    private bool _focusText;               // マウスを離したら、文字の欄へ移って打てるようにする
    private (double X, double Y) _moveLast;
    private Color _color = _lastColor;
    private Color _fill = _lastFill;
    private Color _textColor = _lastTextColor;
    private bool _syncing;                 // 選んだものの見た目を部品へ写している途中（変更として扱わない）

    private readonly RadioButton _toolFrame = new() { Text = "枠", AutoSize = true };
    private readonly RadioButton _toolArrow = new() { Text = "矢印", AutoSize = true };
    private readonly RadioButton _toolText = new() { Text = "文字", AutoSize = true };
    private readonly RadioButton _toolNumber = new() { Text = "番号", AutoSize = true };
    private readonly TextBox _text = new() { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Width = 186, Height = 40, MaxLength = 500 };
    private readonly Label _fontText = SliderLabel();
    private readonly TrackBar _font = Slider(MinFontSize, MaxFontSize);
    private readonly Label _backText = new() { Text = "背景:", AutoSize = true, Margin = new Padding(3, 6, 0, 3) };
    private readonly RadioButton _backNone = new() { Text = "なし", AutoSize = true };
    private readonly RadioButton _backBox = new() { Text = "帯", AutoSize = true };
    private readonly RadioButton _backBalloon = new() { Text = "吹き出し", AutoSize = true };
    private readonly Label _alignText = new() { Text = "行:", AutoSize = true, Margin = new Padding(3, 6, 0, 3) };
    private readonly RadioButton _alignLeft = new() { Text = "左", AutoSize = true };
    private readonly RadioButton _alignCenter = new() { Text = "中央", AutoSize = true };
    private readonly RadioButton _alignRight = new() { Text = "右", AutoSize = true };
    private readonly SwatchPanel _fillSwatch = new() { Width = 28, Height = 24, Margin = new Padding(8, 3, 2, 3), Cursor = Cursors.Hand };
    private readonly Button _pickFill = new() { Text = "中の色...", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly SwatchPanel _textSwatch = new() { Width = 28, Height = 24, Margin = new Padding(3, 3, 2, 3), Cursor = Cursors.Hand };
    private readonly Button _pickText = new() { Text = "文字...", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly ComboBox _fontBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 112, DropDownWidth = 240, MaxDropDownItems = 20, Margin = new Padding(8, 4, 3, 3) };
    private readonly SwatchPanel _swatch = new() { Width = 28, Height = 24, Margin = new Padding(3, 3, 2, 3), Cursor = Cursors.Hand };
    private readonly Button _pickColor = new() { Text = "色...", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Label _thicknessText = SliderLabel();
    private readonly TrackBar _thickness = Slider(1, 100);
    private readonly Label _radiusText = SliderLabel();
    private readonly TrackBar _radius = Slider(0, 200);
    private readonly Label _headText = SliderLabel();
    private readonly TrackBar _head = Slider((int)(Annotator.MinHeadSize * HeadScale), (int)(Annotator.MaxHeadSize * HeadScale));
    private readonly CheckBox _shadow = new() { Text = "影を付ける", AutoSize = true };
    private readonly Button _removeSelected = new() { Text = "選んだものを消す", Width = 112, Height = 26 };
    private readonly Button _removeAll = new() { Text = "すべて消す", Width = 82, Height = 26 };
    // 2 行分の高さに固定する（文の長さで下のボタンが上下しないように）
    private readonly Label _selectionInfo = new() { AutoSize = false, Size = new Size(210, 34), Margin = new Padding(3, 6, 3, 1) };
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
        Minimum = min, Maximum = max, TickStyle = TickStyle.None, LargeChange = Math.Max(1, (max - min) / 10), Width = 108, AutoSize = false, Height = 26,
    };

    /// <summary>スライダーの左に置く、項目の名前と今の値（縦に場所を取らないよう、スライダーと同じ行に並べる）</summary>
    private static Label SliderLabel() => new() { AutoSize = false, Width = 84, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 3, 0, 3) };

    public AnnotateDialog(IReadOnlyList<string> paths, int startIndex = 0)
    {
        _stepper = new ImageStepper(paths);
        _stepper.StepRequested += async delta => await StepAsync(delta);
        _saver = new EditSavePanel(this, paths.Count,
            "「保存」は「元の名前_mark」で別に、「上書き保存」は元に上書きします",
            _lastFolder, _lastToCustomFolder, withSaveAll: false);
        _saver.SaveRequested += async (advance, overwrite) => await SaveCurrentAsync(advance, overwrite);
        _saver.BusyChanged += UpdateButtons;

        _canvas.Paint += Canvas_Paint;
        _canvas.MouseDown += Canvas_MouseDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseUp += Canvas_MouseUp;
        _canvas.Settled += (_, _) => RebuildDisplay();
        EditDialogShell.Setup(this, "枠・矢印・文字", _stepper, _canvas, BuildSidePanel(), new Size(700, 560));

        _syncing = true;
        ToolButton(_lastKind).Checked = true;
        BackgroundButton(_lastBackground).Checked = true;
        AlignButton(_lastAlign).Checked = true;
        _swatch.Swatch = _color;
        _fillSwatch.Swatch = _fill;
        _textSwatch.Swatch = _textColor;
        _fontBox.Items.AddRange(Annotator.FontNames().ToArray<object>());
        SelectFont(_lastFont);
        _font.Value = Math.Clamp(Math.Max(MinFontSize, _lastFontSize), _font.Minimum, _font.Maximum);
        _thickness.Value = Math.Clamp(Math.Max(1, _lastThickness), _thickness.Minimum, _thickness.Maximum);
        _radius.Value = Math.Clamp(_lastRadius, _radius.Minimum, _radius.Maximum);
        _head.Value = Math.Clamp(_lastHead, _head.Minimum, _head.Maximum);
        _shadow.Checked = _lastShadow;
        _syncing = false;
        foreach (var rb in new[] { _toolFrame, _toolArrow, _toolText, _toolNumber })
            rb.CheckedChanged += (_, _) => { if (rb.Checked) UpdateStyleControls(); };
        foreach (var rb in new[] { _backNone, _backBox, _backBalloon, _alignLeft, _alignCenter, _alignRight })
            rb.CheckedChanged += (_, _) => { if (rb.Checked) OnStyleChanged(); };
        foreach (var slider in new[] { _thickness, _radius, _head, _font })
            slider.ValueChanged += (_, _) => OnStyleChanged();
        _shadow.CheckedChanged += (_, _) => OnStyleChanged();
        _text.TextChanged += (_, _) => OnStyleChanged();
        _fontBox.SelectedIndexChanged += (_, _) => OnStyleChanged();
        foreach (var (swatch, button) in new[] { (_swatch, _pickColor), (_fillSwatch, _pickFill), (_textSwatch, _pickText) })
        {
            swatch.Click += (_, _) => PickColor(swatch);
            button.Click += (_, _) => PickColor(swatch);
        }
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
            _lastFill = _fill;
            _lastTextColor = _textColor;
            _lastFont = _fontBox.SelectedItem as string ?? "";
            _lastBackground = CurrentBackground;
            _lastAlign = CurrentAlign;
            if (_styleReady)
            {
                _lastThickness = _thickness.Value;
                _lastRadius = _radius.Value;
                _lastFontSize = _font.Value;
            }
            _lastHead = _head.Value;
            _lastShadow = _shadow.Checked;
            _lastFolder = _saver.FolderText;
            _lastToCustomFolder = _saver.ToCustom;
        };
    }

    // 太さ・角の丸み・文字の大きさの初めの値を決めたか（前回の設定が無ければ、初めの画像を読んでから大きさに合わせて決める）
    private bool _styleReady = _lastThickness > 0;

    /// <summary>これから描くもの（描いたものの種類は変えない）</summary>
    private AnnotationKind CurrentTool =>
        _toolArrow.Checked ? AnnotationKind.Arrow : _toolText.Checked ? AnnotationKind.Text : _toolNumber.Checked ? AnnotationKind.Number : AnnotationKind.Frame;

    private TextBackground CurrentBackground => _backBalloon.Checked ? TextBackground.Balloon : _backBox.Checked ? TextBackground.Box : TextBackground.None;

    private RadioButton ToolButton(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Arrow => _toolArrow,
        AnnotationKind.Text => _toolText,
        AnnotationKind.Number => _toolNumber,
        _ => _toolFrame,
    };

    private TextLineAlign CurrentAlign => _alignLeft.Checked ? TextLineAlign.Left : _alignRight.Checked ? TextLineAlign.Right : TextLineAlign.Center;

    private RadioButton AlignButton(TextLineAlign align) => align == TextLineAlign.Left ? _alignLeft : align == TextLineAlign.Right ? _alignRight : _alignCenter;

    private RadioButton BackgroundButton(TextBackground background) =>
        background == TextBackground.Balloon ? _backBalloon : background == TextBackground.Box ? _backBox : _backNone;

    private Annotation? Selected => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    /// <summary>部品を横に並べる</summary>
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    private Control BuildSidePanel()
    {
        var side = EditDialogShell.SidePanel(250); // 縦のスクロールバーが出ても横にはみ出さない幅
        side.Controls.Add(EditDialogShell.Group("描くもの", vertical: false, _toolFrame, _toolArrow, _toolText, _toolNumber));
        _toolTip.SetToolTip(_toolArrow, "指したい所へ向かってドラッグします（離した所が先端）。\nShift を押しながらドラッグすると、向きを 45° ごとにそろえます");
        _toolTip.SetToolTip(_toolText, "クリックした所に文字を置きます（中身は下の欄に打ちます）。\n吹き出しは、置く所から指したい所へドラッグします（離した所がしっぽの先）");
        _toolTip.SetToolTip(_toolNumber, "クリックした所に、丸で囲んだ番号を置きます（置くたびに 1 ずつ増えます）");

        // 背景・行の寄せは、それぞれ別の入れ物に入れる（同じ入れ物のラジオボタンは 1 つしか選べないため）
        side.Controls.Add(EditDialogShell.Group("文字", vertical: true, _text, Row(_fontText, _font), Row(_backText, _backNone, _backBox, _backBalloon),
            Row(_alignText, _alignLeft, _alignCenter, _alignRight)));
        foreach (var c in new Control[] { _alignText, _alignLeft, _alignCenter, _alignRight })
            _toolTip.SetToolTip(c, "文字を本体の 左 / 中央 / 右 のどこに寄せるか（2 行以上なら行も同じ側にそろえます）");
        _toolTip.SetToolTip(_text, "選んだ文字の中身。Enter で改行します");
        _toolTip.SetToolTip(_backBox, "文字の後ろに角の丸い四角を置きます");
        _toolTip.SetToolTip(_backBalloon, "四角に、指したい所へ向かうしっぽを付けます");

        side.Controls.Add(EditDialogShell.Group("見た目", vertical: true, Row(_swatch, _pickColor, _fillSwatch, _pickFill), Row(_textSwatch, _pickText, _fontBox), Row(_thicknessText, _thickness), Row(_radiusText, _radius), Row(_headText, _head), _shadow));
        foreach (var c in new Control[] { _thicknessText, _thickness }) _toolTip.SetToolTip(c, "線の太さ（枠・矢印と、帯・吹き出しの縁）");
        foreach (var c in new Control[] { _radiusText, _radius }) _toolTip.SetToolTip(c, "枠・帯・吹き出しの角の丸み。短い辺の半分より大きくはなりません");
        foreach (var c in new Control[] { _headText, _head }) _toolTip.SetToolTip(c, "矢印の先端の大きさ（線の太さの何倍か）");
        foreach (var c in new Control[] { _fontText, _font }) _toolTip.SetToolTip(c, "文字・番号の大きさ（文字の四隅のドラッグは、文字でなく本体の大きさを変えます）");

        _removeSelected.Click += (_, _) => RemoveSelected();
        _removeAll.Click += (_, _) => RemoveAll();
        _toolTip.SetToolTip(_removeSelected, "選んだものを消します (Del)。右クリックでも消せます");
        _toolTip.SetToolTip(_pickColor, "枠・矢印・番号の丸と、帯・吹き出しの縁の色");
        _toolTip.SetToolTip(_pickFill, "帯・吹き出しの中の色");
        _toolTip.SetToolTip(_pickText, "文字の色（番号の数字は、丸の色に合わせて白か黒になります）");
        _toolTip.SetToolTip(_fontBox, "文字・番号の書体（太字がある書体は太字で描きます）");
        side.Controls.AddRange(new Control[] { _selectionInfo, Row(_removeSelected, _removeAll) });

        side.Controls.Add(_saver.FolderBox);
        side.Controls.AddRange(_saver.Buttons);
        return side;
    }

    // ---- 見た目（色・太さ・角の丸み・先端の大きさ・影・文字） ----

    private static Rgba32 ToRgba(Color c) => new(c.R, c.G, c.B, c.A);

    /// <summary>部品の今の値で、枠か矢印を作る</summary>
    private Annotation NewItem(AnnotationKind kind, double x0, double y0, double x1, double y1) =>
        Styled(new Annotation(kind, x0, y0, x1, y1, default, 0, 0, 0, false));

    /// <summary>部品の今の値を見た目に入れる（位置と種類はそのまま。番号の数字も変えない）</summary>
    private Annotation Styled(Annotation a) => a with
    {
        Color = ToRgba(_color), Thickness = _thickness.Value, CornerRadius = _radius.Value, HeadSize = (double)_head.Value / HeadScale, Shadow = _shadow.Checked,
        FontSize = _font.Value, Background = CurrentBackground, Fill = ToRgba(_fill), TextColor = ToRgba(_textColor), FontName = _fontBox.SelectedItem as string ?? "", Align = CurrentAlign,
        Text = a.Kind == AnnotationKind.Text ? _text.Text : a.Text,
    };

    /// <summary>見本（色・中の色・文字の色）の色を選び直す</summary>
    private void PickColor(SwatchPanel swatch)
    {
        using var dlg = new ColorDialog { Color = swatch.Swatch, FullOpen = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        SetSwatch(swatch, Color.FromArgb(255, dlg.Color));
        OnStyleChanged();
    }

    private void SetSwatch(SwatchPanel swatch, Color color)
    {
        if (swatch == _fillSwatch) _fill = color;
        else if (swatch == _textSwatch) _textColor = color;
        else _color = color;
        swatch.Swatch = color;
        swatch.Invalidate();
    }

    private static Color ToColor(Rgba32 c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>書体の一覧で name を選ぶ（一覧に無い・空なら、初めの書体）</summary>
    private void SelectFont(string name)
    {
        int index = _fontBox.FindStringExact(name.Length > 0 ? name : Annotator.DefaultFontName);
        if (index < 0) index = _fontBox.FindStringExact(Annotator.DefaultFontName);
        if (index >= 0) _fontBox.SelectedIndex = index;
    }

    /// <summary>見た目の部品を変えた: 選んだものがあればそれに反映する（無ければ次に描くものから使う）</summary>
    private void OnStyleChanged()
    {
        UpdateStyleControls();
        if (_syncing || _saver.Busy || Selected is not { } a) return;
        var styled = KeepInside(Styled(a));
        // しっぽを出し直すのは、背景を吹き出しに変えたときだけ（文字を足して本体が先を覆っても、指している所は変えない）
        _items[_selected] = a.HasTail ? styled : WithTail(styled);
        RenderPreview();
    }

    /// <summary>文字・番号の本体が画像からはみ出していたら中へ戻す（文字を足した・大きくした・背景を付けたときに、端で切れないように）</summary>
    private Annotation KeepInside(Annotation a)
    {
        if (!a.IsText || _stepper.Image is not { } image) return a;
        var (x, y, w, h) = a.Body;
        return a with { X0 = ClampInside(x, w, image.Width), Y0 = ClampInside(y, h, image.Height) };
    }

    /// <summary>吹き出しにした・クリックだけで置いたときに、しっぽの先が本体の中にあれば、本体の下（下に出せなければ上）へ出す</summary>
    private Annotation WithTail(Annotation a)
    {
        if (!a.HasTail || _stepper.Image is not { } image) return a;
        var (x, y, w, h) = a.Body;
        if (a.X1 < x || a.X1 > x + w || a.Y1 < y || a.Y1 > y + h) return a;
        double gap = a.FontSize * 1.2;
        var tip = _canvas.ClampToImage((x + w * 0.3, y + h + gap <= image.Height ? y + h + gap : y - gap));
        return a with { X1 = tip.X, Y1 = tip.Y };
    }

    /// <summary>選んだものの見た目を部品へ写す（続けて調整できるように）</summary>
    private void LoadStyleFrom(Annotation a)
    {
        _syncing = true;
        SetSwatch(_swatch, ToColor(a.Color));
        // 使わない値（枠の先端の大きさ・矢印の角の丸み・番号の太さ など）は、次に描くもののために今の部品の値を残す
        bool boxed = a.Kind == AnnotationKind.Text && a.Background != TextBackground.None;
        if (!a.IsText || boxed) _thickness.Value = Math.Clamp((int)Math.Round(a.Thickness), _thickness.Minimum, _thickness.Maximum);
        if (a.Kind == AnnotationKind.Frame || boxed) _radius.Value = Math.Clamp((int)Math.Round(a.CornerRadius), _radius.Minimum, _radius.Maximum);
        if (a.Kind == AnnotationKind.Arrow) _head.Value = Math.Clamp((int)Math.Round(a.HeadSize * HeadScale), _head.Minimum, _head.Maximum);
        if (a.IsText)
        {
            _font.Value = Math.Clamp((int)Math.Round(a.FontSize), _font.Minimum, _font.Maximum);
            SelectFont(a.FontName);
        }
        if (a.Kind == AnnotationKind.Text)
        {
            _text.Text = a.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            BackgroundButton(a.Background).Checked = true;
            AlignButton(a.Align).Checked = true;
            SetSwatch(_textSwatch, ToColor(a.TextColor ?? a.Color));
            if (boxed) SetSwatch(_fillSwatch, ToColor(a.Fill));
        }
        _shadow.Checked = a.Shadow;
        _syncing = false;
        UpdateStyleControls();
    }

    private void UpdateStyleControls()
    {
        // 選んだものがあればその種類、無ければこれから描くものの種類で使う部品を決める（背景の部品は、選んだ文字に合わせてある）
        var kind = Selected?.Kind ?? CurrentTool;
        bool text = kind == AnnotationKind.Text, boxed = text && CurrentBackground != TextBackground.None;
        _thickness.Enabled = _thicknessText.Enabled = kind is AnnotationKind.Frame or AnnotationKind.Arrow || boxed;
        _radius.Enabled = _radiusText.Enabled = kind == AnnotationKind.Frame || boxed;
        _head.Enabled = _headText.Enabled = kind == AnnotationKind.Arrow;
        _font.Enabled = _fontText.Enabled = kind is AnnotationKind.Text or AnnotationKind.Number;
        _text.Enabled = _backText.Enabled = _backNone.Enabled = _backBox.Enabled = _backBalloon.Enabled = text;
        _alignText.Enabled = _alignLeft.Enabled = _alignCenter.Enabled = _alignRight.Enabled = text;
        _fillSwatch.Enabled = _pickFill.Enabled = boxed;
        _textSwatch.Enabled = _pickText.Enabled = text;
        _fontBox.Enabled = _font.Enabled;
        _fontText.Text = $"大きさ {_font.Value} px";
        _thicknessText.Text = $"太さ {_thickness.Value} px";
        _radiusText.Text = _radius.Value == 0 ? "丸み なし" : $"丸み {_radius.Value} px";
        _headText.Text = $"先端 {(double)_head.Value / HeadScale:0.0} 倍";
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
            case Keys.Escape when ActiveControl == _text:
                _canvas.Focus(); // 文字を打ち終えた（ダイアログは閉じない）
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
            _font.Value = Math.Clamp(Annotator.DefaultFontSize(image.Width, image.Height), _font.Minimum, _font.Maximum);
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
            : _items.Count == 0 ? "ドラッグで枠・矢印を描き、クリックで文字・番号を置きます（いくつでも）"
            : Selected is not { } a ? $"描いたもの {_items.Count} 個（クリックすると選べます）"
            : a.Kind switch
            {
                AnnotationKind.Frame => $"描いたもの {_items.Count} 個（選んだ枠: {Math.Round(a.Width)} × {Math.Round(a.Height)} px）",
                AnnotationKind.Arrow => $"描いたもの {_items.Count} 個（選んだ矢印: 長さ {Math.Round(a.Length)} px）",
                AnnotationKind.Number => $"描いたもの {_items.Count} 個（選んだ番号: {a.Text}）",
                _ => $"描いたもの {_items.Count} 個（選んだ文字: {Math.Round(a.Body.Width)} × {Math.Round(a.Body.Height)} px。ダブルクリックで打ち直し）",
            };
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

    /// <summary>選んだものの、つかんで形を変える点（枠は四隅、矢印は始点と先端、文字・番号は本体の四隅と、吹き出しならしっぽの先）</summary>
    private static (double X, double Y)[] HandlesOf(Annotation a)
    {
        if (a.Kind == AnnotationKind.Arrow) return new[] { (a.X0, a.Y0), (a.X1, a.Y1) };
        if (a.Kind == AnnotationKind.Frame) return new[] { (a.X0, a.Y0), (a.X1, a.Y0), (a.X1, a.Y1), (a.X0, a.Y1) };
        var (x, y, w, h) = a.Body;
        var corners = new[] { (x, y), (x + w, y), (x + w, y + h), (x, y + h) };
        return a.HasTail ? corners.Append((a.X1, a.Y1)).ToArray() : corners;
    }

    /// <summary>長さ size のものを 0〜limit に収めたときの始まりの位置（収まらない大きさなら中央に置く）</summary>
    private static double ClampInside(double start, double size, double limit) =>
        size >= limit ? (limit - size) / 2 : Math.Clamp(start, 0, limit - size);

    /// <summary>次に置く番号（今ある番号のいちばん大きいもの + 1）</summary>
    private string NextNumber() =>
        (_items.Where(i => i.Kind == AnnotationKind.Number).Select(i => int.TryParse(i.Text, out int n) ? n : 0).DefaultIfEmpty(0).Max() + 1).ToString();

    /// <summary>文字の欄へ移って、すぐ打ち直せるようにする</summary>
    private void FocusText()
    {
        _text.Focus();
        _text.SelectAll();
    }

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
        bool typing = ActiveControl == _text; // 文字を打っている途中だったか
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

        // 選んだもののつかむ点なら形を変える（しっぽの先が角に重なっていたら、しっぽの先を優先する）
        if (Selected is { } a)
        {
            var handles = HandlesOf(a);
            for (int i = handles.Length - 1; i >= 0; i--)
            {
                if (!_canvas.HitsHandle(e.X, e.Y, handles[i])) continue;
                _creating = false;
                if (a.IsText && i == 4)
                {
                    _mode = DragMode.Tail;
                    return;
                }
                _mode = a.Kind == AnnotationKind.Text ? DragMode.Box : a.IsText ? DragMode.Scale : DragMode.Resize;
                _fixed = handles[a.IsText ? (i + 2) % 4 : (i + handles.Length / 2) % handles.Length]; // 枠・文字は対角、矢印は反対の端を固定
                _fixedRight = i is 0 or 3;
                _fixedBottom = i is 0 or 1;
                _dragTip = i == 1;
                return;
            }
        }

        // 線の上なら選んで移動、何も無い所なら新しく描く
        int target = HitItem(ix, iy);
        if (target >= 0)
        {
            _selected = target;
            LoadStyleFrom(_items[target]);
            if (e.Clicks > 1 && _items[target].Kind == AnnotationKind.Text)
            {
                // 文字をダブルクリック: 打ち直す
                OnItemsChanged();
                FocusText();
                return;
            }
            _mode = DragMode.Move;
            _moveLast = (ix, iy);
        }
        else if (typing && Selected != null && CurrentTool == AnnotationKind.Text)
        {
            // 文字を打ち終えて何も無い所をクリックした: 選択を外すだけ（続けて新しい文字を置いてしまわないように）
            _selected = -1;
        }
        else if (CurrentTool is AnnotationKind.Text or AnnotationKind.Number)
        {
            // 文字・番号: クリックした所を本体の中心にして置く。そのままドラッグすると、吹き出しはしっぽの先を、ほかは位置を決められる
            bool text = CurrentTool == AnnotationKind.Text;
            if (text && string.IsNullOrWhiteSpace(_text.Text))
            {
                _syncing = true;
                _text.Text = DefaultText;
                _syncing = false;
            }
            var item = NewItem(CurrentTool, 0, 0, ix, iy);
            if (!text) item = item with { Text = NextNumber() };
            var (_, _, w, h) = item.Body;
            var image = _stepper.Image;
            item = item with { X0 = ClampInside(ix - w / 2, w, image.Width), Y0 = ClampInside(iy - h / 2, h, image.Height) };
            _items.Add(item);
            _selected = _items.Count - 1;
            _mode = item.HasTail ? DragMode.Tail : DragMode.Move;
            _moveLast = (ix, iy);
            _focusText = text;
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
        else if (_mode == DragMode.Box) DoBox(e);
        else if (_mode == DragMode.Scale) DoScale(e);
        else if (_mode == DragMode.Tail) DoTail(e);
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
        // クリックだけで置いた吹き出しにも、しっぽを出す（置いた後のドラッグでは、指している所を変えない）
        if (_focusText && Selected is { } placed) _items[_selected] = WithTail(placed);
        OnItemsChanged();
        if (_focusText && Selected is { Kind: AnnotationKind.Text }) FocusText();
        _focusText = false;
    }

    private static bool TooSmall(Annotation a) =>
        !a.IsText && (a.Kind == AnnotationKind.Frame ? a.Width < MinSizePx || a.Height < MinSizePx : a.Length < MinSizePx);

    private void DoMove(MouseEventArgs e)
    {
        var (ix, iy) = _canvas.CanvasToImage(e.X, e.Y);
        var a = _items[_selected];
        if (a.IsText)
        {
            // 本体が画像の外へ出ない範囲で動かす（しっぽの先は動かさない）
            var (x, y, w, h) = a.Body;
            double mx = ClampInside(x + ix - _moveLast.X, w, _stepper.Image!.Width) - x, my = ClampInside(y + iy - _moveLast.Y, h, _stepper.Image.Height) - y;
            _items[_selected] = a.MoveBody(mx, my);
            _moveLast = (_moveLast.X + mx, _moveLast.Y + my);
            return;
        }
        // 画像の外へ出ない範囲で動かす
        double dx = Math.Clamp(ix - _moveLast.X, -Math.Min(a.X0, a.X1), _stepper.Image!.Width - Math.Max(a.X0, a.X1));
        double dy = Math.Clamp(iy - _moveLast.Y, -Math.Min(a.Y0, a.Y1), _stepper.Image.Height - Math.Max(a.Y0, a.Y1));
        _items[_selected] = a.Offset(dx, dy);
        _moveLast = (_moveLast.X + dx, _moveLast.Y + dy);
    }

    /// <summary>文字: 対角の角を動かさずに、本体の大きさをマウスの位置まで変える（文字が収まる大きさより小さくはならない）</summary>
    private void DoBox(MouseEventArgs e)
    {
        var (fx, fy) = _fixed;
        var a = _items[_selected];
        var (mx, my) = _canvas.ClampToImage(_canvas.CanvasToImage(e.X, e.Y));
        var (minWidth, minHeight) = a.MinBody;
        // 動かない角の反対側へ引いたときは、それ以上小さくしない（裏返さない）
        double w = Math.Max(minWidth, _fixedRight ? fx - mx : mx - fx), h = Math.Max(minHeight, _fixedBottom ? fy - my : my - fy);
        _items[_selected] = a with { BoxWidth = w, BoxHeight = h, X0 = _fixedRight ? fx - w : fx, Y0 = _fixedBottom ? fy - h : fy };
    }

    /// <summary>番号: 対角の角を動かさずに、マウスの位置まで本体が届くように大きさを変える</summary>
    private void DoScale(MouseEventArgs e)
    {
        var (fx, fy) = _fixed;
        var a = _items[_selected];
        var (mx, my) = _canvas.ClampToImage(_canvas.CanvasToImage(e.X, e.Y));
        var (_, _, w, h) = a.Body;
        if (w <= 0 || h <= 0) return;
        double k = Math.Max(Math.Abs(mx - fx) / w, Math.Abs(my - fy) / h);
        // 動かない角から画像の端までに収まる大きさまで（縦横の比は変えられないので、片方が先に端に着く）
        var image = _stepper.Image!;
        double room = Math.Min((_fixedRight ? fx : image.Width - fx) / w, (_fixedBottom ? fy : image.Height - fy) / h);
        int limit = Math.Clamp((int)Math.Floor(a.FontSize * room), _font.Minimum, _font.Maximum);
        int size = Math.Clamp((int)Math.Round(a.FontSize * k), _font.Minimum, limit);
        var scaled = a with { FontSize = size };
        var (_, _, nw, nh) = scaled.Body;
        _items[_selected] = scaled with { X0 = _fixedRight ? fx - nw : fx, Y0 = _fixedBottom ? fy - nh : fy };
        _syncing = true;
        _font.Value = size;
        _syncing = false;
        UpdateStyleControls();
    }

    /// <summary>吹き出し: しっぽの先を動かす</summary>
    private void DoTail(MouseEventArgs e)
    {
        var (mx, my) = _canvas.ClampToImage(_canvas.CanvasToImage(e.X, e.Y));
        _items[_selected] = _items[_selected] with { X1 = mx, Y1 = my };
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
            _saver.ShowNotice("枠・矢印・文字のどれかを描いてから保存してください");
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
