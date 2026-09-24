// ツールバー・フッターのボタン（線のアイコン、文字、ショートカットの表示、▾）。フォーカスは取らない（キー操作はショートカットで行う）
namespace ImageViewer.App.Chrome;

public sealed class IconButton : Control
{
    private bool _hover, _down, _active, _dropDown, _danger, _showText = true, _showHint = true;
    private IconPainter? _icon;
    private string _hint = "";

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

    /// <summary>押された状態で表示する（メニューを開いている間・選んでいる方など）</summary>
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

    /// <summary>文字の後ろに控えめに出すショートカット（"Ctrl+R" など）</summary>
    public string Hint
    {
        get => _hint;
        set { _hint = value ?? ""; Invalidate(); }
    }

    /// <summary>削除など取り消しにくい操作（危険の色で描く）</summary>
    public bool Danger
    {
        get => _danger;
        set { _danger = value; Invalidate(); }
    }

    /// <summary>幅が足りないときに文字 / ショートカットの表示を省く（アイコンがあるときだけ文字を省ける）</summary>
    public bool ShowText
    {
        get => _showText;
        set { _showText = value; Invalidate(); }
    }

    public bool ShowHint
    {
        get => _showHint;
        set { _showHint = value; Invalidate(); }
    }

    /// <summary>左右の余白（論理 px）</summary>
    public int HorizontalPadding { get; set; } = 10;

    private bool TextVisible => !string.IsNullOrEmpty(Text) && (_showText || _icon == null);
    private bool HintVisible => TextVisible && _showHint && _hint.Length > 0;

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    /// <summary>アイコンだけなら正方形、文字があれば文字の幅（＋ショートカット・▾）</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        int h = Height > 0 ? Height : LogicalToDeviceUnits(28);
        if (!TextVisible) return new Size(h + (_dropDown ? LogicalToDeviceUnits(10) : 0), h);
        int w = TextRenderer.MeasureText(Text, Font, Size.Empty, Flags).Width;
        if (_icon != null) w += LogicalToDeviceUnits(16 + 6);
        if (HintVisible) w += LogicalToDeviceUnits(6) + TextRenderer.MeasureText(_hint, HintFont, Size.Empty, Flags).Width;
        if (_dropDown) w += LogicalToDeviceUnits(4 + 10);
        return new Size(w + LogicalToDeviceUnits(HorizontalPadding) * 2, h);
    }

    private Font? _hintFont;
    private Font HintFont => _hintFont ??= new Font(Font.FontFamily, Font.Size * 0.9f);

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _hintFont?.Dispose();
        _hintFont = null;
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

        var color = !Enabled ? p.Disabled : _danger ? p.Danger : p.TextStrong;
        int iconSize = LogicalToDeviceUnits(16);
        int chevron = LogicalToDeviceUnits(10);
        if (!TextVisible)
        {
            float left = (Width - iconSize - (_dropDown ? chevron : 0)) / 2f;
            _icon?.Invoke(g, new RectangleF(left, (Height - iconSize) / 2f, iconSize, iconSize), color);
            if (_dropDown) Icons.ChevronDown(g, new RectangleF(left + iconSize, (Height - chevron) / 2f, chevron, chevron), color);
            return;
        }

        int x = LogicalToDeviceUnits(HorizontalPadding);
        if (_icon != null)
        {
            _icon(g, new RectangleF(x, (Height - iconSize) / 2f, iconSize, iconSize), color);
            x += iconSize + LogicalToDeviceUnits(6);
        }
        var textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, Flags);
        TextRenderer.DrawText(g, Text, Font, new Point(x, (Height - textSize.Height) / 2), color, Flags);
        x += textSize.Width;
        if (HintVisible)
        {
            x += LogicalToDeviceUnits(6);
            var hintSize = TextRenderer.MeasureText(g, _hint, HintFont, Size.Empty, Flags);
            TextRenderer.DrawText(g, _hint, HintFont, new Point(x, (Height - hintSize.Height) / 2 + 1), Enabled ? p.TextMuted : p.Disabled, Flags);
            x += hintSize.Width;
        }
        if (_dropDown)
            Icons.ChevronDown(g, new RectangleF(x + LogicalToDeviceUnits(4), (Height - chevron) / 2f, chevron, chevron), color);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hintFont?.Dispose();
        base.Dispose(disposing);
    }
}
