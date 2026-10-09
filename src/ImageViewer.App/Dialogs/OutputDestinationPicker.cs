// 一括処理（リサイズ・形式変換 / まとめて補正）の出力先の選択: 中のフォルダ / 同じフォルダ / 指定のフォルダ と、同名のファイルの扱い。
// 部品を持つだけで、並べるのは各ダイアログ（行の作り方がダイアログごとにあるので、部品の並びを渡す）
using ImageViewer.Core.Editing;

namespace ImageViewer.App.Dialogs;

internal sealed class OutputDestinationPicker
{
    private readonly RadioButton _toSubfolder = new() { Text = "中のフォルダ:", AutoSize = true };
    private readonly TextBox _subfolderName = new() { Width = 120 };
    private readonly RadioButton _toSame = new() { Text = "同じフォルダ", AutoSize = true };
    private readonly RadioButton _toCustom = new() { Text = "指定:", AutoSize = true };
    private readonly TextBox _customFolder = new() { Width = 250 };
    private readonly Button _browse = new() { Text = "参照...", AutoSize = true };
    private readonly RadioButton _skipExisting = new() { Text = "同名のファイルがあれば飛ばす", AutoSize = true };
    private readonly RadioButton _overwrite = new() { Text = "上書きする", AutoSize = true };

    /// <summary>選び直した・フォルダの名前を書き換えた（計画を作り直す）</summary>
    public event Action? Changed;

    public OutputDestinationPicker()
    {
        _browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "出力先のフォルダ", InitialDirectory = _customFolder.Text };
            if (dlg.ShowDialog(_browse.FindForm()) != DialogResult.OK) return;
            _customFolder.Text = dlg.SelectedPath;
            _toCustom.Checked = true;
        };
        _subfolderName.Enter += (_, _) => _toSubfolder.Checked = true;
        _customFolder.Enter += (_, _) => _toCustom.Checked = true;
        foreach (var rb in new[] { _toSubfolder, _toSame, _toCustom, _skipExisting, _overwrite })
            rb.CheckedChanged += (_, _) => { if (rb.Checked) Changed?.Invoke(); };
        foreach (var t in new[] { _subfolderName, _customFolder })
            t.TextChanged += (_, _) => Changed?.Invoke();
    }

    /// <summary>フォルダの決め方の行に並べる部品</summary>
    public Control[] FolderRow => new Control[] { _toSubfolder, _subfolderName, _toSame, _toCustom, _customFolder, _browse };

    /// <summary>同名のファイルの扱いの行に並べる部品</summary>
    public Control[] ExistingRow => new Control[] { _skipExisting, _overwrite };

    /// <summary>設定を画面に写す</summary>
    /// <param name="defaultSubfolder">中のフォルダの名前が空のときに入れる名前</param>
    public void Show(OutputDestination o, string defaultSubfolder)
    {
        _subfolderName.Text = string.IsNullOrWhiteSpace(o.SubfolderName) ? defaultSubfolder : o.SubfolderName;
        _customFolder.Text = o.CustomFolder ?? "";
        (o.Mode switch
        {
            OutputFolderMode.Same => _toSame,
            OutputFolderMode.Custom when !string.IsNullOrWhiteSpace(o.CustomFolder) => _toCustom,
            _ => _toSubfolder,
        }).Checked = true;
        (o.Overwrite ? _overwrite : _skipExisting).Checked = true;
    }

    /// <summary>画面の今の設定</summary>
    public OutputDestination Current => new(
        _toSame.Checked ? OutputFolderMode.Same : _toCustom.Checked ? OutputFolderMode.Custom : OutputFolderMode.Subfolder,
        _subfolderName.Text.Trim(),
        string.IsNullOrWhiteSpace(_customFolder.Text) ? null : _customFolder.Text.Trim(),
        _overwrite.Checked);
}
