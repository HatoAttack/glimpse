// ツールバーのボタン（線のアイコン、または文字＋▾）。フォーカスは取らない（キー操作はショートカットで行う）
namespace ImageViewer.App.Chrome;

public sealed class IconButton : Control
{
    private bool _hover, _down, _active;
    private IconPainter? _icon;
    private bool _dropDown;

    public IconButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.PushButton;
        BackColor = Color.Transparent;
    }

    public IconPainter? Icon
    {
        get => _icon;
        set { _icon = value; Invalidate(); }
    }

    /// <summary>文字の右に ▾ を付ける（押すとメニューが出るボタン）</summary>
    public bool DropDown
    {
        get => _dropDown;
        set { _dropDown = value; Invalidate(); }
    }

    /// <summary>押された状態で表示する（メニューを開いている間など）</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            Invalidate();
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName ??= Text;
        Invalidate();
    }

    /// <summary>アイコンだけなら正方形、文字があれば文字の幅（＋▾）</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        int h = Height > 0 ? Height : LogicalToDeviceUnits(28);
        if (string.IsNullOrEmpty(Text)) return new Size(h, h);
        int w = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        int pad = LogicalToDeviceUnits(10);
        if (_icon != null) w += LogicalToDeviceUnits(16 + 6);
        if (_dropDown) w += LogicalToDeviceUnits(4 + 10);
        return new Size(w + pad * 2, h);
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
        _hover = _down = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _down = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _down = false;
        Invalidate();
    }

    /// <summary>左ボタンのクリックだけを Click にする（戻る / 進むボタン等は親が MouseDown で受ける）</summary>
    protected override void OnClick(EventArgs e)
    {
        if (e is MouseEventArgs { Button: not MouseButtons.Left }) return;
        base.OnClick(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theming.Theme.Current;
        var g = e.Graphics;
        var bounds = new RectangleF(0, 0, Width, Height);
        if (Enabled && (_down || _active)) Icons.FillRounded(g, bounds, LogicalToDeviceUnits(6), p.Pressed);
        else if (Enabled && _hover) Icons.FillRounded(g, bounds, LogicalToDeviceUnits(6), p.Hover);

        var color = Enabled ? p.TextStrong : p.Disabled;
        int iconSize = LogicalToDeviceUnits(16);
        if (string.IsNullOrEmpty(Text))
        {
            _icon?.Invoke(g, new RectangleF((Width - iconSize) / 2f, (Height - iconSize) / 2f, iconSize, iconSize), color);
            return;
        }

        int x = LogicalToDeviceUnits(10);
        if (_icon != null)
        {
            _icon(g, new RectangleF(x, (Height - iconSize) / 2f, iconSize, iconSize), color);
            x += iconSize + LogicalToDeviceUnits(6);
        }
        var textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Text, Font, new Point(x, (Height - textSize.Height) / 2), color, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        if (_dropDown)
        {
            int chevron = LogicalToDeviceUnits(10);
            Icons.ChevronDown(g, new RectangleF(x + textSize.Width + LogicalToDeviceUnits(4), (Height - chevron) / 2f, chevron, chevron), color);
        }
    }
}
