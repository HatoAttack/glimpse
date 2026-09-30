// ダイアログの共通の土台: タイトルバーを今の配色に合わせ、ダークのときは中の標準部品をダークの色にする。
// ライトのときは Windows の標準の見た目のまま（今までと変えない）。開いている間に配色が変わったら塗り直す
// ダークで使えない（Enabled = false）チェックボックス・ラジオボタン・ラベルは、標準の浮き彫りの文字が読みづらいので控えめな色で描き直す
using System.Runtime.InteropServices;
using ImageViewer.App.Jump;

namespace ImageViewer.App.Theming;

public class ThemedForm : Form
{
    private bool _prepared;
    // ダイアログを作ったとき・最後に塗ったときの配色（文字の色を新しい配色の同じ役割の色に置き換えるため）
    private Palette _palette = Theme.Current;

    public ThemedForm()
    {
        Icon = AppIcon.Current;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        // ライトで開くときは何も塗らない（今まで通りの見た目のまま）
        if (Theme.Current.IsDark) ApplyTheme();
        Theme.Changed += OnThemeChanged;
        base.OnLoad(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Theme.Changed -= OnThemeChanged;
        base.OnFormClosed(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        Theme.ApplyTitleBar(this);
        ApplyTheme();
        Invalidate(true);
    }

    private void ApplyTheme()
    {
        if (!_prepared)
        {
            // 見出しを描き直す仕掛けは 1 回だけ付ける（描くたびに今の配色を読む）
            _prepared = true;
            Prepare(this);
        }
        // 作ったときに配色の色で塗った文字（補足の灰色・エラーの赤など）を、新しい配色の同じ役割の色に
        if (_palette != Theme.Current) Remap(this, _palette, Theme.Current);
        _palette = Theme.Current;
        if (Theme.Current.IsDark)
        {
            BackColor = Theme.Current.Background;
            ForeColor = Theme.Current.Text;
        }
        else
        {
            ResetBackColor();
            ResetForeColor();
        }
        Apply(this, this, Theme.Current);
    }

    private static void Remap(Control parent, Palette from, Palette to)
    {
        foreach (Control c in parent.Controls)
        {
            var fore = c.ForeColor;
            if (fore == from.Text) c.ForeColor = to.Text;
            else if (fore == from.TextSecondary) c.ForeColor = to.TextSecondary;
            else if (fore == from.TextMuted) c.ForeColor = to.TextMuted;
            else if (fore == from.Danger) c.ForeColor = to.Danger;
            else if (fore == from.Warning) c.ForeColor = to.Warning;
            Remap(c, from, to);
        }
    }

    private static void Prepare(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case ListView view:
                    PrepareHeader(view);
                    break;
                case GroupBox group:
                    // 標準の描き方では、色を変えた見出しの上を枠の線が横切ってしまうので、ダークのときは見出しだけ描き直す
                    group.Paint += (_, e) =>
                    {
                        if (!Theme.Current.IsDark || string.IsNullOrEmpty(group.Text)) return;
                        var size = TextRenderer.MeasureText(e.Graphics, group.Text, group.Font, Size.Empty, TextFormatFlags.NoPrefix);
                        int x = group.LogicalToDeviceUnits(6);
                        using (var bg = new SolidBrush(group.BackColor)) e.Graphics.FillRectangle(bg, x, 0, size.Width + 2, size.Height);
                        TextRenderer.DrawText(e.Graphics, group.Text, group.Font, new Point(x + 1, 0), Theme.Current.Text, TextFormatFlags.NoPrefix);
                    };
                    break;
                case CheckBox or RadioButton:
                    // 使えない間の文字は、標準では地より暗い色に白い影を付けて描くので、ダークでは文字が反転したように見える。描き直す
                    c.Paint += (_, e) => { if (Theme.Current.IsDark && !c.Enabled) PaintDisabledCheck((ButtonBase)c, e.Graphics); };
                    break;
                case Label label when label.GetType() == typeof(Label):
                    label.Paint += (_, e) => { if (Theme.Current.IsDark && !label.Enabled) PaintDisabledLabel(label, e.Graphics); };
                    break;
            }
            Prepare(c);
        }
    }

    /// <summary>ダークで使えないチェックボックス・ラジオボタンを、控えめな文字の色で描き直す（印も同じ色で）</summary>
    private static void PaintDisabledCheck(ButtonBase button, Graphics g)
    {
        var p = Theme.Current;
        var rect = button.ClientRectangle;
        using (var bg = new SolidBrush(button.BackColor)) g.FillRectangle(bg, rect);
        int size = button.LogicalToDeviceUnits(12);
        var glyph = new Rectangle(rect.Left + button.Padding.Left, rect.Top + (rect.Height - size) / 2, size, size);
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var pen = new Pen(p.TextMuted, 1))
        using (var fill = new SolidBrush(p.TextMuted))
        {
            if (button is RadioButton radio)
            {
                g.DrawEllipse(pen, glyph.X, glyph.Y, glyph.Width - 1, glyph.Height - 1);
                if (radio.Checked)
                {
                    int inset = size / 4 + 1;
                    g.FillEllipse(fill, glyph.X + inset, glyph.Y + inset, glyph.Width - inset * 2 - 1, glyph.Height - inset * 2 - 1);
                }
            }
            else
            {
                g.DrawRectangle(pen, glyph.X, glyph.Y, glyph.Width - 1, glyph.Height - 1);
                if (((CheckBox)button).CheckState != CheckState.Unchecked)
                {
                    using var mark = new Pen(p.TextMuted, Math.Max(1.5f, button.LogicalToDeviceUnits(2)));
                    g.DrawLines(mark, new[]
                    {
                        new PointF(glyph.X + size * 0.22f, glyph.Y + size * 0.52f),
                        new PointF(glyph.X + size * 0.42f, glyph.Y + size * 0.72f),
                        new PointF(glyph.X + size * 0.78f, glyph.Y + size * 0.28f),
                    });
                }
            }
        }
        g.SmoothingMode = smoothing;
        int textLeft = glyph.Right + button.LogicalToDeviceUnits(4);
        var textRect = Rectangle.FromLTRB(textLeft, rect.Top, rect.Right - button.Padding.Right, rect.Bottom);
        TextRenderer.DrawText(g, button.Text, button.Font, textRect, p.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    /// <summary>ダークで使えないラベルを、控えめな文字の色で描き直す（標準の浮き彫りの描き方をやめる）</summary>
    private static void PaintDisabledLabel(Label label, Graphics g)
    {
        using (var bg = new SolidBrush(label.BackColor)) g.FillRectangle(bg, label.ClientRectangle);
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | (label.UseMnemonic ? TextFormatFlags.Default : TextFormatFlags.NoPrefix);
        flags |= label.TextAlign switch
        {
            ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.Left,
        };
        flags |= label.TextAlign switch
        {
            ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight => TextFormatFlags.VerticalCenter,
            ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => TextFormatFlags.Bottom,
            _ => TextFormatFlags.Top,
        };
        var rect = Rectangle.FromLTRB(label.Padding.Left, label.Padding.Top,
            label.ClientSize.Width - label.Padding.Right, label.ClientSize.Height - label.Padding.Bottom);
        TextRenderer.DrawText(g, label.Text, label.Font, rect, Theme.Current.TextMuted, flags);
    }

    /// <summary>
    /// 中の部品を今の配色に。ラベル・パネル・チェックボックス等は親の色を引き継ぐので、引き継がない部品（入力欄・一覧・ボタン）だけ個別に塗る。
    /// ライトでは標準の見た目に戻す（開いている間にダークからライトに変わったとき）。自前で描いている部品（プレビュー等）は触らない
    /// </summary>
    private static void Apply(Control parent, Form form, Palette p)
    {
        bool dark = p.IsDark;
        // ライトに戻すときは、塗った色を消して標準の色に戻す
        void Colors(Control c, Color back, Color fore)
        {
            if (dark)
            {
                c.BackColor = back;
                c.ForeColor = fore;
            }
            else
            {
                c.ResetBackColor();
                c.ResetForeColor();
            }
        }
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case JumpList list:
                    list.ApplyTheme();
                    break;
                case TextBoxBase box:
                    Colors(box, p.Field, p.Text);
                    Theme.ApplyNativeTheme(box);
                    break;
                case NumericUpDown number:
                    Colors(number, p.Field, p.Text);
                    break;
                case ComboBox combo:
                    combo.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                    Colors(combo, p.Field, p.Text);
                    break;
                case ListBox list:
                    Colors(list, p.Field, p.Text);
                    list.BorderStyle = dark ? BorderStyle.None : BorderStyle.Fixed3D; // ダークでは標準の枠が白い線になる
                    Theme.ApplyNativeTheme(list);
                    break;
                case ListView view:
                    Colors(view, p.Field, p.Text);
                    view.BorderStyle = dark ? BorderStyle.None : BorderStyle.Fixed3D;
                    Theme.ApplyNativeTheme(view);
                    ApplyHeaderTheme(view);
                    view.Invalidate();
                    break;
                case Button button:
                    if (dark)
                    {
                        button.FlatStyle = FlatStyle.Flat;
                        button.UseVisualStyleBackColor = false;
                        button.BackColor = p.Field;
                        button.ForeColor = p.Text;
                        button.FlatAppearance.BorderColor = button == form.AcceptButton ? p.SelectionBorder : p.Border;
                        button.FlatAppearance.MouseOverBackColor = p.Hover;
                        button.FlatAppearance.MouseDownBackColor = p.Pressed;
                    }
                    else
                    {
                        button.FlatStyle = FlatStyle.Standard;
                        button.ResetBackColor();
                        button.ResetForeColor();
                        button.UseVisualStyleBackColor = true;
                    }
                    break;
                case CheckBox or RadioButton:
                    ((ButtonBase)c).FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard; // 標準の見た目はダークで白い四角・丸になる
                    break;
                case LinkLabel link:
                    link.LinkColor = link.VisitedLinkColor = dark ? p.SelectionBorder : Color.Empty;
                    link.ActiveLinkColor = dark ? p.SelectionText : Color.Empty;
                    break;
            }
            if (c is ScrollableControl { AutoScroll: true }) Theme.ApplyNativeTheme(c);
            if (c is not (TextBoxBase or ListBox or ListView or ComboBox or NumericUpDown)) Apply(c, form, p);
        }
    }

    /// <summary>一覧（詳細表示）の見出しは標準ではダークにならないので、ダークのときは見出しだけ自分で描く（行は標準のまま）</summary>
    private static void PrepareHeader(ListView view)
    {
        if (view.View != View.Details || view.OwnerDraw) return;
        view.OwnerDraw = true;
        view.DrawItem += (_, e) => e.DrawDefault = true;
        view.DrawSubItem += (_, e) => e.DrawDefault = true;
        view.HandleCreated += (_, _) => ApplyHeaderTheme(view);
        view.DrawColumnHeader += (_, e) =>
        {
            var p = Theme.Current;
            if (!p.IsDark)
            {
                e.DrawDefault = true;
                return;
            }
            using (var bg = new SolidBrush(p.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
            using (var line = new Pen(p.Border))
            {
                e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
            }
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, view.Font, Rectangle.Inflate(e.Bounds, -6, 0), p.TextSecondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
    }

    /// <summary>見出しの右の空き（列の無い所）は Windows のダーク用 / 通常の見た目に</summary>
    private static void ApplyHeaderTheme(ListView view)
    {
        if (!view.IsHandleCreated || view.View != View.Details) return;
        var header = SendMessage(view.Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
        if (header == IntPtr.Zero) return;
        _ = SetWindowTheme(header, Theme.Current.IsDark ? "DarkMode_ItemsView" : "ItemsView", null);
        InvalidateRect(header, IntPtr.Zero, true);
    }

    private const int LvmGetHeader = 0x101F;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);
}
