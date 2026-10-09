// 更新の確認（起動時に 1 回。新しければステータスバーとヘルプメニューに出すだけで、更新はユーザーが選んだときだけ）。
// 見つけた新しいバージョン・通信に使う HttpClient・「更新」のメニュー項目は、ここが持つ
using ImageViewer.App.Chrome;
using ImageViewer.App.Commands;
using ImageViewer.App.Dialogs;
using ImageViewer.Core.Updates;

namespace ImageViewer.App;

internal sealed class UpdateFlow : IDisposable
{
    private readonly Form _owner;
    private readonly ISettingsAccess _settings;
    private readonly FooterBar _footer;
    private readonly Action<string> _notify;
    private readonly Func<string?> _currentFolder;
    private HttpClient? _http;
    private ReleaseInfo? _available;

    /// <summary>ヘルプメニューに置く「vX.Y.Z に更新...」（新しいバージョンが見つかるまでは出さない）</summary>
    public ToolStripMenuItem MenuItem { get; }

    /// <param name="currentFolder">今開いているフォルダ（更新した後、同じフォルダを開いた状態で起動し直すため）</param>
    public UpdateFlow(Form owner, ISettingsAccess settings, FooterBar footer, Action<string> notify, Func<string?> currentFolder)
    {
        _owner = owner;
        _settings = settings;
        _footer = footer;
        _notify = notify;
        _currentFolder = currentFolder;
        MenuItem = new ToolStripMenuItem("更新(&N)...", null, (_, _) => ShowDialog()) { Visible = false };
        footer.UpdateClicked += (_, _) => ShowDialog();
    }

    private static Version CurrentVersion => typeof(UpdateFlow).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// 自分で入れ替えられる exe（リリース用の単一ファイル）のパス。
    /// 開発用のビルド（横に Glimpse.dll がある）では null（確認はするが入れ替えない）
    /// </summary>
    private static string? UpdatableExe =>
        Environment.ProcessPath is string exe && !File.Exists(Path.Combine(Path.GetDirectoryName(exe) ?? "", typeof(UpdateFlow).Assembly.GetName().Name + ".dll")) ? exe : null;

    /// <summary>起動時の確認を仕掛ける（設定でオフにしていなければ、ウィンドウが出てから 1 回）</summary>
    public void CheckOnStartup()
    {
        if (UpdatableExe is not string exe) return; // 開発用のビルドでは起動時に確認しない（手動の確認はできる）
        SelfUpdate.CleanUp(exe); // 前回の更新で残った古い exe を消す
        if (_settings.Settings.CheckUpdatesOnStartup ?? true) _owner.Shown += async (_, _) => await CheckAsync(manual: false);
    }

    /// <param name="manual">メニューの「更新を確認」から（結果を必ず知らせる。起動時は新しいバージョンがあるときだけ）</param>
    public async Task CheckAsync(bool manual)
    {
        _http ??= UpdateChecker.CreateClient(CurrentVersion);
        // 起動時は短めに打ち切る（つながらなければ何も出さない）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(manual ? 15 : 5));
        var latest = await UpdateChecker.GetLatestAsync(_http, cts.Token);
        if (latest == null)
        {
            if (manual)
                MessageBox.Show(_owner, "新しいバージョンを確認できませんでした。インターネットにつながっているか確かめてください。", "更新を確認",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!UpdateChecker.IsNewer(latest, CurrentVersion))
        {
            if (manual)
                MessageBox.Show(_owner, $"最新のバージョンです（v{UpdateChecker.Normalize(CurrentVersion)}）。", "更新を確認",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!manual && latest.Tag == _settings.Settings.SkippedVersion) return; // 「このバージョンは飛ばす」を選んだもの

        _available = latest;
        _footer.UpdateText = $"新しいバージョン {latest.Tag} があります（クリックで更新）";
        MenuItem.Text = $"{latest.Tag} に更新(&N)...";
        MenuItem.Visible = true;
        if (manual) ShowDialog();
    }

    private void ShowDialog()
    {
        if (_available is not { } release || _http == null) return;
        UpdateDialog.Outcome outcome;
        using (var dlg = new UpdateDialog(release, CurrentVersion, _http, UpdatableExe))
        {
            dlg.ShowDialog(_owner);
            outcome = dlg.Result;
        }
        switch (outcome)
        {
            case UpdateDialog.Outcome.Skip:
                _settings.UpdateSettings(s => s with { SkippedVersion = release.Tag });
                _footer.UpdateText = "";
                MenuItem.Visible = false;
                _notify($"{release.Tag} は飛ばします（☰ → ヘルプ →「更新を確認」からいつでも更新できます）");
                break;
            case UpdateDialog.Outcome.Updated when UpdatableExe is string exe:
                // 新しい exe で、今のフォルダを開いた状態で起動し直す
                var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
                if (_currentFolder() is string folder) start.ArgumentList.Add(folder);
                System.Diagnostics.Process.Start(start);
                _owner.Close();
                break;
        }
    }

    public void Dispose() => _http?.Dispose();
}
