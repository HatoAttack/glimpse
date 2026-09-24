// アドレスバー: ふだんは今のフォルダをパンくず（クリックでその階層へ）で表示し、
// 空いている所のクリック・Ctrl+L で入力に切り替える（枠なしの TextBox を出す）。入力中は青い枠
namespace ImageViewer.App.Chrome;

public sealed class AddressBox : Control
{
    private string? _path;
    private readonly List<(Rectangle Bounds, string Path, string Label)> _segments = new();
    private Rectangle _ellipsis;
    private int _hover = -1;

    public TextBox TextBox { get; }

    /// <summary>パンくずの階層がクリックされた（そのフォルダのパス）</summary>
    public event EventHandler<string>? SegmentClicked;

    public AddressBox(TextBox textBox)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        TextBox = textBox;
        TextBox.BorderStyle = BorderStyle.None;
        TextBox.Dock = DockStyle.None;
        TextBox.Visible = false;
        Controls.Add(TextBox);
        TextBox.GotFocus += (_, _) => Invalidate();
        // 入力をやめたらパンくずに戻す（候補の一覧をクリックしてフォーカスが移っただけなら入力を続ける）
        TextBox.LostFocus += (_, _) => BeginInvoke(() =>
        {
            if (!TextBox.Focused && KeepEditing?.Invoke() != true) EndEdit();
        });
        ApplyTheme();
    }

    /// <summary>パンくずに出す今のフォルダ</summary>
    public string? Path
    {
        get => _path;
        set
        {
            if (_path == value) return;
            _path = value;
            _hover = -1;
            Invalidate();
        }
    }

    /// <summary>フォーカスが離れても入力を続けるとき（候補の一覧にフォーカスがある間など）</summary>
    public Func<bool>? KeepEditing { get; set; }

    /// <summary>入力をやめてパンくずに戻す。途中まで入力した文字は捨てて今のフォルダに戻す（次の Ctrl+L で古い入力が出ないように）</summary>
    public void EndEdit()
    {
        if (!TextBox.Visible) return;
        TextBox.Visible = false;
        TextBox.Text = _path ?? "";
        Invalidate();
    }

    /// <summary>入力を始める（Ctrl+L・空いている所のクリック）。入っている文字は全体を選択</summary>
    public void BeginEdit()
    {
        TextBox.Visible = true;
        TextBox.Focus();
        TextBox.SelectAll();
        Invalidate();
    }

    public void ApplyTheme()
    {
        var p = Theming.Theme.Current;
        TextBox.BackColor = p.Field;
        TextBox.ForeColor = p.Text;
        Invalidate();
    }

    private bool Editing => TextBox.Visible;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int pad = LogicalToDeviceUnits(10);
        int h = TextBox.PreferredHeight;
        TextBox.SetBounds(pad, (Height - h) / 2, Math.Max(0, Width - pad * 2), h);
    }

    // ---- パンくず ----

    /// <summary>"C:\Users\me\Pictures" → [C:, Users, me, Pictures]（各階層のパス付き）</summary>
    private static List<(string Label, string Path)> Split(string path)
    {
        var list = new List<(string, string)>();
        string? root = System.IO.Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return list;
        list.Add((root.TrimEnd('\\'), root));
        string current = root;
        foreach (var name in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, name);
            list.Add((name, current));
        }
        return list;
    }

    private const TextFormatFlags Flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

    /// <summary>階層の位置を決める。入りきらなければ先頭側を「…」にまとめる</summary>
    private void LayoutSegments(Graphics g)
    {
        _segments.Clear();
        _ellipsis = Rectangle.Empty;
        if (_path == null) return;
        var parts = Split(_path);
        int pad = LogicalToDeviceUnits(6), sep = LogicalToDeviceUnits(14), left = LogicalToDeviceUnits(4);
        int available = Width - left * 2;
        var widths = parts.Select(p => TextRenderer.MeasureText(g, p.Label, Font, Size.Empty, Flags).Width + pad * 2).ToList();
        int first = 0;
        int ellipsisWidth = TextRenderer.MeasureText(g, "…", Font, Size.Empty, Flags).Width + pad * 2 + sep;
        int Total(int from) => widths.Skip(from).Sum() + sep * (parts.Count - from - 1) + (from > 0 ? ellipsisWidth : 0);
        while (first < parts.Count - 1 && Total(first) > available) first++;

        int x = left, top = LogicalToDeviceUnits(3), height = Height - top * 2;
        if (first > 0)
        {
            _ellipsis = new Rectangle(x, top, ellipsisWidth - sep, height);
            x += ellipsisWidth;
        }
        for (int i = first; i < parts.Count; i++)
        {
            _segments.Add((new Rectangle(x, top, widths[i], height), parts[i].Path, parts[i].Label));
            x += widths[i] + sep;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theming.Theme.Current;
        var g = e.Graphics;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        Icons.FillRounded(g, r, LogicalToDeviceUnits(6), p.Field);
        if (Editing)
        {
            if (!TextBox.Focused) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Icons.RoundedRect(RectangleF.Inflate(r, -0.75f, -0.75f), LogicalToDeviceUnits(6));
            using var pen = new Pen(p.SelectionBorder, 1.5f);
            g.DrawPath(pen, path);
            g.SmoothingMode = old;
            return;
        }

        LayoutSegments(g);
        int pad = LogicalToDeviceUnits(6), chevron = LogicalToDeviceUnits(10);
        if (!_ellipsis.IsEmpty)
        {
            TextRenderer.DrawText(g, "…", Font, Rectangle.Inflate(_ellipsis, -pad, 0), p.TextSecondary, Flags);
            Icons.Forward(g, new RectangleF(_ellipsis.Right + 2, (Height - chevron) / 2f, chevron, chevron), p.TextMuted);
        }
        for (int i = 0; i < _segments.Count; i++)
        {
            var (bounds, _, label) = _segments[i];
            bool last = i == _segments.Count - 1;
            if (i == _hover) Icons.FillRounded(g, bounds, LogicalToDeviceUnits(4), p.Hover);
            TextRenderer.DrawText(g, label, Font, Rectangle.Inflate(bounds, -pad, 0), last ? p.Text : p.TextSecondary, Flags);
            if (!last) Icons.Forward(g, new RectangleF(bounds.Right + 2, (Height - chevron) / 2f, chevron, chevron), p.TextMuted);
        }

        // 空いていれば右端に操作の案内
        const string hint = "Ctrl+L で移動・コマンド";
        int hintWidth = TextRenderer.MeasureText(g, hint, Font, Size.Empty, Flags).Width;
        int end = _segments.Count > 0 ? _segments[^1].Bounds.Right : 0;
        if (Width - end > hintWidth + LogicalToDeviceUnits(48))
            TextRenderer.DrawText(g, hint, Font, new Rectangle(Width - hintWidth - LogicalToDeviceUnits(10), 0, hintWidth, Height), p.TextMuted, Flags);
    }

    private int SegmentAt(Point pt) => _segments.FindIndex(s => s.Bounds.Contains(pt));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = Editing ? -1 : SegmentAt(e.Location);
        Cursor = i >= 0 ? Cursors.Hand : Cursors.IBeam;
        if (i == _hover) return;
        _hover = i;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (!Editing && SegmentAt(e.Location) is var i and >= 0)
        {
            SegmentClicked?.Invoke(this, _segments[i].Path);
            return;
        }
        BeginEdit(); // 空いている所・「…」は入力
    }
}
