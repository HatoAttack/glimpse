// 画像を選んでいる間にフッターに出す操作ボタン。
// 左から 対象の切り替え（選択 / チェック）｜ 操作ボタン …（空きに選択中の画像の情報）… 削除・選択を解除。
// 幅が足りないときは ショートカットの表示 → ボタンの文字 の順に省く（対象の切り替えと削除の文字は最後まで残す）
namespace ImageViewer.App.Chrome;

public sealed class ActionBar : Control
{
    private readonly List<IconButton> _targets = new(), _actions = new(), _trailing = new();
    private string _info = "";
    private int _separatorX = -1;
    private Rectangle _targetGroup, _infoBounds;
    // 親が隠れていると Visible は false になるので、出す / 出さないは自分で覚える
    private readonly HashSet<IconButton> _hidden = new();

    public ActionBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
    }

    /// <summary>対象の切り替え（角の丸い枠の中に並べる）</summary>
    public void AddTargets(params IconButton[] buttons) => Add(_targets, buttons, padding: 8);

    /// <summary>操作ボタン（左から）</summary>
    public void AddActions(params IconButton[] buttons) => Add(_actions, buttons, padding: 8);

    /// <summary>右端のボタン（削除・選択を解除）</summary>
    public void AddTrailing(params IconButton[] buttons) => Add(_trailing, buttons, padding: 8);

    private void Add(List<IconButton> list, IconButton[] buttons, int padding)
    {
        foreach (var b in buttons)
        {
            b.HorizontalPadding = padding;
            list.Add(b);
            Controls.Add(b);
        }
        PerformLayout();
    }

    /// <summary>ボタンを出す / 出さない（チェックが無いときの「チェック」など）</summary>
    public void SetShown(IconButton button, bool shown)
    {
        if (shown ? !_hidden.Remove(button) : !_hidden.Add(button)) return;
        button.Visible = shown;
        PerformLayout();
    }

    private bool Shown(IconButton b) => !_hidden.Contains(b);

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) PerformLayout();
    }

    /// <summary>空いている所に出す、選択中の画像の情報</summary>
    public string Info
    {
        get => _info;
        set
        {
            if (_info == value) return;
            _info = value;
            Invalidate();
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (Width <= 0) return;
        int h = LogicalToDeviceUnits(26), top = (Height - h) / 2;
        int gap = LogicalToDeviceUnits(2), sep = LogicalToDeviceUnits(13), segPad = LogicalToDeviceUnits(2);
        foreach (var b in _targets.Concat(_actions).Concat(_trailing)) b.Height = h;

        // 省く段階: 0 = 全部出す、1 = ショートカットを省く、2 = 操作ボタンの文字も省く、3 = 右端の文字も省く
        int Measure(IEnumerable<IconButton> bs) => bs.Where(Shown).Sum(b => b.GetPreferredSize(Size.Empty).Width + gap);
        for (int level = 0; level <= 3; level++)
        {
            foreach (var b in _actions.Concat(_trailing))
            {
                b.ShowHint = level == 0;
                b.ShowText = _trailing.Contains(b) ? level < 3 : level < 2;
            }
            int need = Measure(_targets) + segPad * 2 + sep + Measure(_actions) + LogicalToDeviceUnits(16) + Measure(_trailing);
            if (need <= Width || level == 3) break;
        }

        int x = 0;
        int groupLeft = x;
        x += segPad;
        foreach (var b in _targets.Where(Shown))
        {
            int w = b.GetPreferredSize(Size.Empty).Width;
            b.SetBounds(x, top, w, h);
            x += w + gap;
        }
        x += segPad - gap;
        _targetGroup = _targets.Any(Shown) ? new Rectangle(groupLeft, top - segPad, x - groupLeft, h + segPad * 2) : Rectangle.Empty;
        _separatorX = x + sep / 2;
        x += sep;
        foreach (var b in _actions.Where(Shown))
        {
            int w = b.GetPreferredSize(Size.Empty).Width;
            b.SetBounds(x, top, w, h);
            x += w + gap;
        }

        int right = Width;
        for (int i = _trailing.Count - 1; i >= 0; i--)
        {
            var b = _trailing[i];
            if (!Shown(b)) continue;
            int w = b.GetPreferredSize(Size.Empty).Width;
            right -= w;
            b.SetBounds(right, top, w, h);
            right -= gap;
        }
        int margin = LogicalToDeviceUnits(16);
        _infoBounds = new Rectangle(x + margin, 0, Math.Max(0, right - margin - x - margin), Height);
        Invalidate();
    }

    private const TextFormatFlags Flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine
                                          | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theming.Theme.Current;
        var g = e.Graphics;
        if (!_targetGroup.IsEmpty) Icons.FillRounded(g, _targetGroup, LogicalToDeviceUnits(7), p.Field);
        if (_separatorX >= 0)
        {
            int h = LogicalToDeviceUnits(18);
            using var pen = new Pen(p.Border);
            g.DrawLine(pen, _separatorX, (Height - h) / 2, _separatorX, (Height + h) / 2);
        }
        // 情報は、ある程度の幅が空いているときだけ
        if (_info.Length > 0 && _infoBounds.Width >= LogicalToDeviceUnits(80))
            TextRenderer.DrawText(g, _info, Font, _infoBounds, p.TextSecondary, Flags);
    }
}
