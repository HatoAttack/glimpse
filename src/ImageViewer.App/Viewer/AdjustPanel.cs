// 1 枚表示の右側の補正パネル（E で開け閉め）。スライダーとボタンだけで、画像にかけるのは 1 枚表示の側。
// 1 枚表示は常に暗い地なのでダークの配色で描く。どの部品もフォーカスを取らない（← → などのキーは 1 枚表示が受ける）。
// ウィンドウが低くて入りきらないときは縦にスクロールする
using System.Drawing.Drawing2D;
using ImageViewer.App.Chrome;
using ImageViewer.App.Theming;
using ImageViewer.Core.Editing;

namespace ImageViewer.App.Viewer;

public sealed class AdjustPanel : ScrollableControl
{
    private static Palette P => Palette.Dark;

    private readonly List<SliderRow> _rows = new();
    private readonly SliderRow _brightness, _contrast, _saturation, _temperature, _black, _white, _gamma;
    private readonly PanelButton _auto, _last, _reset, _compare, _save;
    private bool _updating;
    private int _levelsTop;

    /// <summary>値が変わった（スライダー・自動補正・前回の補正・リセット）</summary>
    public event EventHandler? OptionsChanged;

    /// <summary>「自動補正」が押された（1 枚表示が今の画像から値を決めて Options に入れる）</summary>
    public event EventHandler? AutoRequested;

    /// <summary>「保存」が押された</summary>
    public event EventHandler? SaveRequested;

    /// <summary>「補正前」を押している間 true</summary>
    public event EventHandler<bool>? CompareChanged;

    /// <summary>「前回の補正」で入れる値（保存したときの値。無ければ押せない）</summary>
    public AdjustOptions? Last
    {
        get => _last.Tag as AdjustOptions;
        set
        {
            _last.Tag = value;
            UpdateButtons();
        }
    }

    /// <summary>保存中（ボタンを押せなくする）</summary>
    public bool Saving
    {
        get => _saving;
        set
        {
            _saving = value;
            foreach (var row in _rows) row.Slider.Enabled = !value;
            UpdateButtons();
        }
    }

    private bool _saving;

    public AdjustPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Width = LogicalToDeviceUnits(264);
        BackColor = P.Surface;
        AutoScroll = true;

        _brightness = AddRow("明るさ", AdjustOptions.MinAmount, AdjustOptions.MaxAmount, 0, v => $"{v:+0;-0;0}");
        _contrast = AddRow("コントラスト", AdjustOptions.MinAmount, AdjustOptions.MaxAmount, 0, v => $"{v:+0;-0;0}");
        _saturation = AddRow("彩度", AdjustOptions.MinAmount, AdjustOptions.MaxAmount, 0, v => $"{v:+0;-0;0}");
        _temperature = AddRow("色温度", AdjustOptions.MinAmount, AdjustOptions.MaxAmount, 0, v => $"{v:+0;-0;0}");
        _black = AddRow("黒点", 0, 254, 0, v => v.ToString());
        _white = AddRow("白点", 1, 255, 255, v => v.ToString());
        _gamma = AddRow("ガンマ", GammaSliderMin, GammaSliderMax, 0, v => SliderToGamma(v).ToString("0.00"));

        // 黒点と白点は入れ替わらないように、動かした方に合わせてもう一方を押す
        _black.Slider.ValueChanged += (_, _) => { if (_white.Slider.Value <= _black.Slider.Value) _white.Slider.Value = _black.Slider.Value + 1; };
        _white.Slider.ValueChanged += (_, _) => { if (_black.Slider.Value >= _white.Slider.Value) _black.Slider.Value = _white.Slider.Value - 1; };

        _auto = AddButton("自動補正", "黒と白の位置と中間の明るさを、画像から決めます");
        _last = AddButton("前回の補正", "最後に保存したときの値を入れます");
        _reset = AddButton("リセット", "すべて元に戻します（スライダーはダブルクリックでその項目だけ）");
        _compare = AddButton("補正前", "押している間だけ、補正する前の画像を表示します");
        _save = AddButton("保存（上書き）  Ctrl+S", "補正した画像で元のファイルを上書きします");
        _save.Primary = true;

        _auto.Click += (_, _) => AutoRequested?.Invoke(this, EventArgs.Empty);
        _last.Click += (_, _) => { if (Last != null) Options = Last; };
        _reset.Click += (_, _) => Options = new AdjustOptions();
        _save.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
        _compare.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) CompareChanged?.Invoke(this, true); };
        _compare.MouseUp += (_, _) => CompareChanged?.Invoke(this, false);
        _compare.MouseCaptureChanged += (_, _) => { if (!_compare.Capture) CompareChanged?.Invoke(this, false); };
        UpdateButtons();
    }

    /// <summary>今の値。入れると画面の表示も合わせ、OptionsChanged を 1 回だけ出す</summary>
    public AdjustOptions Options
    {
        get => new AdjustOptions
        {
            Brightness = _brightness.Slider.Value,
            Contrast = _contrast.Slider.Value,
            Saturation = _saturation.Slider.Value,
            Temperature = _temperature.Slider.Value,
            BlackPoint = _black.Slider.Value,
            WhitePoint = _white.Slider.Value,
            Gamma = SliderToGamma(_gamma.Slider.Value),
        }.Normalize();
        set
        {
            var o = value.Normalize();
            _updating = true;
            try
            {
                _brightness.Slider.Value = o.Brightness;
                _contrast.Slider.Value = o.Contrast;
                _saturation.Slider.Value = o.Saturation;
                _temperature.Slider.Value = o.Temperature;
                // 黒点・白点は互いに押し合うので、広げる向きから入れる
                _black.Slider.Value = 0;
                _white.Slider.Value = o.WhitePoint;
                _black.Slider.Value = o.BlackPoint;
                _gamma.Slider.Value = GammaToSlider(o.Gamma);
            }
            finally
            {
                _updating = false;
            }
            Changed();
        }
    }

    // ガンマはスライダーの -100〜100 を 2^(v/50) = 0.25〜4 に（1 を真ん中にして、明るく・暗くを同じ幅で動かす）
    private const int GammaSliderMin = -100, GammaSliderMax = 100;
    private static double SliderToGamma(int v) => Math.Round(Math.Pow(2, v / 50.0), 2);
    private static int GammaToSlider(double gamma) =>
        Math.Clamp((int)Math.Round(Math.Log2(Math.Max(gamma, 0.01)) * 50), GammaSliderMin, GammaSliderMax);

    private SliderRow AddRow(string label, int min, int max, int initial, Func<int, string> format)
    {
        var slider = new ThinSlider { FixedPalette = P, Minimum = min, Maximum = max, AccessibleName = label };
        slider.Value = initial;
        var row = new SliderRow(label, slider, initial, format);
        slider.ValueChanged += (_, _) =>
        {
            Invalidate(); // 値の文字
            if (!_updating) Changed();
        };
        slider.DoubleClick += (_, _) => slider.Value = row.Default;
        _rows.Add(row);
        Controls.Add(slider);
        return row;
    }

    private PanelButton AddButton(string text, string hint)
    {
        var button = new PanelButton { Text = text, AccessibleName = text, AccessibleDescription = hint };
        _toolTip.SetToolTip(button, hint);
        Controls.Add(button);
        return button;
    }

    private readonly ToolTip _toolTip = new();

    private void Changed()
    {
        UpdateButtons();
        Invalidate();
        OptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateButtons()
    {
        if (_save == null) return; // 作っている途中
        bool identity = Options.IsIdentity;
        _save.Enabled = !identity && !_saving;
        _reset.Enabled = !identity && !_saving;
        _compare.Enabled = !identity;
        _auto.Enabled = !_saving;
        _last.Enabled = Last != null && !_saving;
    }

    private int Pad => LogicalToDeviceUnits(16);
    private int LabelHeight => LogicalToDeviceUnits(20);
    private int SliderHeight => LogicalToDeviceUnits(22);
    private int ButtonHeight => LogicalToDeviceUnits(30);
    private int Gap => LogicalToDeviceUnits(8);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_save == null) return;
        // 位置は中身の上端から数える（LabelTop・_levelsTop も）。部品は今のスクロールの分ずらして置く（AutoScrollPosition.Y は 0 か負）
        int x = Pad, width = Math.Max(1, ClientSize.Width - Pad * 2), top = AutoScrollPosition.Y;
        Rectangle At(int left, int y, int w, int h) => new(left, y + top, w, h);
        int y = LogicalToDeviceUnits(14) + Font.Height + LogicalToDeviceUnits(10); // 見出しの下
        foreach (var row in _rows)
        {
            if (row == _black)
            {
                y += Gap;
                _levelsTop = y;
                y += Gap + Font.Height + Gap; // 「レベル補正」の見出し（線の下に Gap あけて描く）
            }
            row.LabelTop = y;
            y += LabelHeight;
            row.Slider.Bounds = At(x, y, width, SliderHeight);
            y += SliderHeight + LogicalToDeviceUnits(4);
        }

        y += Gap * 2;
        int half = (width - Gap) / 2;
        _auto.Bounds = At(x, y, half, ButtonHeight);
        _last.Bounds = At(x + half + Gap, y, width - half - Gap, ButtonHeight);
        y += ButtonHeight + Gap;
        _reset.Bounds = At(x, y, half, ButtonHeight);
        _compare.Bounds = At(x + half + Gap, y, width - half - Gap, ButtonHeight);
        y += ButtonHeight + Gap * 2;
        _save.Bounds = At(x, y, width, ButtonHeight);
        // 中身の高さ（ウィンドウがこれより低ければスクロールバーが出る）
        var content = new Size(0, y + ButtonHeight + Pad);
        if (AutoScrollMinSize != content) AutoScrollMinSize = content;
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate(); // 手で描いている文字（項目名・値）を描き直す
    }

    /// <summary>パネルの上のホイールは親（1 枚表示・後ろの一覧）へ回さない</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }

    private const TextFormatFlags Flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(P.Surface);
        using (var line = new Pen(P.Border)) g.DrawLine(line, 0, 0, 0, Height);
        int x = Pad, width = Math.Max(0, ClientSize.Width - Pad * 2), top = AutoScrollPosition.Y; // スクロールの分ずらす
        using var small = new Font(Font.FontFamily, Font.Size * 0.9f);
        TextRenderer.DrawText(g, "補正", small, new Point(x, top + LogicalToDeviceUnits(14)), P.TextMuted, Flags & ~TextFormatFlags.VerticalCenter);

        foreach (var row in _rows)
        {
            var r = new Rectangle(x, top + row.LabelTop, width, LabelHeight);
            TextRenderer.DrawText(g, row.Label, Font, r, P.Text, Flags);
            bool changed = row.Slider.Value != row.Default;
            TextRenderer.DrawText(g, row.Format(row.Slider.Value), Font, r, changed ? P.Text : P.TextMuted, Flags | TextFormatFlags.Right);
        }
        if (_levelsTop > 0)
        {
            int levels = top + _levelsTop;
            using (var line = new Pen(P.Border)) g.DrawLine(line, x, levels, x + width, levels);
            TextRenderer.DrawText(g, "レベル補正", small, new Rectangle(x, levels + Gap, width, Font.Height), P.TextMuted, Flags);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private sealed class SliderRow(string label, ThinSlider slider, int @default, Func<int, string> format)
    {
        public string Label { get; } = label;
        public ThinSlider Slider { get; } = slider;
        public int Default { get; } = @default;
        public Func<int, string> Format { get; } = format;
        public int LabelTop { get; set; }
    }

    /// <summary>パネルのボタン（フォーカスを取らない。暗い地で描く）</summary>
    private sealed class PanelButton : Control
    {
        private bool _hover, _down;

        /// <summary>主な操作（保存）。目立つ色で描く</summary>
        public bool Primary { get; set; }

        public PanelButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardDoubleClick, false);
            TabStop = false;
            AccessibleRole = AccessibleRole.PushButton;
            Cursor = Cursors.Hand;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = e.Button == MouseButtons.Left; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(P.Surface);
            Color fill = !Enabled ? P.Surface
                : Primary ? (_down ? ControlPaint.Dark(P.SelectionBorder, 0.1f) : _hover ? ControlPaint.Light(P.SelectionBorder, 0.1f) : P.SelectionBorder)
                : _down ? P.Pressed : _hover ? P.Hover : P.Field;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Icons.FillRounded(g, r, LogicalToDeviceUnits(4), fill);
            if (!Enabled)
                using (var border = new Pen(P.Border)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            g.SmoothingMode = SmoothingMode.None;
            Color text = !Enabled ? P.Disabled : Primary ? Color.White : P.Text;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
