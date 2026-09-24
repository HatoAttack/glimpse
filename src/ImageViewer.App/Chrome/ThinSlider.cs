// フッターの細いスライダー（サムネイルの大きさ）。標準の TrackBar はダークの配色にできないので自分で描く。
// フォーカスは取らない（一覧のキー操作を奪わない。Ctrl+ホイールでも変えられる）
namespace ImageViewer.App.Chrome;

public sealed class ThinSlider : Control
{
    private int _min, _max = 100, _value, _step = 1;
    private bool _dragging, _hover;

    public event EventHandler? ValueChanged;

    public ThinSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.Slider;
    }

    public int Minimum
    {
        get => _min;
        set { _min = value; Value = _value; }
    }

    public int Maximum
    {
        get => _max;
        set { _max = Math.Max(_min, value); Value = _value; }
    }

    /// <summary>ドラッグ・ホイールで動く単位</summary>
    public int Step
    {
        get => _step;
        set => _step = Math.Max(1, value);
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _value) return;
            _value = v;
            AccessibleDescription = v.ToString();
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int ThumbRadius => LogicalToDeviceUnits(7);
    private int TrackLeft => ThumbRadius;
    private int TrackWidth => Math.Max(1, Width - ThumbRadius * 2);
    private float Fraction => _max == _min ? 0 : (float)(_value - _min) / (_max - _min);

    private void SetFromX(int x)
    {
        float f = Math.Clamp((x - TrackLeft) / (float)TrackWidth, 0, 1);
        int raw = _min + (int)Math.Round(f * (_max - _min));
        Value = _min + (int)Math.Round((raw - _min) / (double)_step) * _step;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Value += Math.Sign(e.Delta) * _step;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theming.Theme.Current;
        var g = e.Graphics;
        int track = LogicalToDeviceUnits(4);
        float cy = Height / 2f;
        var full = new RectangleF(TrackLeft, cy - track / 2f, TrackWidth, track);
        Icons.FillRounded(g, full, track / 2f, p.Border);
        Icons.FillRounded(g, full with { Width = TrackWidth * Fraction }, track / 2f, p.TextMuted);

        float cx = TrackLeft + TrackWidth * Fraction;
        int r = ThumbRadius;
        var thumb = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        var old = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(p.IsDark ? p.TextStrong : p.Surface)) g.FillEllipse(fill, thumb);
        using (var ring = new Pen(_hover || _dragging ? p.SelectionBorder : p.TextMuted, 1)) g.DrawEllipse(ring, thumb);
        g.SmoothingMode = old;
    }
}
