// 編集ダイアログの右の列に置く、見出しのボタンで開け閉めできる枠（閉じている間は見出しのボタンだけ）
namespace ImageViewer.App.Dialogs;

internal sealed class CollapsibleSection : FlowLayoutPanel
{
    private readonly string _title;
    private readonly Button _header = new() { Width = 210, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 6, 3, 0) };
    private readonly GroupBox _body;
    private bool _open; // Visible は親が出ていないと false を返すので、自分で覚える

    /// <param name="open">初めから開いておくか</param>
    public CollapsibleSection(string title, bool open, params Control[] controls)
    {
        _title = title;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        Margin = Padding.Empty;
        _body = EditDialogShell.Group("", vertical: true, controls);
        _body.Margin = new Padding(3, 0, 3, 3);
        _header.Click += (_, _) => Open = !Open;
        Controls.Add(_header);
        Controls.Add(_body);
        Open = open;
    }

    /// <summary>開いているか（前回の状態として覚えておく値にも使う）</summary>
    public bool Open
    {
        get => _open;
        set
        {
            _open = value;
            _body.Visible = value;
            _header.Text = (value ? "▾ " : "▸ ") + _title;
        }
    }
}
