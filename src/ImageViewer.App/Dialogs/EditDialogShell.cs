// 編集ダイアログ（切り抜き / モザイク・ぼかし / 枠・矢印）の共通の体裁: 上に ◀ ▶ と見出し、右に設定、残りがキャンバス
namespace ImageViewer.App.Dialogs;

internal static class EditDialogShell
{
    /// <param name="title">ダイアログの名前（2 枚以上なら「名前（n 枚）」にする）</param>
    public static void Setup(Form form, string title, ImageStepper stepper, EditCanvas canvas, Control side, Size minimumSize)
    {
        form.Text = stepper.Count == 1 ? title : $"{title}（{stepper.Count} 枚）";
        form.Font = new Font("Yu Gothic UI", 9F);
        form.StartPosition = FormStartPosition.CenterParent;
        form.ShowInTaskbar = false;
        form.KeyPreview = true;
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        form.Size = new Size(Math.Min(1200, screen.Width * 9 / 10), Math.Min(960, screen.Height * 9 / 10));
        form.MinimumSize = minimumSize;

        form.Controls.Add(canvas);
        form.Controls.Add(side);
        form.Controls.Add(stepper.Bar);
    }

    /// <summary>右の設定の列（上から順に並べる。収まらなければ縦にスクロール）</summary>
    public static FlowLayoutPanel SidePanel(int width) => new()
    {
        Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        Width = width, Padding = new Padding(6, 4, 6, 4),
    };

    /// <summary>見出し付きの枠に、部品を縦（vertical が false なら横）に並べる</summary>
    public static GroupBox Group(string title, bool vertical, params Control[] controls)
    {
        var stack = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        if (vertical) stack.FlowDirection = FlowDirection.TopDown;
        stack.Controls.AddRange(controls);
        var box = new GroupBox { Text = title, AutoSize = true, Width = 210, Padding = new Padding(8) };
        box.Controls.Add(stack);
        return box;
    }
}
