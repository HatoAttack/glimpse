// ダイアログの共通の土台: タイトルバーを今の配色に合わせ、ダークのときは中の標準部品をダークの色にする。
// ライトのときは Windows の標準の見た目のまま（今までと変えない）
using System.Runtime.InteropServices;
using ImageViewer.App.Jump;

namespace ImageViewer.App.Theming;

public class ThemedForm : Form
{
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        if (Theme.Current.IsDark) ApplyDark(this);
        base.OnLoad(e);
    }

    /// <summary>
    /// フォームと中の部品をダークの色に。ラベル・パネル・チェックボックス等は親の色を引き継ぐので、
    /// 引き継がない部品（入力欄・一覧・ボタン）だけ個別に塗る。自前で描いている部品（プレビュー等）は触らない
    /// </summary>
    public static void ApplyDark(Form form)
    {
        var p = Theme.Current;
        form.BackColor = p.Background;
        form.ForeColor = p.Text;
        Apply(form, form, p);
    }

    private static void Apply(Control parent, Form form, Palette p)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case JumpList list:
                    list.ApplyTheme();
                    break;
                case TextBoxBase text:
                    text.BackColor = p.Field;
                    text.ForeColor = p.Text;
                    if (text.BorderStyle == BorderStyle.Fixed3D) text.BorderStyle = BorderStyle.FixedSingle;
                    Theme.ApplyNativeTheme(text);
                    break;
                case NumericUpDown number:
                    number.BackColor = p.Field;
                    number.ForeColor = p.Text;
                    number.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox combo:
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.BackColor = p.Field;
                    combo.ForeColor = p.Text;
                    break;
                case ListBox list:
                    list.BackColor = p.Field;
                    list.ForeColor = p.Text;
                    list.BorderStyle = BorderStyle.None; // 標準の枠は白い線になる。地の色の違いで見分けられる
                    Theme.ApplyNativeTheme(list);
                    break;
                case ListView view:
                    view.BackColor = p.Field;
                    view.ForeColor = p.Text;
                    view.BorderStyle = BorderStyle.None;
                    DrawDarkHeader(view, p);
                    Theme.ApplyNativeTheme(view);
                    break;
                case GroupBox group:
                    // 標準の描き方では、色を変えた見出しの上を枠の線が横切ってしまうので、見出しだけ描き直す
                    group.Paint += (_, e) =>
                    {
                        if (string.IsNullOrEmpty(group.Text)) return;
                        var size = TextRenderer.MeasureText(e.Graphics, group.Text, group.Font, Size.Empty, TextFormatFlags.NoPrefix);
                        int x = group.LogicalToDeviceUnits(6);
                        using (var bg = new SolidBrush(group.BackColor)) e.Graphics.FillRectangle(bg, x, 0, size.Width + 2, size.Height);
                        TextRenderer.DrawText(e.Graphics, group.Text, group.Font, new Point(x + 1, 0), p.Text, TextFormatFlags.NoPrefix);
                    };
                    break;
                case Button button:
                    button.FlatStyle = FlatStyle.Flat;
                    button.UseVisualStyleBackColor = false;
                    button.BackColor = p.Field;
                    button.ForeColor = p.Text;
                    button.FlatAppearance.BorderColor = button == form.AcceptButton ? p.SelectionBorder : p.Border;
                    button.FlatAppearance.MouseOverBackColor = p.Hover;
                    button.FlatAppearance.MouseDownBackColor = p.Pressed;
                    break;
                case CheckBox or RadioButton:
                    ((ButtonBase)c).FlatStyle = FlatStyle.Flat; // 標準の見た目は白い四角・丸になる
                    break;
                case LinkLabel link:
                    link.LinkColor = link.VisitedLinkColor = p.SelectionBorder;
                    link.ActiveLinkColor = p.SelectionText;
                    break;
            }
            if (c is ScrollableControl { AutoScroll: true }) Theme.ApplyNativeTheme(c);
            if (c is not (TextBoxBase or ListBox or ListView or ComboBox or NumericUpDown)) Apply(c, form, p);
        }
    }

    /// <summary>一覧（詳細表示）の見出しは標準では白いままなので、見出しだけ自分で描く（行は標準のまま）</summary>
    private static void DrawDarkHeader(ListView view, Palette p)
    {
        if (view.View != View.Details || view.OwnerDraw) return;
        view.OwnerDraw = true;
        view.DrawItem += (_, e) => e.DrawDefault = true;
        view.DrawSubItem += (_, e) => e.DrawDefault = true;
        // 見出しの右の空き（列の無い所）は Windows のダーク用の見た目に
        void DarkHeader()
        {
            var header = SendMessage(view.Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero) _ = SetWindowTheme(header, "DarkMode_ItemsView", null);
        }
        if (view.IsHandleCreated) DarkHeader();
        else view.HandleCreated += (_, _) => DarkHeader();
        view.DrawColumnHeader += (_, e) =>
        {
            using (var bg = new SolidBrush(p.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
            using (var line = new Pen(p.Border))
            {
                e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
            }
            var text = Rectangle.Inflate(e.Bounds, -6, 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, view.Font, text, p.TextSecondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
    }

    private const int LvmGetHeader = 0x101F;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);
}
