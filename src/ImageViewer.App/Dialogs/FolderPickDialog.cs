// 「フォルダーへ移動 / コピー」の移動先を選ぶダイアログ。
// フォルダジャンプと同じ検索（名前の一部・あいまい一致・よく開くフォルダが上）で選べる。入力が空なら最近の移動先を出す
using ImageViewer.App.Jump;
using ImageViewer.Core.Jump;
using ImageViewer.Core.Navigation;
using ImageViewer.App.Theming;

namespace ImageViewer.App.Dialogs;

/// <summary>フォルダ名で検索する（結果と、見つからないときのお知らせ）</summary>
public delegate Task<(IReadOnlyList<JumpResult> Results, string? Message)> FolderSearch(string query, CancellationToken ct);

public sealed class FolderPickDialog : ThemedForm
{
    private readonly TextBox _input = new() { Dock = DockStyle.Top };
    private readonly JumpList _list = new() { Dock = DockStyle.Fill, Visible = true, TabStop = false };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Theme.Current.Danger, Dock = DockStyle.Bottom, Padding = new Padding(0, 4, 0, 0) };
    private readonly System.Windows.Forms.Timer _delay = new() { Interval = 120 };
    private readonly IReadOnlyList<string> _recent;
    private readonly FolderSearch _search;
    private CancellationTokenSource? _cts;

    /// <summary>選ばれたフォルダ（OK のとき）</summary>
    public string? SelectedFolder { get; private set; }

    public FolderPickDialog(string title, string prompt, IReadOnlyList<string> recent, FolderSearch search)
    {
        _recent = recent.Where(Directory.Exists).ToList();
        _search = search;
        Text = title;
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 420);
        MinimumSize = new Size(360, 280);
        _list.ItemHeight = Font.Height * 2 + LogicalToDeviceUnits(6);

        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        var browse = new Button { Text = "参照...", AutoSize = true };
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => Accept(_list.SelectedPath);
        browse.Click += (_, _) => Browse();
        _list.Picked += (_, path) => Accept(path);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(8),
        };
        buttons.Controls.AddRange(new Control[] { cancel, ok, browse });
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0) };
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
        listHost.Controls.Add(_list);
        body.Controls.Add(listHost);
        body.Controls.Add(_error);
        body.Controls.Add(_input);
        body.Controls.Add(new Label { Text = prompt, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 4) });
        Controls.Add(body);
        Controls.Add(buttons);

        // 矢印キーは入力欄に置いたまま候補を動かす（アドレスバーのフォルダジャンプと同じ）
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Down or Keys.Up or Keys.PageDown or Keys.PageUp)) return;
            e.SuppressKeyPress = true;
            _list.MoveSelection(e.KeyCode switch
            {
                Keys.Down => 1, Keys.Up => -1, Keys.PageDown => 10, _ => -10,
            });
        };
        _input.TextChanged += (_, _) =>
        {
            _error.Text = "";
            _delay.Stop();
            _delay.Start();
        };
        _delay.Tick += async (_, _) =>
        {
            _delay.Stop();
            await UpdateListAsync();
        };
        Shown += (_, _) => _input.Focus();
        FormClosed += (_, _) =>
        {
            _cts?.Cancel();
            _delay.Dispose();
        };
        ShowRecent();
    }

    private void ShowRecent() =>
        _list.SetResults(_recent.Select(p => new JumpResult(p, DisplayName(p), 0)).ToList(),
            "フォルダー名の一部を入力してください（最近の移動先はここに出ます）");

    private static string DisplayName(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path;
    }

    private async Task UpdateListAsync()
    {
        _cts?.Cancel();
        string query = _input.Text.Trim();
        if (query.Length == 0)
        {
            ShowRecent();
            return;
        }
        if (FolderListing.LooksLikePath(query))
        {
            // パスはそのフォルダだけを候補にする
            _list.SetResults(FolderListing.Normalize(query) is string folder
                ? new[] { new JumpResult(folder, DisplayName(folder), 0) }
                : Array.Empty<JumpResult>(), "フォルダーが見つかりません");
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        IReadOnlyList<JumpResult> results;
        string? message;
        try
        {
            (results, message) = await _search(query, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // 次の入力で検索し直している
        }
        if (cts.IsCancellationRequested || IsDisposed) return;
        _list.SetResults(results, message ?? "見つかりません");
    }

    private void Accept(string? path)
    {
        // 候補が無ければ、入力をパスとして受け付ける
        path ??= FolderListing.Normalize(_input.Text);
        if (path == null || !Directory.Exists(path))
        {
            System.Media.SystemSounds.Beep.Play();
            _error.Text = "フォルダーを選んでください";
            return;
        }
        SelectedFolder = path;
        DialogResult = DialogResult.OK;
    }

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog { InitialDirectory = _list.SelectedPath ?? _recent.FirstOrDefault() ?? "" };
        if (dlg.ShowDialog(this) == DialogResult.OK) Accept(dlg.SelectedPath);
    }
}
