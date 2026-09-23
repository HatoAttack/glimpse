// 連結ダイアログ（image-sizechange の連結タブから移植）。設定を変えるとすぐプレビューに反映する
// プレビューは縮小して読んだ画像で組む（全部同じ比率で縮小し、px の設定も同じ比率で縮める）。保存するときだけ原寸で読む
using ImageViewer.Core.Editing;
using ImageViewer.Core.Thumbnails;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace ImageViewer.App.Dialogs;

public sealed class CombineDialog : Form
{
    private const int PreviewSourceEdge = 1200;  // プレビュー用に読む画像の長辺の上限
    private const int PreviewMaxEdge = 2400;     // プレビューの絵の長辺の上限

    private readonly List<string> _paths;
    private readonly Dictionary<string, ISImage> _previewImages = new(StringComparer.OrdinalIgnoreCase);
    private double _previewScale = 1;
    private Bitmap? _previewBitmap;
    private int _previewVersion;
    private bool _busy;
    private readonly List<Task> _renders = new(); // 裏で組んでいるプレビュー（閉じるときに終わるのを待ってから画像を解放する）

    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
    private readonly RadioButton _horizontal = new() { Text = "横", AutoSize = true };
    private readonly RadioButton _vertical = new() { Text = "縦", AutoSize = true };
    private readonly RadioButton _grid = new() { Text = "グリッド", AutoSize = true };
    private readonly NumericUpDown _columns = new() { Minimum = 1, Maximum = 50, Width = 60, TextAlign = HorizontalAlignment.Right };
    private readonly ComboBox _normalize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _target = new() { Minimum = 1, Maximum = 20000, Width = 80, TextAlign = HorizontalAlignment.Right };
    private readonly ComboBox _align = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _spacing = new() { Minimum = 0, Maximum = 2000, Width = 80, TextAlign = HorizontalAlignment.Right };
    private readonly NumericUpDown _padding = new() { Minimum = 0, Maximum = 2000, Width = 80, TextAlign = HorizontalAlignment.Right };
    private readonly TextBox _background = new() { Width = 80 };
    private readonly Panel _swatch = new() { Width = 22, Height = 22, BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand, Margin = new Padding(3, 4, 3, 3) };
    private readonly CheckBox _transparent = new() { Text = "背景を透明に（JPG では白）", AutoSize = true };
    private readonly RadioButton _png = new() { Text = "PNG", AutoSize = true };
    private readonly RadioButton _jpeg = new() { Text = "JPG", AutoSize = true };
    private readonly RadioButton _webp = new() { Text = "WEBP", AutoSize = true };
    private readonly Button _save = new() { Text = "連結して保存...", AutoSize = true };
    private readonly PreviewPanel _preview = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 32, 32) };
    private readonly Label _info = new() { AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(4, 6, 4, 6) };
    private readonly System.Windows.Forms.Timer _previewDelay = new() { Interval = 150 };

    /// <summary>使った設定（次回の初期値として保存する用）。保存していなければ null</summary>
    public CombineOptions? UsedOptions { get; private set; }

    /// <summary>保存したファイル。保存していなければ null</summary>
    public string? SavedPath { get; private set; }

    /// <summary>保存した画像の大きさ</summary>
    public (int Width, int Height) SavedSize { get; private set; }

    private sealed class PreviewPanel : Panel
    {
        public PreviewPanel() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <param name="paths">連結する画像（画面の並び順。この順に並べる）</param>
    public CombineDialog(IReadOnlyList<string> paths, CombineOptions initial)
    {
        _paths = paths.ToList();
        Text = $"連結（{paths.Count} 枚）";
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(1200, screen.Width * 9 / 10), Math.Min(820, screen.Height * 9 / 10));
        MinimumSize = new Size(760, 560);

        var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = 320, ColumnCount = 1, Padding = new Padding(8, 8, 4, 8) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(new Label { Text = "上から順に並べます（▲▼ で入れ替え）", AutoSize = true }, 0, 0);
        left.Controls.Add(_list, 0, 1);

        var up = new Button { Text = "▲", Width = 36 };
        var down = new Button { Text = "▼", Width = 36 };
        var remove = new Button { Text = "外す", AutoSize = true };
        up.Click += (_, _) => MoveSelected(-1);
        down.Click += (_, _) => MoveSelected(1);
        remove.Click += (_, _) => RemoveSelected();
        var listButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        listButtons.Controls.AddRange(new Control[] { up, down, remove });
        left.Controls.Add(listButtons, 0, 2);
        left.Controls.Add(BuildOptions(), 0, 3);

        var cancel = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.Cancel };
        CancelButton = cancel;
        _save.Click += async (_, _) => await SaveAsync();
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(3, 8, 3, 3) };
        actions.Controls.AddRange(new Control[] { _save, cancel });
        left.Controls.Add(actions, 0, 4);

        _preview.Paint += Preview_Paint;
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 8, 8, 0) };
        right.Controls.Add(_preview);
        right.Controls.Add(_info);
        Controls.Add(right);
        Controls.Add(left);

        foreach (var p in _paths) _list.Items.Add(Path.GetFileName(p));
        Apply(initial);

        foreach (var rb in new[] { _horizontal, _vertical, _grid })
            rb.CheckedChanged += (_, _) => { if (rb.Checked) { OnLayoutChanged(); SchedulePreview(); } };
        _normalize.SelectedIndexChanged += (_, _) => { _target.Enabled = SelectedNormalize() == CombineNormalize.Fixed; SchedulePreview(); };
        foreach (var n in new[] { _columns, _target, _spacing, _padding }) n.ValueChanged += (_, _) => SchedulePreview();
        _align.SelectedIndexChanged += (_, _) => SchedulePreview();
        _background.TextChanged += (_, _) => { SyncSwatch(); SchedulePreview(); };
        _transparent.CheckedChanged += (_, _) => SchedulePreview();
        _swatch.Click += (_, _) => PickColor();
        foreach (var rb in new[] { _png, _jpeg, _webp }) rb.CheckedChanged += (_, _) => { if (rb.Checked) SchedulePreview(); };
        _previewDelay.Tick += async (_, _) =>
        {
            _previewDelay.Stop();
            await RenderPreviewAsync();
        };

        Shown += async (_, _) => await LoadPreviewImagesAsync();
        FormClosed += (_, _) =>
        {
            _previewBitmap?.Dispose();
            // 裏でプレビューを組んでいる最中なら、終わってから解放する
            var images = _previewImages.Values.ToList();
            _previewImages.Clear();
            void Release() { foreach (var im in images) im.Dispose(); }
            var pending = _renders.Where(t => !t.IsCompleted).ToArray();
            if (pending.Length > 0) Task.WhenAll(pending).ContinueWith(_ => Release(), TaskScheduler.Default);
            else Release();
        };
    }

    private Control BuildOptions()
    {
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        Label L(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(3, 7, 3, 3) };
        FlowLayoutPanel Row(params Control[] items)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            row.Controls.AddRange(items);
            return row;
        }
        int r = 0;
        void Add(string label, Control c)
        {
            grid.Controls.Add(L(label), 0, r);
            grid.Controls.Add(c, 1, r++);
        }
        Add("並べ方:", Row(_horizontal, _vertical, _grid));
        Add("列の数:", _columns);
        Add("大きさ:", _normalize);
        Add("指定 px:", _target);
        Add("揃え:", _align);
        Add("間隔 px:", _spacing);
        Add("外の余白 px:", _padding);
        Add("背景色:", Row(_background, _swatch));
        grid.Controls.Add(_transparent, 1, r++);
        Add("形式:", Row(_png, _jpeg, _webp));
        var box = new GroupBox { Text = "設定", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(6) };
        box.Controls.Add(grid);
        return box;
    }

    // ---- 設定 ↔ 画面 ----

    private void Apply(CombineOptions o)
    {
        (o.Layout switch { CombineLayout.Vertical => _vertical, CombineLayout.Grid => _grid, _ => _horizontal }).Checked = true;
        _columns.Value = Math.Clamp(o.Columns, 1, 50);
        _target.Value = Math.Clamp(o.TargetPx, 1, 20000);
        _spacing.Value = Math.Clamp(o.Spacing, 0, 2000);
        _padding.Value = Math.Clamp(o.Padding, 0, 2000);
        _background.Text = o.Background;
        _transparent.Checked = o.Transparent;
        (o.Format switch { OutputFormat.Jpeg => _jpeg, OutputFormat.Webp => _webp, _ => _png }).Checked = true;
        OnLayoutChanged();
        var norms = NormalizeChoices();
        int ni = Array.FindIndex(norms, n => n.Value == o.Normalize);
        _normalize.SelectedIndex = ni >= 0 ? ni : 0;
        if (_align.Enabled) _align.SelectedIndex = (int)o.Align;
        _target.Enabled = SelectedNormalize() == CombineNormalize.Fixed;
        SyncSwatch();
    }

    private CombineOptions CurrentOptions() => new()
    {
        Layout = _grid.Checked ? CombineLayout.Grid : _vertical.Checked ? CombineLayout.Vertical : CombineLayout.Horizontal,
        Columns = (int)_columns.Value,
        Normalize = SelectedNormalize(),
        TargetPx = (int)_target.Value,
        Align = _align.SelectedIndex is >= 0 and <= 2 ? (CombineAlign)_align.SelectedIndex : CombineAlign.Start,
        Spacing = (int)_spacing.Value,
        Padding = (int)_padding.Value,
        Background = _background.Text.Trim(),
        Transparent = _transparent.Checked,
        Format = _jpeg.Checked ? OutputFormat.Jpeg : _webp.Checked ? OutputFormat.Webp : OutputFormat.Png,
    };

    private (string Label, CombineNormalize Value)[] NormalizeChoices() => _grid.Checked
        ? new[] { ("元の一番大きい画像に合わせる", CombineNormalize.None), ("セルを指定 px 角に", CombineNormalize.Fixed) }
        : new[]
        {
            ("そのまま", CombineNormalize.None), ("小さい方に揃える", CombineNormalize.Min),
            ("大きい方に揃える", CombineNormalize.Max), ("指定 px に揃える", CombineNormalize.Fixed),
        };

    private CombineNormalize SelectedNormalize()
    {
        var choices = NormalizeChoices();
        return _normalize.SelectedIndex >= 0 && _normalize.SelectedIndex < choices.Length ? choices[_normalize.SelectedIndex].Value : CombineNormalize.None;
    }

    /// <summary>並べ方に合わせて「大きさ」「揃え」の選択肢を張り替える（横は高さ、縦は幅を揃える）</summary>
    private void OnLayoutChanged()
    {
        _columns.Enabled = _grid.Checked;
        _normalize.Items.Clear();
        _normalize.Items.AddRange(NormalizeChoices().Select(c => (object)c.Label).ToArray());
        _normalize.SelectedIndex = 0;
        _align.Items.Clear();
        _align.Enabled = !_grid.Checked;
        if (!_grid.Checked)
        {
            _align.Items.AddRange(_horizontal.Checked ? new object[] { "上", "中央", "下" } : new object[] { "左", "中央", "右" });
            _align.SelectedIndex = 0;
        }
    }

    private void PickColor()
    {
        using var dlg = new ColorDialog { Color = _swatch.BackColor, FullOpen = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) _background.Text = $"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}";
    }

    private void SyncSwatch()
    {
        try
        {
            var c = Combiner.ParseColor(_background.Text, false);
            _swatch.BackColor = Color.FromArgb(c.R, c.G, c.B);
        }
        catch (ArgumentException)
        {
            // 入力の途中
        }
    }

    // ---- 並びの入れ替え ----

    private void MoveSelected(int delta)
    {
        if (_list.SelectedIndices.Count != 1) return;
        int i = _list.SelectedIndex, j = i + delta;
        if (j < 0 || j >= _paths.Count) return;
        (_paths[i], _paths[j]) = (_paths[j], _paths[i]);
        object item = _list.Items[i];
        _list.Items.RemoveAt(i);
        _list.Items.Insert(j, item);
        _list.SelectedIndex = j;
        SchedulePreview();
    }

    private void RemoveSelected()
    {
        var indices = _list.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
        if (_paths.Count - indices.Count < 1) return; // 全部は外せない
        foreach (int i in indices)
        {
            _list.Items.RemoveAt(i);
            _paths.RemoveAt(i);
        }
        Text = $"連結（{_paths.Count} 枚）";
        SchedulePreview();
    }

    // ---- プレビュー ----

    private async Task LoadPreviewImagesAsync()
    {
        _info.Text = "プレビュー用に読み込み中…";
        UseWaitCursor = true;
        try
        {
            var paths = _paths.ToList();
            var (images, scale) = await Task.Run(() => Combiner.LoadForPreview(paths, PreviewSourceEdge));
            if (IsDisposed)
            {
                foreach (var im in images) im.Dispose();
                return;
            }
            for (int i = 0; i < paths.Count; i++) _previewImages[paths[i]] = images[i];
            _previewScale = scale;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _info.Text = $"読み込めない画像があります: {ex.Message}";
            return;
        }
        finally
        {
            UseWaitCursor = false;
        }
        await RenderPreviewAsync();
    }

    private void SchedulePreview()
    {
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    private async Task RenderPreviewAsync()
    {
        if (_previewImages.Count == 0) return;
        var images = _paths.Where(_previewImages.ContainsKey).Select(p => _previewImages[p]).ToList();
        var options = CurrentOptions();
        double scale = _previewScale;
        int version = ++_previewVersion;
        Bitmap bitmap;
        int fullW, fullH;
        try
        {
            var task = Task.Run(() =>
            {
                using var result = Combiner.Combine(images, options.Scaled(scale));
                // 原寸での大きさの目安（縮小で丸めた分ずれることがある）
                int w = (int)Math.Round(result.Width / scale), h = (int)Math.Round(result.Height / scale);
                if (Math.Max(result.Width, result.Height) > PreviewMaxEdge)
                    result.Mutate(x => x.Resize(new ResizeOptions { Size = new SixLabors.ImageSharp.Size(PreviewMaxEdge, PreviewMaxEdge), Mode = ResizeMode.Max }));
                if (options.Format == OutputFormat.Jpeg) result.Mutate(x => x.BackgroundColor(SixLabors.ImageSharp.Color.White));
                return (ThumbnailGenerator.ToPArgbBitmap(result), w, h);
            });
            _renders.RemoveAll(t => t.IsCompleted);
            _renders.Add(task);
            (bitmap, fullW, fullH) = await task;
        }
        catch (ArgumentException ex)
        {
            if (version == _previewVersion) _info.Text = ex.Message;
            return;
        }
        if (version != _previewVersion || IsDisposed)
        {
            bitmap.Dispose();
            return;
        }
        _previewBitmap?.Dispose();
        _previewBitmap = bitmap;
        _preview.Invalidate();
        _info.Text = scale < 1
            ? $"{images.Count} 枚 ・ 仕上がりの大きさ: 約 {fullW} × {fullH} px"
            : $"{images.Count} 枚 ・ 仕上がりの大きさ: {fullW} × {fullH} px";
    }

    private void Preview_Paint(object? sender, PaintEventArgs e)
    {
        if (_previewBitmap == null) return;
        int cw = _preview.ClientSize.Width, ch = _preview.ClientSize.Height;
        double s = Math.Min(Math.Min((double)cw / _previewBitmap.Width, (double)ch / _previewBitmap.Height), 1.0);
        int w = Math.Max(1, (int)(_previewBitmap.Width * s)), h = Math.Max(1, (int)(_previewBitmap.Height * s));
        int x = (cw - w) / 2, y = (ch - h) / 2;
        // 透明な部分がわかるように市松模様を敷く
        using (var checker = new System.Drawing.Drawing2D.HatchBrush(System.Drawing.Drawing2D.HatchStyle.LargeCheckerBoard, Color.FromArgb(70, 70, 70), Color.FromArgb(50, 50, 50)))
            e.Graphics.FillRectangle(checker, x, y, w, h);
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        e.Graphics.DrawImage(_previewBitmap, x, y, w, h);
    }

    // ---- 保存 ----

    private async Task SaveAsync()
    {
        if (_busy) return;
        var options = CurrentOptions();
        try
        {
            Combiner.ParseColor(options.Background, options.Transparent);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string ext = ImageSaver.ExtensionFor(options.Format == OutputFormat.Keep ? OutputFormat.Png : options.Format, ".png");
        string folder = Path.GetDirectoryName(_paths[0])!;
        using var dlg = new SaveFileDialog
        {
            Title = "連結した画像の保存先",
            InitialDirectory = folder,
            FileName = Path.GetFileName(ImageSaver.UniquePath(Path.Combine(folder, "combined" + ext))),
            Filter = $"{ext.TrimStart('.').ToUpperInvariant()}|*{ext}",
            DefaultExt = ext.TrimStart('.'),
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string dst = dlg.FileName;
        var paths = _paths.ToList();

        _busy = true;
        _save.Enabled = false;
        UseWaitCursor = true;
        _info.Text = "原寸で連結して保存しています…";
        try
        {
            SavedSize = await Task.Run(() => Combiner.CombineFiles(paths, options, dst));
            UsedOptions = options;
            SavedPath = dst;
            _busy = false;
            DialogResult = DialogResult.OK; // 保存できたら閉じる
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _info.Text = "保存できませんでした";
            MessageBox.Show(this, ex.Message, "保存できませんでした", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            _save.Enabled = true;
            UseWaitCursor = false;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy) e.Cancel = true; // 保存中は閉じない
        base.OnFormClosing(e);
    }
}
