// まとめて補正のダイアログ（一覧のフッターの「補正」から）。
// 左に代表の 1 枚（◀ ▶ で選んだ画像を切り替えて確かめられる）、右に 1 枚表示と同じ補正パネル、下に出力先と「元 → 出力」の一覧。
// プレビューは画面の大きさに縮小して読んだものに補正をかける（保存は原寸で読み直してかける）
using ImageViewer.App.Theming;
using ImageViewer.App.Viewer;
using ImageViewer.Core.Editing;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Thumbnails;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageViewer.App.Dialogs;

public sealed class AdjustBatchDialog : ThemedForm
{
    /// <summary>プレビュー用に読む大きさ（長辺）</summary>
    private const int PreviewEdge = 1400;

    private readonly IReadOnlyList<string> _paths;
    private List<ConvertPlanItem> _plan = new();
    private CancellationTokenSource? _runCts, _loadCts;
    private bool _closeAfterCancel;

    // プレビュー
    private int _index = -1;
    private SixLabors.ImageSharp.Image<Rgba32>? _source; // 縮小して読んだ代表の画像（補正前）
    private AdjustOptions? _auto;                        // その画像の自動補正の値
    private Bitmap? _original, _adjusted;                 // 補正前 / 補正後の表示用
    private AdjustOptions? _adjustedWith;
    private bool _comparing;
    private string? _loadError;

    private readonly PreviewCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly Label _name = new() { AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
    private readonly Button _prev = new() { Text = "◀ 前", AutoSize = true };
    private readonly Button _next = new() { Text = "次 ▶", AutoSize = true };
    private readonly AdjustPanel _panel = new(Theme.Current, showSave: false) { Dock = DockStyle.Fill };
    private readonly CheckBox _autoLevels = new() { Text = "レベル補正は 1 枚ずつ自動で決める", AutoSize = true, Dock = DockStyle.Top };
    private readonly System.Windows.Forms.Timer _render = new() { Interval = 15 };

    // 出力先
    private readonly OutputDestinationPicker _output = new();

    private readonly ListView _list = new()
    {
        View = View.Details, VirtualMode = true, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label _summary = new() { AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 6, 0, 0) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 6, Visible = false, Style = ProgressBarStyle.Continuous };
    private readonly Button _run = new() { Text = "実行", Width = 110 };
    private readonly Button _runOver = new() { Text = "上書き保存", Width = 110 };
    private readonly Button _close = new() { Text = "閉じる", AutoSize = true };
    private readonly Control[] _inputs;

    /// <summary>実行したときの補正の値（「前回の補正」として覚える用。1 枚ずつ自動のときレベルは既定）。実行していなければ null</summary>
    public AdjustOptions? UsedAdjust { get; private set; }

    /// <summary>実行したときの設定（次回の初期値として保存する用）。実行していなければ null</summary>
    public AdjustBatchOptions? UsedOptions { get; private set; }

    /// <summary>実行結果。実行していなければ null</summary>
    public ConvertResult? Result { get; private set; }

    /// <param name="paths">対象の画像（画面の並び順）</param>
    /// <param name="last">前回の補正（最初に入れておき、「前回の補正」のボタンでも入れられる。無ければ既定から）</param>
    /// <param name="initial">前回の設定</param>
    public AdjustBatchDialog(IReadOnlyList<string> paths, AdjustOptions? last, AdjustBatchOptions initial)
    {
        _paths = paths;
        Text = $"補正（{paths.Count} 枚）";
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(1100, screen.Width * 9 / 10), Math.Min(940, screen.Height * 9 / 10));
        MinimumSize = new Size(760, 600);

        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6, 4, 6, 0) };
        _prev.Click += async (_, _) => await StepAsync(-1);
        _next.Click += async (_, _) => await StepAsync(1);
        _prev.Visible = _next.Visible = paths.Count > 1;
        top.Controls.AddRange(new Control[] { _prev, _next, _name });

        _canvas.Paint += Canvas_Paint;
        _canvas.Resize += (_, _) => _canvas.Invalidate();

        var side = new Panel { Dock = DockStyle.Right, Width = _panel.Width };
        var autoHost = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(52), Padding = new Padding(14, 4, 8, 4), BackColor = Theme.Current.Surface };
        var autoHint = new Label
        {
            Text = "明るさの違う写真をそろえるとき向け", AutoSize = true, Dock = DockStyle.Top, ForeColor = Theme.Current.TextMuted, Padding = new Padding(18, 0, 0, 0),
        };
        autoHost.Controls.Add(autoHint);
        autoHost.Controls.Add(_autoLevels);
        side.Controls.Add(_panel);
        side.Controls.Add(autoHost);
        _panel.Last = last;
        _panel.Options = last ?? new AdjustOptions();
        _panel.OptionsChanged += (_, _) => { QueueRender(); UpdateSummary(); };
        _panel.AutoRequested += (_, _) =>
        {
            if (_auto == null) return;
            _panel.Options = _panel.Options.WithLevels(_auto);
            if (_auto.IsIdentity) _name.Text = $"{NameLine()}  （自動補正: 直すところが見つかりませんでした）";
        };
        _panel.CompareChanged += (_, on) =>
        {
            _comparing = on;
            _canvas.Invalidate();
        };
        _autoLevels.CheckedChanged += (_, _) =>
        {
            _panel.LevelsEnabled = !_autoLevels.Checked;
            QueueRender();
            UpdateSummary();
        };
        _render.Tick += (_, _) =>
        {
            _render.Stop();
            RebuildAdjusted();
        };

        var outputGroup = Group("出力先",
            Flow(_output.FolderRow),
            Flow(_output.ExistingRow.Append(Hint("形式は元のまま。HEIC・RAW など書き出せない形式は同じ名前の JPG に")).ToArray()));
        outputGroup.Dock = DockStyle.Bottom;

        _list.Columns.Add("元の画像", 260);
        _list.Columns.Add("出力", 260);
        _list.Columns.Add("", 280);
        _list.RetrieveVirtualItem += OnRetrieveItem;
        var listHost = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(100), Padding = new Padding(10, 4, 10, 0) };
        listHost.Controls.Add(_list);

        CancelButton = _close;
        _close.Click += (_, _) => Close();
        _run.Click += async (_, _) => await RunAsync(overwriteSources: false);
        _runOver.Click += async (_, _) => await RunAsync(overwriteSources: true);
        new ToolTip().SetToolTip(_runOver, "出力先の設定に関わらず、元の画像を補正した画像で置き換えます（元には戻せません）。\n" +
                                           "HEIC・RAW など書き出せない形式は、元を残して同じ名前の JPG に保存します");
        // 「実行」の下に「上書き保存」を置く
        var runStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        runStack.Controls.AddRange(new Control[] { _run, _runOver });
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { _close, runStack });
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 78, Padding = new Padding(10, 6, 10, 6) };
        bottom.Controls.Add(_summary);
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(_progress);

        // Dock は後から追加したものが外側になる: 下（ボタン → 出力先 → 一覧）・上・右の順に外から取り、残りがプレビュー
        Controls.Add(_canvas);
        Controls.Add(side);
        Controls.Add(top);
        Controls.Add(listHost);
        Controls.Add(outputGroup);
        Controls.Add(bottom);
        _inputs = new Control[] { side, outputGroup, _prev, _next };

        Apply(initial);
        _output.Changed += UpdatePlan;
        UpdatePlan();

        Shown += async (_, _) => await ShowIndexAsync(0);
        FormClosed += (_, _) =>
        {
            _loadCts?.Cancel();
            _render.Dispose();
            _source?.Dispose();
            _original?.Dispose();
            _adjusted?.Dispose();
        };
    }

    // ---- 設定 ↔ 画面 ----

    private void Apply(AdjustBatchOptions o)
    {
        _autoLevels.Checked = o.AutoLevels;
        _panel.LevelsEnabled = !o.AutoLevels;
        _output.Show(o.Output(), "adjusted");
    }

    private AdjustBatchOptions CurrentOptions() => new()
    {
        AutoLevels = _autoLevels.Checked,
        OutputMode = _output.Current.Mode,
        SubfolderName = _output.Current.SubfolderName,
        CustomFolder = _output.Current.CustomFolder,
        Overwrite = _output.Current.Overwrite,
    };

    /// <summary>補正の値（1 枚ずつ自動で決めるときは、レベル補正を既定にしておく。実際の値は画像ごとに決まる）</summary>
    private AdjustOptions CurrentAdjust()
    {
        var adjust = _panel.Options;
        return _autoLevels.Checked ? adjust.WithLevels(new AdjustOptions()) : adjust;
    }

    // ---- 出力の一覧 ----

    private string? _outputError;

    private void UpdatePlan()
    {
        var o = CurrentOptions();
        _outputError = o.Output().Validate();
        _plan = _outputError == null ? BatchAdjuster.Plan(_paths, o) : new List<ConvertPlanItem>();
        _list.VirtualListSize = _plan.Count;
        _list.Invalidate();
        int firstError = _plan.FindIndex(p => p.Status == ConvertStatus.Error);
        if (firstError >= 0) _list.EnsureVisible(firstError);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_runCts != null) return;
        int ok = _plan.Count(p => p.Status == ConvertStatus.Ok);
        int skip = _plan.Count(p => p.Status == ConvertStatus.Skip);
        int errors = _plan.Count(p => p.Status == ConvertStatus.Error);
        int replaces = _plan.Count(p => p.Status == ConvertStatus.Ok && p.ReplacesSource);
        bool work = BatchAdjuster.HasWork(CurrentAdjust(), CurrentOptions());
        _summary.Text = _outputError
            ?? (errors > 0 ? $"エラーが {errors} 件あります。直すまで実行できません"
                : !work ? "補正の値がまだ既定のままです"
                : $"{ok} 枚を補正します" + (skip > 0 ? $"（{skip} 枚は飛ばします）" : "") + (replaces > 0 ? $"　元の画像 {replaces} 枚を上書きします" : ""));
        _summary.ForeColor = _outputError != null || errors > 0 ? Theme.Current.Danger
            : work && replaces > 0 ? Theme.Current.Warning : Theme.Current.Text;
        _run.Enabled = _outputError == null && errors == 0 && ok > 0 && work;
        _runOver.Enabled = work && _paths.Count > 0;
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        var p = _plan[e.ItemIndex];
        string where = Path.GetDirectoryName(p.Target) is string d && !string.Equals(d, Path.GetDirectoryName(p.Source), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetFileName(d), p.TargetName) : p.TargetName;
        e.Item = new ListViewItem(new[] { p.SourceName, where, p.Note ?? "" });
        e.Item.ForeColor = p.Status switch
        {
            ConvertStatus.Error => Theme.Current.Danger,
            ConvertStatus.Skip => Theme.Current.TextMuted,
            _ when p.Note != null => Theme.Current.Warning,
            _ => Theme.Current.Text,
        };
    }

    // ---- プレビュー ----

    private string NameLine() => _paths.Count > 1
        ? $"[{_index + 1}/{_paths.Count}] {Path.GetFileName(_paths[_index])}"
        : Path.GetFileName(_paths[_index]);

    private async Task StepAsync(int delta) =>
        await ShowIndexAsync(((_index + delta) % _paths.Count + _paths.Count) % _paths.Count);

    private async Task ShowIndexAsync(int index)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        _index = index;
        string path = _paths[index];
        _name.Text = $"{NameLine()}  （読み込み中…）";
        SixLabors.ImageSharp.Image<Rgba32> image;
        AdjustOptions auto;
        Bitmap original;
        try
        {
            (image, auto, original) = await Task.Run(() =>
            {
                var img = ImageLoader.Load(path, LoadOptions.Thumbnail(PreviewEdge));
                return (img, Adjuster.Auto(img), ThumbnailGenerator.ToPArgbBitmap(img));
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (cts.IsCancellationRequested) return;
            SetPreview(null, null, null);
            _loadError = ex.Message;
            _name.Text = $"{NameLine()}  （読み込めません: {ex.Message}）";
            _canvas.Invalidate();
            return;
        }
        if (cts.IsCancellationRequested || IsDisposed)
        {
            image.Dispose();
            original.Dispose();
            return;
        }
        SetPreview(image, auto, original);
        _name.Text = NameLine();
        RebuildAdjusted();
    }

    private void SetPreview(SixLabors.ImageSharp.Image<Rgba32>? image, AdjustOptions? auto, Bitmap? original)
    {
        _source?.Dispose();
        _original?.Dispose();
        _adjusted?.Dispose();
        (_source, _auto, _original, _adjusted, _adjustedWith, _loadError) = (image, auto, original, null, null, null);
    }

    /// <summary>スライダーを続けて動かしても、描き直しは少し間をおいて 1 回にまとめる</summary>
    private void QueueRender()
    {
        _render.Stop();
        _render.Start();
    }

    /// <summary>この画像にかかる値（1 枚ずつ自動なら、この画像の自動補正のレベル）</summary>
    private AdjustOptions EffectiveAdjust() =>
        _autoLevels.Checked && _auto != null ? _panel.Options.WithLevels(_auto) : CurrentAdjust();

    private void RebuildAdjusted()
    {
        if (_source == null) return;
        var options = EffectiveAdjust();
        if (_adjusted != null && _adjustedWith == options) return;
        _adjusted?.Dispose();
        _adjusted = null;
        if (!options.IsIdentity)
        {
            using var copy = _source.Clone();
            Adjuster.Apply(copy, options);
            _adjusted = ThumbnailGenerator.ToPArgbBitmap(copy);
        }
        _adjustedWith = options;
        _canvas.Invalidate();
    }

    private void Canvas_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.FromArgb(32, 32, 32));
        var shown = !_comparing && _adjusted != null ? _adjusted : _original;
        if (shown == null)
        {
            if (_loadError != null)
                TextRenderer.DrawText(g, "読み込めません", Font, _canvas.ClientRectangle, Color.Gainsboro,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var area = _canvas.ClientRectangle;
        area.Inflate(-8, -8);
        double scale = Math.Min(1, Math.Min((double)area.Width / shown.Width, (double)area.Height / shown.Height));
        int w = Math.Max(1, (int)Math.Round(shown.Width * scale)), h = Math.Max(1, (int)Math.Round(shown.Height * scale));
        var r = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(shown, r);
        if (_comparing)
            TextRenderer.DrawText(g, "補正前", Font, new Point(r.X + 8, r.Y + 8), Color.White);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_paths.Count > 1 && _runCts == null && e.KeyCode is Keys.PageUp or Keys.PageDown)
        {
            e.Handled = true;
            _ = StepAsync(e.KeyCode == Keys.PageUp ? -1 : 1);
        }
    }

    // ---- 実行 ----

    /// <param name="overwriteSources">
    /// true なら出力先の設定に関わらず元の画像を置き換える（確かめない）。書き出せない形式（HEIC など）は同じフォルダに同じ名前の JPG を作るが、
    /// その名前のファイルがあれば（実行している間にできたものでも）別の画像なので上書きせずに飛ばす
    /// </param>
    private async Task RunAsync(bool overwriteSources)
    {
        var options = CurrentOptions();
        var adjust = CurrentAdjust();
        var runOptions = options;
        var plan = _plan;
        if (overwriteSources)
        {
            runOptions = options with { OutputMode = OutputFolderMode.Same, Overwrite = true };
            plan = BatchAdjuster.Plan(_paths, runOptions);
            // 保存先の名前が重なる（photo.heic と photo.jpg を両方選んだ など）ものがあれば、理由を出して実行しない
            var planErrors = plan.Where(p => p.Status == ConvertStatus.Error).ToList();
            if (planErrors.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n", planErrors.Take(15).Select(p => $"{p.SourceName}: {p.Note}"))
                                      + (planErrors.Count > 15 ? $"\n…ほか {planErrors.Count - 15} 件" : "")
                                      + "\n\n選ぶ画像を変えるか、「実行」で別のフォルダに保存してください。",
                    $"上書き保存できない画像があります（{planErrors.Count} 枚）", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        // 実際にかけた値を覚える（1 枚ずつ自動のときは、隠れているレベルの値は使っていないので既定にしておく）。
        // 出力先の設定は、上書き保存のときも画面で選んでいたものを覚える
        UsedAdjust = adjust;
        UsedOptions = options;
        foreach (var c in _inputs) c.Enabled = false;
        _run.Enabled = _runOver.Enabled = false;
        _close.Text = "中断";
        _progress.Visible = true;
        _progress.Value = 0;
        var cts = _runCts = new CancellationTokenSource();
        var progress = new Progress<ConvertProgress>(p =>
        {
            _progress.Maximum = Math.Max(1, p.Total);
            _progress.Value = Math.Min(p.Done, _progress.Maximum);
            _summary.ForeColor = Theme.Current.Text;
            _summary.Text = p.Done < p.Total ? $"補正中 {p.Done + 1} / {p.Total}: {p.Name}" : "仕上げ中…";
        });
        ConvertResult result;
        try
        {
            result = await Task.Run(() => BatchAdjuster.Run(plan, adjust, runOptions, progress, cts.Token, overwriteSourcesOnly: overwriteSources));
        }
        finally
        {
            _runCts = null;
            cts.Dispose();
        }
        Result = result;

        _progress.Visible = false;
        _close.Text = "閉じる";
        _close.Enabled = true;
        foreach (var c in _inputs) c.Enabled = true;
        _summary.Text = Summarize(result);
        _summary.ForeColor = result.Errors.Count > 0 ? Theme.Current.Danger : Theme.Current.Text;
        if (_closeAfterCancel)
        {
            Close();
            return;
        }
        if (result.Errors.Count > 0)
            MessageBox.Show(this, string.Join("\n", result.Errors.Take(15)) + (result.Errors.Count > 15 ? $"\n…ほか {result.Errors.Count - 15} 件" : ""),
                $"補正できなかった画像（{result.Errors.Count} 枚）", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else if (!result.Canceled)
            Close();
    }

    public static string Summarize(ConvertResult r) =>
        (r.Canceled ? "中断しました: " : "") + $"{r.Converted} 枚を補正しました"
        + (r.Skipped > 0 ? $"・{r.Skipped} 枚は同名のファイルがあるので飛ばしました" : "")
        + (r.Errors.Count > 0 ? $"・{r.Errors.Count} 枚は失敗しました" : "");

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 実行中は中断を頼んで、終わってから閉じる（書きかけのファイルを残さない）
        if (_runCts != null)
        {
            e.Cancel = true;
            _closeAfterCancel = true;
            _runCts.Cancel();
            _close.Enabled = false;
            _summary.Text = "中断しています…";
        }
        base.OnFormClosing(e);
    }

    // ---- 部品 ----

    private sealed class PreviewCanvas : Panel
    {
        public PreviewCanvas() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

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

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Theme.Current.TextMuted, Margin = new Padding(8, 7, 3, 3) };
}
