// 名前の変更ダイアログ（連番 / 元の名前を元に置換・付け足し）。入力のたびに変更前 → 変更後を一覧で確認できる
using ImageViewer.Core.Rename;
using ImageViewer.App.Theming;

namespace ImageViewer.App.Dialogs;

public sealed class RenameDialog : ThemedForm
{
    private readonly IReadOnlyList<string> _paths;
    private List<RenamePlanItem> _plan = new();

    private readonly RadioButton _useSequence = new() { Text = "文字列＋連番", AutoSize = true, Checked = true };
    private readonly RadioButton _useOriginal = new() { Text = "元の名前を元にする", AutoSize = true };

    private readonly TextBox _prefix = new() { Width = 160 };
    private readonly NumericUpDown _start = new() { Maximum = 999_999_999_999_999_999m, Width = 140 };
    private readonly NumericUpDown _digits = new() { Minimum = 1, Maximum = 18, Width = 60 };
    private readonly NumericUpDown _step = new() { Minimum = 1, Maximum = 1_000_000, Width = 80 };
    private readonly TextBox _seqSuffix = new() { Width = 120 };

    private readonly TextBox _search = new() { Width = 160 };
    private readonly TextBox _replace = new() { Width = 160 };
    private readonly TextBox _suffix = new() { Width = 120 };

    private readonly CheckBox _lowercase = new() { Text = "すべて小文字にする（拡張子も）", AutoSize = true };
    private readonly CheckBox _lowerExt = new() { Text = "拡張子を小文字にする", AutoSize = true };

    private readonly ListView _preview = new()
    {
        View = View.Details, VirtualMode = true, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label _summary = new() { AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 6, 0, 0) };
    private readonly Button _ok = new() { Text = "実行", DialogResult = DialogResult.OK, AutoSize = true };
    private readonly Panel _sequencePanel, _originalPanel;

    /// <summary>実行する名前変更（変更の無いもの・エラーは含まない）。OK で閉じたときだけ有効</summary>
    public IReadOnlyList<RenameOp> Operations =>
        _plan.Where(p => p.Status == RenameStatus.Ok).Select(p => new RenameOp(p.Source, p.Target)).ToList();

    /// <param name="paths">対象（画面の並び順。この順に連番を振る）</param>
    public RenameDialog(IReadOnlyList<string> paths)
    {
        _paths = paths;
        Text = $"名前の変更（{paths.Count} 件）";
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(760, 600);
        MinimumSize = new Size(640, 480);

        // 初期値はファイル名から推測（a260019, a260003, a260000 … → "a"・一番小さい 260000・6 桁）
        var guess = RenamePlanner.GuessSequence(paths.Select(Path.GetFileName).ToList()!);
        _prefix.Text = guess.Prefix;
        _start.Value = guess.Start;
        _digits.Value = guess.Digits;
        _step.Value = 1;
        _lowerExt.Checked = false;

        _sequencePanel = Row(
            Labeled("文字列", _prefix), Labeled("開始番号", _start), Labeled("桁数", _digits),
            Labeled("増分", _step), Labeled("後ろに付ける", _seqSuffix));
        _originalPanel = Row(
            Labeled("置換前", _search), Labeled("置換後", _replace), Labeled("末尾に付ける", _suffix));

        _preview.Columns.Add("変更前", 250);
        _preview.Columns.Add("変更後", 250);
        _preview.Columns.Add("状態", 200);
        _preview.RetrieveVirtualItem += OnRetrieveItem;

        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = _ok;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, AutoSize = true, WrapContents = false,
        };
        buttons.Controls.AddRange(new Control[] { cancel, _ok });
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(10, 6, 10, 6) };
        bottom.Controls.Add(_summary);
        bottom.Controls.Add(buttons);

        var modes = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4, 8, 4, 0) };
        modes.Controls.AddRange(new Control[] { _useSequence, _useOriginal });
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4, 0, 4, 6) };
        options.Controls.AddRange(new Control[] { _lowercase, _lowerExt });
        var hint = new Label
        {
            Text = "画面の並び順（手動で並べ替えた順）に番号を振ります。拡張子はそのまま残ります。",
            AutoSize = true, Dock = DockStyle.Top, ForeColor = Theme.Current.TextMuted, Padding = new Padding(8, 4, 0, 4),
        };
        var previewHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0) };
        previewHost.Controls.Add(_preview);

        // Dock の都合で Fill → Top（下から積む順）→ Bottom の順に追加
        Controls.Add(previewHost);
        Controls.Add(hint);
        Controls.Add(options);
        Controls.Add(_originalPanel);
        Controls.Add(_sequencePanel);
        Controls.Add(modes);
        Controls.Add(bottom);

        foreach (var c in new Control[] { _prefix, _seqSuffix, _search, _replace, _suffix })
            c.TextChanged += (_, _) => UpdatePreview();
        foreach (var n in new[] { _start, _digits, _step })
            n.ValueChanged += (_, _) => UpdatePreview();
        foreach (var r in new[] { _useSequence, _useOriginal })
            r.CheckedChanged += (_, _) => UpdatePreview();
        _lowercase.CheckedChanged += (_, _) =>
        {
            _lowerExt.Enabled = !_lowercase.Checked;
            UpdatePreview();
        };
        _lowerExt.CheckedChanged += (_, _) => UpdatePreview();

        UpdatePreview();
    }

    private RenameOptions CurrentOptions() => new()
    {
        UseSequence = _useSequence.Checked,
        Prefix = _prefix.Text,
        Start = (long)_start.Value,
        Digits = (int)_digits.Value,
        Step = (long)_step.Value,
        SequenceSuffix = _seqSuffix.Text,
        ReplaceSearch = _search.Text,
        ReplaceWith = _replace.Text,
        Suffix = _suffix.Text,
        Lowercase = _lowercase.Checked,
        LowercaseExtension = _lowerExt.Checked,
    };

    private void UpdatePreview()
    {
        _sequencePanel.Visible = _useSequence.Checked;
        _originalPanel.Visible = _useOriginal.Checked;

        _plan = RenamePlanner.Plan(_paths, CurrentOptions());
        _preview.VirtualListSize = _plan.Count;
        _preview.Invalidate();

        int changes = _plan.Count(p => p.Status == RenameStatus.Ok);
        int errors = _plan.Count(p => p.Status == RenameStatus.Error);
        _summary.Text = errors > 0
            ? $"エラーが {errors} 件あります。直すまで実行できません"
            : $"{_plan.Count} 件中 {changes} 件の名前を変更します";
        _summary.ForeColor = errors > 0 ? Theme.Current.Danger : Theme.Current.Text;
        _ok.Enabled = errors == 0 && changes > 0;

        // 最初のエラーが見えるようにする
        int firstError = _plan.FindIndex(p => p.Status == RenameStatus.Error);
        if (firstError >= 0) _preview.EnsureVisible(firstError);
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        var p = _plan[e.ItemIndex];
        string state = p.Status switch
        {
            RenameStatus.Error => p.Error!,
            RenameStatus.Unchanged => "変更なし",
            _ => "",
        };
        e.Item = new ListViewItem(new[] { p.SourceName, p.TargetName, state });
        if (p.Status == RenameStatus.Error) e.Item.ForeColor = Theme.Current.Danger;
        else if (p.Status == RenameStatus.Unchanged) e.Item.ForeColor = Theme.Current.TextMuted;
    }

    private static Panel Row(params Control[] items)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4, 4, 4, 0), WrapContents = true };
        row.Controls.AddRange(items);
        return row;
    }

    private static Control Labeled(string label, Control input)
    {
        var box = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(4, 0, 8, 0) };
        box.Controls.Add(new Label { Text = label, AutoSize = true });
        box.Controls.Add(input);
        return box;
    }
}
