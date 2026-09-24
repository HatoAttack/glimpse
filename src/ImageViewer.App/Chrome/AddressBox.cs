// アドレスバーの入れ物: 角の丸い入力欄の地に、枠なしの TextBox を載せる。入力中は青い枠を出す
namespace ImageViewer.App.Chrome;

public sealed class AddressBox : Control
{
    public TextBox TextBox { get; }

    public AddressBox(TextBox textBox)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        TextBox = textBox;
        TextBox.BorderStyle = BorderStyle.None;
        TextBox.Dock = DockStyle.None;
        Controls.Add(TextBox);
        TextBox.GotFocus += (_, _) => Invalidate();
        TextBox.LostFocus += (_, _) => Invalidate();
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        var p = Theming.Theme.Current;
        TextBox.BackColor = p.Field;
        TextBox.ForeColor = p.Text;
        Invalidate();
    }

    /// <summary>欄のどこをクリックしても入力できるように</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        TextBox.Focus();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int pad = LogicalToDeviceUnits(10);
        int h = TextBox.PreferredHeight;
        TextBox.SetBounds(pad, (Height - h) / 2, Math.Max(0, Width - pad * 2), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theming.Theme.Current;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        Icons.FillRounded(e.Graphics, r, LogicalToDeviceUnits(6), p.Field);
        if (!TextBox.Focused) return;
        var old = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = Icons.RoundedRect(RectangleF.Inflate(r, -0.75f, -0.75f), LogicalToDeviceUnits(6));
        using var pen = new Pen(p.SelectionBorder, 1.5f);
        e.Graphics.DrawPath(pen, path);
        e.Graphics.SmoothingMode = old;
    }
}
