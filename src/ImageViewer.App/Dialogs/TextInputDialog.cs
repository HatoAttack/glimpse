// 1 行の文字を入力してもらう小さなダイアログ（新しいフォルダーの名前など）。入力のたびに検査し、エラーがあれば OK を押せない
using ImageViewer.App.Theming;

namespace ImageViewer.App.Dialogs;

public sealed class TextInputDialog : ThemedForm
{
    private readonly TextBox _input = new() { Dock = DockStyle.Top };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Theme.Current.Danger, Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 0) };
    private readonly Button _ok = new() { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
    private readonly Func<string, string?> _validate;

    /// <summary>入力された文字（前後の空白を除く）</summary>
    public string Value => _input.Text.Trim();

    /// <param name="validate">入力を検査し、問題があればその理由を返す（無ければ null）</param>
    public TextInputDialog(string title, string prompt, string initial, Func<string, string?> validate)
    {
        _validate = validate;
        Text = title;
        Font = new Font("Yu Gothic UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 130);

        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = _ok;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(8),
        };
        buttons.Controls.AddRange(new Control[] { cancel, _ok });
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0) };
        body.Controls.Add(_error);
        body.Controls.Add(_input);
        body.Controls.Add(new Label { Text = prompt, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 4) });
        Controls.Add(body);
        Controls.Add(buttons);

        _input.Text = initial;
        _input.TextChanged += (_, _) => Validate();
        Shown += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
        Validate();
    }

    private new void Validate()
    {
        string? error = Value.Length == 0 ? "名前を入力してください" : _validate(Value);
        _error.Text = error ?? "";
        _ok.Enabled = error == null;
    }
}
