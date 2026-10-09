// 編集ダイアログ（切り抜き / モザイク・ぼかし / 枠・矢印）の保存まわり:
// - 保存先（元と同じフォルダ / 指定のフォルダ）の選択と確かめ
// - 保存・上書き保存・保存して次へ のボタン（と、一括の「全部を…」）
// - 保存している間は操作を止めて閉じさせない。結果を知らせて、保存した数を数える
// 何をどう保存するか（切り抜く・モザイクをかける・描く）は、渡された関数が行う
using ImageViewer.App.Theming;

namespace ImageViewer.App.Dialogs;

internal sealed class EditSavePanel
{
    private readonly Form _form;
    private readonly RadioButton _toSame = new() { Text = "元と同じフォルダ", AutoSize = true };
    private readonly RadioButton _toCustom = new() { Text = "指定のフォルダ", AutoSize = true };
    private readonly TextBox _folder = new() { Width = 190 };
    // 保存のボタンは「保存（別の名前）」の下に「上書き保存（元の画像を置き換える）」を置く
    private readonly Button _save = new() { Text = "保存", Width = 200, Height = 30 };
    private readonly Button _saveOver = new() { Text = "上書き保存", Width = 200, Height = 30 };
    private readonly Button _saveNext = new() { Text = "保存して次へ (Enter)", Width = 200, Height = 30 };
    private readonly Button _saveNextOver = new() { Text = "上書きして次へ (Shift+Enter)", Width = 200, Height = 30 };
    private readonly Label _saveAllCaption = new() { AutoSize = true, Margin = new Padding(3, 10, 3, 0) };
    private readonly Button _saveAll = new() { Text = "全部を保存", Width = 200, Height = 30 };
    private readonly Button _saveAllOver = new() { Text = "全部を上書き", Width = 200, Height = 30 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(210, 0), Margin = new Padding(3, 8, 3, 3) };

    /// <summary>「保存先」の枠（横のパネルに置く）</summary>
    public Control FolderBox { get; }

    /// <summary>保存のボタンと結果の表示（FolderBox の下に、この順で置く）</summary>
    public Control[] Buttons { get; }

    /// <summary>保存のボタンを押した（次へ進むか, 元の画像を置き換えるか）</summary>
    public event Action<bool, bool>? SaveRequested;

    /// <summary>「全部を…」のボタンを押した（元の画像を置き換えるか）</summary>
    public event Action<bool>? SaveAllRequested;

    /// <summary>保存を始めた・終えた（ボタンの状態を合わせる）</summary>
    public event Action? BusyChanged;

    /// <summary>保存している間（元の画像を使っているので、操作も閉じるのも待たせる）</summary>
    public bool Busy { get; private set; }

    /// <summary>保存したファイルの数（一覧の読み直しの判断用）</summary>
    public int SavedCount { get; private set; }

    /// <param name="hint">保存先の枠の中に出す説明（どんな名前で保存するか）</param>
    /// <param name="withSaveAll">一括の「全部を保存 / 全部を上書き」も置く</param>
    public EditSavePanel(Form form, int imageCount, string hint, string lastFolder, bool lastToCustom, bool withSaveAll)
    {
        _form = form;
        // 保存中は閉じない（元の画像を使っている）
        form.FormClosing += (_, e) => { if (Busy) e.Cancel = true; };

        var browse = new Button { Text = "参照...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "保存先のフォルダ", InitialDirectory = _folder.Text };
            if (dlg.ShowDialog(_form) != DialogResult.OK) return;
            _folder.Text = dlg.SelectedPath;
            _toCustom.Checked = true;
        };
        _folder.Enter += (_, _) => _toCustom.Checked = true;
        _folder.Text = lastFolder;
        (lastToCustom && lastFolder.Length > 0 ? _toCustom : _toSame).Checked = true;
        var outputHint = new Label { AutoSize = true, MaximumSize = new Size(190, 0), Text = hint, ForeColor = Theme.Current.TextMuted };
        var outStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        outStack.Controls.AddRange(new Control[] { _toSame, _toCustom, _folder, browse, outputHint });
        var outBox = new GroupBox { Text = "保存先", AutoSize = true, Width = 210, Padding = new Padding(8), Margin = new Padding(3, 8, 3, 3) };
        outBox.Controls.Add(outStack);
        FolderBox = outBox;

        _save.Click += (_, _) => SaveRequested?.Invoke(false, false);
        _saveOver.Click += (_, _) => SaveRequested?.Invoke(false, true);
        _saveNext.Click += (_, _) => SaveRequested?.Invoke(true, false);
        _saveNextOver.Click += (_, _) => SaveRequested?.Invoke(true, true);
        _saveAll.Click += (_, _) => SaveAllRequested?.Invoke(false);
        _saveAllOver.Click += (_, _) => SaveAllRequested?.Invoke(true);
        _saveNext.Visible = _saveNextOver.Visible = imageCount > 1;
        _saveAllCaption.Visible = _saveAll.Visible = _saveAllOver.Visible = false;
        Buttons = withSaveAll
            ? new Control[] { _save, _saveOver, _saveNext, _saveNextOver, _saveAllCaption, _saveAll, _saveAllOver, _status }
            : new Control[] { _save, _saveOver, _saveNext, _saveNextOver, _status };
    }

    /// <summary>前回の設定として覚える値</summary>
    public string FolderText => _folder.Text.Trim();
    public bool ToCustom => _toCustom.Checked;

    /// <summary>1 枚の保存のボタンを使えるか</summary>
    public bool SaveEnabled
    {
        set => _save.Enabled = _saveOver.Enabled = _saveNext.Enabled = _saveNextOver.Enabled = value;
    }

    /// <summary>「全部を…」を出すか・使えるか・見出し（出さないなら caption は null のまま）</summary>
    public void SetSaveAll(bool visible, bool enabled, string? caption = null)
    {
        _saveAllCaption.Visible = _saveAll.Visible = _saveAllOver.Visible = visible;
        _saveAll.Enabled = _saveAllOver.Enabled = enabled;
        if (caption != null) _saveAllCaption.Text = caption;
    }

    /// <summary>補足を控えめな色で出す</summary>
    public void ShowNotice(string text)
    {
        _status.ForeColor = Theme.Current.TextMuted;
        _status.Text = text;
    }

    /// <summary>prefix で始まる表示が出ていれば消す（前の画像で出したお知らせ）</summary>
    public void ClearNotice(string prefix)
    {
        if (_status.Text.StartsWith(prefix, StringComparison.Ordinal)) _status.Text = "";
    }

    /// <summary>保存先のフォルダ（指定のフォルダが正しくなければメッセージを出して null）。上書きなら元と同じフォルダ</summary>
    public string? FolderFor(string source, bool overwrite)
    {
        if (_toSame.Checked || overwrite) return Path.GetDirectoryName(source)!;
        string folder = _folder.Text.Trim();
        if (folder.Length > 0 && Path.IsPathFullyQualified(folder)) return folder;
        MessageBox.Show(_form, "保存先のフォルダを C:\\… の形で指定してください。", _form.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return null;
    }

    /// <summary>
    /// 1 枚を保存する。save は別スレッドで呼ばれ、実際に保存したパスを返す。
    /// 保存できたら saved（上書きした編集を捨てる など）を呼んで true、できなければ理由を出して false
    /// </summary>
    /// <param name="detail">「保存しました: 名前」の後ろに付ける補足（切り抜いた大きさ など）</param>
    public async Task<bool> SaveOneAsync(Func<string> save, bool overwrite, string detail = "", Action? saved = null)
    {
        SetBusy(true);
        try
        {
            string dst = await Task.Run(save);
            SavedCount++;
            _status.ForeColor = Theme.Current.Text;
            _status.Text = $"{(overwrite ? "上書きしました" : "保存しました")}: {Path.GetFileName(dst)}{detail}";
            saved?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.ForeColor = Theme.Current.Danger;
            _status.Text = $"保存できませんでした: {ex.Message}";
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 選んだ画像を全部保存する。saveOne(元, 保存先のフォルダ) は 1 枚ずつ別スレッドで呼ばれる（上書きなら元を置き換える）。
    /// 保存先の指定が正しくなければメッセージを出して何もしない。保存できた数を返す
    /// </summary>
    /// <param name="progressWord">進み具合の頭に出す言葉（「切り抜き中」「保存中」）</param>
    /// <param name="failedTitle">失敗した画像の一覧の見出し（「切り抜けなかった画像」など）</param>
    public async Task<int> SaveAllAsync(IReadOnlyList<string> paths, Func<string, string, string> saveOne, bool overwrite,
        string progressWord, string failedTitle)
    {
        if (!overwrite && _toCustom.Checked && FolderFor(paths[0], overwrite) == null) return 0;
        var targets = paths.Select(p => (Src: p, Folder: _toCustom.Checked && !overwrite ? _folder.Text.Trim() : Path.GetDirectoryName(p)!)).ToList();
        SetBusy(true);
        var errors = new List<string>();
        int ok = 0;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var (src, folder) = targets[i];
                _status.ForeColor = Theme.Current.Text;
                _status.Text = $"{progressWord} {i + 1} / {targets.Count}: {Path.GetFileName(src)}";
                try
                {
                    await Task.Run(() => saveOne(src, folder));
                    ok++;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    errors.Add($"{Path.GetFileName(src)}: {ex.Message}");
                }
            }
        }
        finally
        {
            SavedCount += ok;
            SetBusy(false);
        }
        _status.ForeColor = errors.Count > 0 ? Theme.Current.Danger : Theme.Current.Text;
        _status.Text = $"{ok} 枚を{(overwrite ? "上書き" : "保存")}しました" + (errors.Count > 0 ? $"・{errors.Count} 枚は失敗しました" : "");
        if (errors.Count > 0)
            MessageBox.Show(_form, string.Join("\n", errors.Take(15)), $"{failedTitle}（{errors.Count} 枚）", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return ok;
    }

    private void SetBusy(bool busy)
    {
        Busy = busy;
        _form.UseWaitCursor = busy;
        BusyChanged?.Invoke();
    }
}
