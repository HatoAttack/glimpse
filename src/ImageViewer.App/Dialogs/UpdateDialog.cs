// 新しいバージョンのお知らせと更新。「更新する」を押したときだけダウンロードし、
// チェックサムが合えば exe を入れ替える（再起動は呼び出し側）。合わない・書き込めないときは入れ替えない
using System.Diagnostics;
using ImageViewer.Core.Updates;
using ImageViewer.App.Theming;

namespace ImageViewer.App.Dialogs;

public sealed class UpdateDialog : ThemedForm
{
    public enum Outcome { Later, Skip, Updated }

    private readonly ReleaseInfo _release;
    private readonly HttpClient _http;
    private readonly string? _exePath;
    private readonly Button _update = new() { Text = "更新する", AutoSize = true };
    private readonly Button _skip = new() { Text = "このバージョンは飛ばす", AutoSize = true };
    private readonly Button _later = new() { Text = "後で", AutoSize = true };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Bottom, Visible = false, Height = 16 };
    private readonly Label _state = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 2) };
    private CancellationTokenSource? _cts;

    public Outcome Result { get; private set; } = Outcome.Later;

    /// <param name="exePath">入れ替える exe（開発用のビルドなど入れ替えられないときは null。リリースのページを開く）</param>
    public UpdateDialog(ReleaseInfo release, Version current, HttpClient http, string? exePath)
    {
        _release = release;
        _http = http;
        _exePath = exePath;
        Text = "新しいバージョン";
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 420);
        MinimumSize = new Size(380, 300);

        var heading = new Label
        {
            Text = $"新しいバージョン {release.Tag} があります（今のバージョン: v{UpdateChecker.Normalize(current)}）",
            AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 6),
        };
        var notes = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Text = UpdateChecker.NotesToPlainText(release.Notes), BackColor = SystemColors.Window,
            TabStop = false, // 最初にフォーカスが入ると全体が選択された状態で開いてしまう
        };
        var pageLink = new LinkLabel { Text = "リリースのページを開く", AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 6, 0, 0) };
        pageLink.LinkClicked += (_, _) => OpenPage();

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0) };
        body.Controls.Add(notes);
        body.Controls.Add(pageLink);
        body.Controls.Add(_state);
        body.Controls.Add(_progress);
        body.Controls.Add(heading);
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(8),
        };
        buttons.Controls.AddRange(new Control[] { _later, _skip, _update });
        Controls.Add(body);
        Controls.Add(buttons);
        AcceptButton = _update;
        CancelButton = _later;
        Shown += (_, _) => _update.Focus();

        if (exePath == null || release.ExeUrl == null || release.Sha256Url == null)
        {
            // 自分では入れ替えられない（開発用のビルド・exe やチェックサムが付いていないリリース）
            _update.Text = "ダウンロードのページを開く";
            _state.Text = exePath == null ? "このビルドは自動で更新できません。" : "このリリースは自動で更新できません。";
        }

        _update.Click += async (_, _) => await UpdateAsync();
        _skip.Click += (_, _) =>
        {
            Result = Outcome.Skip;
            Close();
        };
        _later.Click += (_, _) =>
        {
            if (_cts != null) _cts.Cancel(); // ダウンロード中なら止める
            else Close();
        };
        FormClosing += (_, e) =>
        {
            // ダウンロード中に閉じるときは止めてから（途中のファイルは UpdateAsync が消す）
            if (_cts == null) return;
            _cts.Cancel();
            e.Cancel = true;
        };
    }

    private void OpenPage()
    {
        try { Process.Start(new ProcessStartInfo(_release.PageUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private async Task UpdateAsync()
    {
        if (_exePath == null || _release.ExeUrl == null || _release.Sha256Url == null)
        {
            OpenPage();
            Close();
            return;
        }

        string exe = _exePath, temp = SelfUpdate.NewPath(_exePath);
        var cts = _cts = new CancellationTokenSource();
        _update.Enabled = _skip.Enabled = false;
        _later.Text = "中止";
        _progress.Visible = true;
        _state.Text = "ダウンロード中…";
        try
        {
            string? expected = UpdateChecker.ParseSha256(await _http.GetStringAsync(_release.Sha256Url, cts.Token));
            if (expected == null) throw new InvalidDataException("チェックサムのファイルが読めませんでした。");
            var progress = new Progress<(long Done, long? Total)>(p =>
            {
                long total = p.Total ?? _release.ExeSize;
                if (total > 0) _progress.Value = (int)Math.Clamp(p.Done * 100 / total, 0, 100);
                _state.Text = $"ダウンロード中… {p.Done / 1024 / 1024} / {Math.Max(total, p.Done) / 1024 / 1024} MB";
            });
            await SelfUpdate.DownloadAsync(_http, _release.ExeUrl, temp, progress, cts.Token);

            _state.Text = "確認中…";
            string actual = await Task.Run(() => SelfUpdate.ComputeSha256(temp), cts.Token);
            if (actual != expected) throw new InvalidDataException("ダウンロードしたファイルが壊れています（チェックサムが一致しません）。");
            SelfUpdate.Swap(exe, temp);
            _cts = null;
            Result = Outcome.Updated;
            Close();
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException
                                       or UnauthorizedAccessException or InvalidDataException)
        {
            _cts = null;
            TryDelete(temp);
            if (ex is OperationCanceledException)
            {
                Close();
                return;
            }
            string hint = ex is UnauthorizedAccessException
                ? "\n\nこの exe の置き場所には書き込めません。リリースのページからダウンロードして置き換えてください。" : "";
            MessageBox.Show(this, ex.Message + hint, "更新できませんでした", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _update.Enabled = _skip.Enabled = true;
            _later.Text = "後で";
            _progress.Visible = false;
            _state.Text = "";
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
