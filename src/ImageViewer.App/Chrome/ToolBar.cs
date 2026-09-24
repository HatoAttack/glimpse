// 上のツールバー（高さ固定）。左から順に並べる部品・残りの幅を使う部品（アドレスバー）・右端に並べる部品
namespace ImageViewer.App.Chrome;

public sealed class ToolBar : Control
{
    /// <summary>部品の間に入れる区切り線</summary>
    public static readonly Control Separator = new();

    private readonly List<Control?> _left = new(), _right = new();
    private Control? _fill;
    private readonly List<int> _separators = new(); // 区切り線の x

    public ToolBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Dock = DockStyle.Top;
        Height = LogicalToDeviceUnits(40);
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        BackColor = Theming.Theme.Current.Surface;
        Invalidate(true);
    }

    /// <summary>左から並べる（ToolBar.Separator で区切り線）</summary>
    public void AddLeft(params Control[] controls) => Add(_left, controls);

    /// <summary>右端から並べる（渡した順に左から）</summary>
    public void AddRight(params Control[] controls) => Add(_right, controls);

    public void SetFill(Control control)
    {
        _fill = control;
        Controls.Add(control);
    }

    private void Add(List<Control?> list, Control[] controls)
    {
        foreach (var c in controls)
        {
            if (c == Separator)
            {
                list.Add(null);
                continue;
            }
            list.Add(c);
            Controls.Add(c);
        }
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int pad = LogicalToDeviceUnits(6), gap = LogicalToDeviceUnits(2), sepSpace = LogicalToDeviceUnits(9);
        int button = LogicalToDeviceUnits(28);
        int top = (Height - button) / 2;
        _separators.Clear();

        int x = pad;
        foreach (var c in _left)
        {
            if (c == null)
            {
                _separators.Add(x + sepSpace / 2 - gap / 2);
                x += sepSpace;
                continue;
            }
            if (!c.Visible) continue;
            int w = c.GetPreferredSize(Size.Empty).Width;
            c.SetBounds(x, top, w, button);
            x += w + gap;
        }

        int right = Width - pad;
        for (int i = _right.Count - 1; i >= 0; i--)
        {
            var c = _right[i];
            if (c == null)
            {
                right -= sepSpace;
                _separators.Add(right + sepSpace / 2);
                continue;
            }
            if (!c.Visible) continue;
            int w = c.GetPreferredSize(Size.Empty).Width;
            right -= w;
            c.SetBounds(right, top, w, button);
            right -= gap;
        }

        int margin = LogicalToDeviceUnits(6);
        _fill?.SetBounds(x + margin - gap, top, Math.Max(0, right - x - margin * 2 + gap * 2), button);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theming.Theme.Current;
        using var pen = new Pen(p.Border);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        int h = LogicalToDeviceUnits(18);
        foreach (int x in _separators) e.Graphics.DrawLine(pen, x, (Height - h) / 2, x, (Height + h) / 2);
    }
}
