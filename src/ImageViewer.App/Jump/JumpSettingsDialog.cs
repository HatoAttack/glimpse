// フォルダジャンプの設定: 索引を作る範囲・索引の状態と作り直し・Everything を使うか（任意）
using ImageViewer.Core.Jump;
using ImageViewer.App.Theming;

namespace ImageViewer.App.Jump;

public sealed class JumpSettingsDialog : ThemedForm
{
    private readonly ListBox _roots = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly CheckBox _useEverything = new() { AutoSize = true, Text = "Everything が起動していれば Everything で検索する" };
    private readonly Label _everythingState = new() { AutoSize = true, ForeColor = Theme.Current.TextMuted };
    private readonly FolderJumpService _service;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };

    public IReadOnlyList<string> Roots => _roots.Items.Cast<string>().ToList();
    public bool UseEverything => _useEverything.Checked;

    /// <summary>「今すぐ作り直す」が押された</summary>
    public bool RebuildRequested { get; private set; }

    public JumpSettingsDialog(IReadOnlyList<string> roots, bool useEverything, FolderJumpService service)
    {
        _service = service;
        Text = "フォルダジャンプの設定";
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 420);
        MinimumSize = new Size(480, 380);

        foreach (var r in roots) _roots.Items.Add(r);
        _useEverything.Checked = useEverything;

        var add = new Button { Text = "追加...", AutoSize = true };
        var remove = new Button { Text = "削除", AutoSize = true };
        var rebuild = new Button { Text = "今すぐ作り直す", AutoSize = true };
        add.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "索引に含めるフォルダを選んでください" };
            if (dlg.ShowDialog(this) == DialogResult.OK && !Roots.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase))
                _roots.Items.Add(dlg.SelectedPath);
        };
        remove.Click += (_, _) =>
        {
            if (_roots.SelectedIndex >= 0 && _roots.Items.Count > 1) _roots.Items.RemoveAt(_roots.SelectedIndex);
        };
        rebuild.Click += (_, _) =>
        {
            RebuildRequested = true;
            _service.Rebuild(Roots);
            UpdateStatus();
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "索引に含めるフォルダ（この中のフォルダ名で検索します。隠しフォルダ・node_modules 等は除きます）",
        });
        var rootsRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        rootsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        side.Controls.AddRange(new Control[] { add, remove, rebuild });
        rootsRow.Controls.Add(_roots, 0, 0);
        rootsRow.Controls.Add(side, 1, 0);
        layout.Controls.Add(rootsRow);
        layout.Controls.Add(_status);
        layout.Controls.Add(new Label { AutoSize = true, Padding = new Padding(0, 12, 0, 0), Text = "Everything との連携（任意）" });
        var everything = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        everything.Controls.Add(_useEverything);
        everything.Controls.Add(new Label
        {
            AutoSize = true, ForeColor = Theme.Current.TextMuted, MaximumSize = new Size(520, 0),
            Text = "Everything（voidtools）を自分でインストールして起動している場合だけ使われます。" +
                   "起動していないときは、これまでどおり上の索引で検索します。このアプリには同梱していません。",
        });
        everything.Controls.Add(_everythingState);
        layout.Controls.Add(everything);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        _refresh.Tick += (_, _) => UpdateStatus();
        Shown += (_, _) => _refresh.Start();
        FormClosed += (_, _) => _refresh.Dispose();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var index = _service.Index;
        _status.Text = _service.IsBuilding
            ? "索引を作成中です（低い優先度で動いているので、そのまま使えます）…"
            : index == null
                ? "索引はまだありません"
                : $"索引: フォルダ {index.Count:N0} 件（{index.BuiltUtc.ToLocalTime():yyyy/MM/dd HH:mm} 作成）" +
                  (index.Truncated ? $"　※上限 {FolderIndex.DefaultLimit:N0} 件に達したため、深い階層の一部は入っていません" : "");
        _everythingState.Text = EverythingClient.IsRunning ? "Everything: 起動しています" : "Everything: 起動していません";
    }
}
