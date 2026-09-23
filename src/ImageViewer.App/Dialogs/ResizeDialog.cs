// リサイズ・形式変換ダイアログ。設定を変えるたびに「元 → 出力」を一覧で確認でき、実行中は進み具合を表示する
using ImageViewer.Core.Editing;

namespace ImageViewer.App.Dialogs;

public sealed class ResizeDialog : Form
{
    private readonly IReadOnlyList<string> _paths;
    private List<ConvertPlanItem> _plan = new();
    private CancellationTokenSource? _cts;
    private bool _closeAfterCancel;

    // サイズ
    private readonly List<(RadioButton Radio, int Size)> _sizeRadios = new();
    private readonly RadioButton _customSize = new() { Text = "指定:", AutoSize = true };
    private readonly NumericUpDown _customSizeValue = new() { Minimum = 1, Maximum = 65500, Value = 800, Width = 80, TextAlign = HorizontalAlignment.Right };
    private readonly RadioButton _keepSize = new() { Text = "変えない（形式の変換だけ）", AutoSize = true };
    private readonly CheckBox _noUpscale = new() { Text = "長辺が指定より小さい画像は拡大しない", AutoSize = true };

    // 形式・画質
    private readonly List<(RadioButton Radio, OutputFormat Format)> _formatRadios = new();
    private readonly List<(RadioButton Radio, ResizeAlgorithm Algorithm)> _algorithmRadios = new();
    private readonly CheckBox _stripMetadata = new() { Text = "EXIF などのメタデータを消す（色の情報 ICC は残す）", AutoSize = true };

    // ファイル名
    private readonly TextBox _search = new() { Width = 140 };
    private readonly TextBox _replace = new() { Width = 140 };
    private readonly TextBox _suffix = new() { Width = 110 };
    private readonly CheckBox _lowercase = new() { Text = "すべて小文字にする（拡張子も）", AutoSize = true };

    // 出力先
    private readonly RadioButton _toSubfolder = new() { Text = "中のフォルダ:", AutoSize = true };
    private readonly TextBox _subfolderName = new() { Width = 120 };
    private readonly RadioButton _toSame = new() { Text = "同じフォルダ", AutoSize = true };
    private readonly RadioButton _toCustom = new() { Text = "指定:", AutoSize = true };
    private readonly TextBox _customFolder = new() { Width = 250 };
    private readonly Button _browse = new() { Text = "参照...", AutoSize = true };
    private readonly RadioButton _skipExisting = new() { Text = "同名のファイルがあれば飛ばす", AutoSize = true };
    private readonly RadioButton _overwrite = new() { Text = "上書きする", AutoSize = true };

    private readonly ListView _preview = new()
    {
        View = View.Details, VirtualMode = true, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label _summary = new() { AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 6, 0, 0) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 6, Visible = false, Style = ProgressBarStyle.Continuous };
    private readonly Button _run = new() { Text = "実行", AutoSize = true };
    private readonly Button _close = new() { Text = "閉じる", AutoSize = true };
    private readonly Control[] _inputs;

    /// <summary>実行した設定（次回の初期値として保存する用）。実行していなければ null</summary>
    public ConvertOptions? UsedOptions { get; private set; }

    /// <summary>実行結果。実行していなければ null</summary>
    public ConvertResult? Result { get; private set; }

    /// <param name="paths">対象の画像（画面の並び順）</param>
    /// <param name="initial">前回の設定</param>
    public ResizeDialog(IReadOnlyList<string> paths, ConvertOptions initial)
    {
        _paths = paths;
        Text = $"リサイズ・形式変換（{paths.Count} 枚）";
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(780, 680);
        MinimumSize = new Size(640, 560);
        Padding = new Padding(8, 4, 8, 0);

        var sizeRow = Flow();
        foreach (int s in ConvertOptions.SizePresets)
        {
            var rb = new RadioButton { Text = $"長辺 {s}px", AutoSize = true };
            _sizeRadios.Add((rb, s));
            sizeRow.Controls.Add(rb);
        }
        sizeRow.Controls.AddRange(new Control[] { _customSize, _customSizeValue, Caption("px"), _keepSize });
        _customSizeValue.Enter += (_, _) => _customSize.Checked = true;
        var sizeGroup = Group("サイズ", sizeRow, Flow(_noUpscale));

        var formatRow = Flow(Caption("形式:"));
        foreach (var (label, format) in new[] { ("元のまま", OutputFormat.Keep), ("JPG", OutputFormat.Jpeg), ("PNG", OutputFormat.Png), ("WEBP", OutputFormat.Webp) })
        {
            var rb = new RadioButton { Text = label, AutoSize = true };
            _formatRadios.Add((rb, format));
            formatRow.Controls.Add(rb);
        }
        formatRow.Controls.Add(Hint("JPG は透過部分を白に。HEIC・RAW など書き出せない形式の「元のまま」は JPG に"));
        var algorithmRow = Flow(Caption("縮小の方法:"));
        foreach (var a in Enum.GetValues<ResizeAlgorithm>())
        {
            var rb = new RadioButton { Text = a.ToString(), AutoSize = true };
            _algorithmRadios.Add((rb, a));
            algorithmRow.Controls.Add(rb);
        }
        var formatGroup = Group("形式・画質", formatRow, algorithmRow, Flow(_stripMetadata));

        var nameGroup = Group("ファイル名",
            Flow(Caption("置換前"), _search, Caption("→ 置換後"), _replace, Caption("末尾に付ける"), _suffix, Hint("例: _s → photo_s.jpg")),
            Flow(_lowercase));

        _browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "出力先のフォルダ", InitialDirectory = _customFolder.Text };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _customFolder.Text = dlg.SelectedPath;
            _toCustom.Checked = true;
        };
        _subfolderName.Enter += (_, _) => _toSubfolder.Checked = true;
        _customFolder.Enter += (_, _) => _toCustom.Checked = true;
        var outputGroup = Group("出力先",
            Flow(_toSubfolder, _subfolderName, _toSame, _toCustom, _customFolder, _browse),
            Flow(_skipExisting, _overwrite));

        _preview.Columns.Add("元の画像", 230);
        _preview.Columns.Add("出力", 230);
        _preview.Columns.Add("", 230);
        _preview.RetrieveVirtualItem += OnRetrieveItem;
        var previewHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 0) };
        previewHost.Controls.Add(_preview);

        CancelButton = _close;
        _close.Click += (_, _) => Close();
        _run.Click += async (_, _) => await RunAsync();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { _close, _run });
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(10, 6, 10, 6) };
        bottom.Controls.Add(_summary);
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(_progress);

        // Dock の都合で Fill → Top（下から積む順）→ Bottom の順に追加
        Controls.Add(previewHost);
        Controls.Add(outputGroup);
        Controls.Add(nameGroup);
        Controls.Add(formatGroup);
        Controls.Add(sizeGroup);
        Controls.Add(bottom);
        _inputs = new Control[] { sizeGroup, formatGroup, nameGroup, outputGroup };

        Apply(initial);
        foreach (var rb in _sizeRadios.Select(r => r.Radio).Concat(_formatRadios.Select(r => r.Radio)).Concat(_algorithmRadios.Select(r => r.Radio))
                     .Concat(new[] { _customSize, _keepSize, _toSubfolder, _toSame, _toCustom, _skipExisting, _overwrite }))
            rb.CheckedChanged += (_, _) => { if (rb.Checked) UpdatePreview(); };
        foreach (var t in new[] { _search, _replace, _suffix, _subfolderName, _customFolder })
            t.TextChanged += (_, _) => UpdatePreview();
        _lowercase.CheckedChanged += (_, _) => UpdatePreview();
        _customSizeValue.ValueChanged += (_, _) => UpdatePreview();
        _keepSize.CheckedChanged += (_, _) => _noUpscale.Enabled = !_keepSize.Checked;
        _noUpscale.Enabled = !_keepSize.Checked;
        UpdatePreview();
    }

    // ---- 設定 ↔ 画面 ----

    private void Apply(ConvertOptions o)
    {
        var preset = _sizeRadios.FirstOrDefault(r => r.Size == o.LongEdge).Radio;
        if (o.LongEdge == ConvertOptions.KeepSize) _keepSize.Checked = true;
        else if (preset != null) preset.Checked = true;
        else
        {
            _customSize.Checked = true;
            _customSizeValue.Value = Math.Clamp(o.LongEdge, (int)_customSizeValue.Minimum, (int)_customSizeValue.Maximum);
        }
        _noUpscale.Checked = o.NoUpscale;
        (_formatRadios.FirstOrDefault(r => r.Format == o.Format).Radio ?? _formatRadios[0].Radio).Checked = true;
        (_algorithmRadios.FirstOrDefault(r => r.Algorithm == o.Algorithm).Radio ?? _algorithmRadios[^1].Radio).Checked = true;
        _stripMetadata.Checked = o.StripMetadata;
        _search.Text = o.ReplaceSearch;
        _replace.Text = o.ReplaceWith;
        _suffix.Text = o.Suffix;
        _lowercase.Checked = o.Lowercase;
        _subfolderName.Text = string.IsNullOrWhiteSpace(o.SubfolderName) ? "resized" : o.SubfolderName;
        _customFolder.Text = o.CustomFolder ?? "";
        (o.OutputMode switch
        {
            OutputFolderMode.Same => _toSame,
            OutputFolderMode.Custom when !string.IsNullOrWhiteSpace(o.CustomFolder) => _toCustom,
            _ => _toSubfolder,
        }).Checked = true;
        (o.Overwrite ? _overwrite : _skipExisting).Checked = true;
    }

    private ConvertOptions CurrentOptions() => new()
    {
        LongEdge = _keepSize.Checked ? ConvertOptions.KeepSize
            : _customSize.Checked ? (int)_customSizeValue.Value
            : _sizeRadios.FirstOrDefault(r => r.Radio.Checked).Size is int s and > 0 ? s : ConvertOptions.SizePresets[0],
        NoUpscale = _noUpscale.Checked,
        Format = _formatRadios.First(r => r.Radio.Checked).Format,
        Algorithm = _algorithmRadios.First(r => r.Radio.Checked).Algorithm,
        StripMetadata = _stripMetadata.Checked,
        ReplaceSearch = _search.Text,
        ReplaceWith = _replace.Text,
        Suffix = _suffix.Text,
        Lowercase = _lowercase.Checked,
        OutputMode = _toSame.Checked ? OutputFolderMode.Same : _toCustom.Checked ? OutputFolderMode.Custom : OutputFolderMode.Subfolder,
        SubfolderName = _subfolderName.Text.Trim(),
        CustomFolder = string.IsNullOrWhiteSpace(_customFolder.Text) ? null : _customFolder.Text.Trim(),
        Overwrite = _overwrite.Checked,
    };

    /// <summary>出力先の指定の誤り（無ければ null）</summary>
    private string? OutputError(ConvertOptions o) => o.OutputMode switch
    {
        OutputFolderMode.Subfolder when Core.Rename.RenamePlanner.ValidateName(o.SubfolderName) is string e => $"中のフォルダの名前: {e}",
        OutputFolderMode.Custom when o.CustomFolder == null => "出力先のフォルダを指定してください",
        OutputFolderMode.Custom when !Path.IsPathFullyQualified(o.CustomFolder!) => "出力先のフォルダは C:\\… の形で指定してください",
        _ => null,
    };

    private void UpdatePreview()
    {
        var o = CurrentOptions();
        string? outputError = OutputError(o);
        _plan = outputError == null ? Converter.Plan(_paths, o) : new List<ConvertPlanItem>();
        _preview.VirtualListSize = _plan.Count;
        _preview.Invalidate();

        int ok = _plan.Count(p => p.Status == ConvertStatus.Ok);
        int skip = _plan.Count(p => p.Status == ConvertStatus.Skip);
        int errors = _plan.Count(p => p.Status == ConvertStatus.Error);
        int replaces = _plan.Count(p => p.Status == ConvertStatus.Ok && p.ReplacesSource);
        _summary.Text = outputError
            ?? (errors > 0 ? $"エラーが {errors} 件あります。直すまで実行できません"
                : $"{ok} 枚を変換します" + (skip > 0 ? $"（{skip} 枚は飛ばします）" : "") + (replaces > 0 ? $"　元の画像 {replaces} 枚を置き換えます" : ""));
        _summary.ForeColor = outputError != null || errors > 0 ? Color.Firebrick : replaces > 0 ? Color.DarkOrange : SystemColors.ControlText;
        _run.Enabled = outputError == null && errors == 0 && ok > 0;

        int firstError = _plan.FindIndex(p => p.Status == ConvertStatus.Error);
        if (firstError >= 0) _preview.EnsureVisible(firstError);
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        var p = _plan[e.ItemIndex];
        string where = Path.GetDirectoryName(p.Target) is string d && !string.Equals(d, Path.GetDirectoryName(p.Source), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetFileName(d), p.TargetName) : p.TargetName;
        e.Item = new ListViewItem(new[] { p.SourceName, where, p.Note ?? "" });
        e.Item.ForeColor = p.Status switch
        {
            ConvertStatus.Error => Color.Firebrick,
            ConvertStatus.Skip => SystemColors.GrayText,
            _ when p.Note != null => Color.DarkOrange,
            _ => SystemColors.ControlText,
        };
    }

    // ---- 実行 ----

    private async Task RunAsync()
    {
        var options = CurrentOptions();
        var plan = _plan;
        int replaces = plan.Count(p => p.Status == ConvertStatus.Ok && p.ReplacesSource);
        if (replaces > 0 && MessageBox.Show(this,
                $"元の画像 {replaces} 枚が変換結果で置き換わります（元には戻せません）。続けますか？",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        UsedOptions = options;
        foreach (var c in _inputs) c.Enabled = false;
        _run.Enabled = false;
        _close.Text = "中断";
        _progress.Visible = true;
        _progress.Value = 0;
        var cts = _cts = new CancellationTokenSource();
        var progress = new Progress<ConvertProgress>(p =>
        {
            _progress.Maximum = Math.Max(1, p.Total);
            _progress.Value = Math.Min(p.Done, _progress.Maximum);
            _summary.ForeColor = SystemColors.ControlText;
            _summary.Text = p.Done < p.Total ? $"変換中 {p.Done + 1} / {p.Total}: {p.Name}" : "仕上げ中…";
        });
        ConvertResult result;
        try
        {
            result = await Task.Run(() => Converter.Run(plan, options, progress, cts.Token));
        }
        finally
        {
            _cts = null;
            cts.Dispose();
        }
        Result = result;

        _progress.Visible = false;
        _close.Text = "閉じる";
        _summary.Text = Summarize(result);
        _summary.ForeColor = result.Errors.Count > 0 ? Color.Firebrick : SystemColors.ControlText;
        if (_closeAfterCancel)
        {
            Close();
            return;
        }
        if (result.Errors.Count > 0)
            MessageBox.Show(this, string.Join("\n", result.Errors.Take(15)) + (result.Errors.Count > 15 ? $"\n…ほか {result.Errors.Count - 15} 件" : ""),
                $"変換できなかった画像（{result.Errors.Count} 枚）", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public static string Summarize(ConvertResult r) =>
        (r.Canceled ? "中断しました: " : "") + $"{r.Converted} 枚を変換しました"
        + (r.Skipped > 0 ? $"・{r.Skipped} 枚は同名のファイルがあるので飛ばしました" : "")
        + (r.Errors.Count > 0 ? $"・{r.Errors.Count} 枚は失敗しました" : "");

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 実行中は中断を頼んで、終わってから閉じる（書きかけのファイルを残さない）
        if (_cts != null)
        {
            e.Cancel = true;
            _closeAfterCancel = true;
            _cts.Cancel();
            _close.Enabled = false;
            _summary.Text = "中断しています…";
        }
        base.OnFormClosing(e);
    }

    // ---- 部品 ----

    private static FlowLayoutPanel Flow(params Control[] items)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty };
        row.Controls.AddRange(items);
        return row;
    }

    private static GroupBox Group(string title, params Control[] rows)
    {
        var box = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 4) };
        // Dock=Top は後から追加したものが上に来るので逆順に追加
        foreach (var r in rows.Reverse()) box.Controls.Add(r);
        return box;
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 7, 3, 3) };
}
