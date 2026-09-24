// 画像ビューア - メイン画面
using ImageViewer.App.Commands;
using ImageViewer.App.Dialogs;
using ImageViewer.App.Filer;
using ImageViewer.App.Grid;
using ImageViewer.App.Jump;
using ImageViewer.App.Viewer;
using ImageViewer.Core.Commands;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Jump;
using ImageViewer.Core.Navigation;
using ImageViewer.Core.Ordering;
using ImageViewer.Core.Rename;
using ImageViewer.Core.Settings;
using ImageViewer.Core.Thumbnails;

namespace ImageViewer.App;

public class MainForm : Form, ICommandHost, ISettingsAccess
{
    public const string AppTitle = "画像ビューア";

    private readonly CommandRegistry _registry = new();
    private readonly ThumbnailService _thumbnails;
    private readonly ThumbnailGrid _grid;
    private readonly QuickLookView _quickLook = new() { Dock = DockStyle.Fill };
    private readonly ToolStripStatusLabel _status;
    private readonly ToolStripStatusLabel _selectionInfo = new() { TextAlign = ContentAlignment.MiddleRight };
    private readonly System.Windows.Forms.Timer _infoDelay = new() { Interval = 100 };
    private readonly Dictionary<ThumbnailKey, ImageHeader?> _infoCache = new();
    private CancellationTokenSource? _infoCts;
    private readonly ContextMenuStrip _contextMenu = new();
    // メニュー項目とコマンドの対応（選択が変わるたびに有効/無効を更新する）
    private readonly List<(ToolStripMenuItem Item, IImageCommand Command)> _commandItems = new();

    private readonly FolderOrderStore _orderStore = FolderOrderStore.CreateDefault();
    private readonly List<(ToolStripMenuItem Item, SortMode Mode)> _sortItems = new();
    private SortMode _sortMode = SortMode.Name;

    // 直前の名前の変更（元に戻す用）。変更前の手動の並び順と並び順の種類も一緒に覚えておく
    private (IReadOnlyList<RenameOp> Ops, IReadOnlyList<string>? SavedOrder, SortMode Mode)? _lastRename;
    private ToolStripMenuItem _undoItem = null!;
    private readonly List<ToolStripMenuItem> _renameFolderItems = new();

    private string? _folder;
    private CancellationTokenSource? _loadCts;

    // ---- ファイラ（移動） ----
    private readonly NavigationHistory _history = new();
    private readonly FolderTree _tree = new() { Dock = DockStyle.Fill };
    private readonly TextBox _address = new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        // Windows 標準のフォルダ名補完（追加の処理・索引は不要）
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.FileSystemDirectories,
    };
    private readonly Button _backButton = new() { Text = "←" };
    private readonly Button _forwardButton = new() { Text = "→" };
    private readonly Button _upButton = new() { Text = "↑" };
    private readonly Button _homeButton = new() { Text = "⌂" };
    private readonly ToolTip _toolTip = new();
    private ToolStripMenuItem _backItem = null!, _forwardItem = null!, _upItem = null!;

    // ---- フォルダジャンプ ----
    private readonly FolderJumpService _jump = new(FolderJumpService.DefaultDataDir);
    private readonly JumpList _jumpList = new();
    private readonly System.Windows.Forms.Timer _jumpDelay = new() { Interval = 120 };
    private EverythingClient? _everything;
    private CancellationTokenSource? _jumpCts;
    private int _visitsSinceSave;

    private readonly SettingsStore _settingsStore = SettingsStore.CreateDefault();
    private AppSettings _settings;

    /// <summary>ホームフォルダ（設定したフォルダが無ければピクチャ）</summary>
    private string HomeFolder =>
        _settings.HomeFolder is string h && Directory.Exists(h) ? h : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    /// <summary>移動の種類（履歴の扱いが変わる）</summary>
    private enum NavKind { New, Back, Forward, Reload }

    public MainForm(string? initialFolder = null)
    {
        Text = AppTitle;
        Font = new Font("Yu Gothic UI", 9F);
        ClientSize = new Size(980, 700);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        _settings = _settingsStore.Load();
        RegisterCommands();

        // サムネイルはメモリ上に最大 128MB まで持つ（大きさは表示サイズに合わせて段階的に決まる）
        _thumbnails = new ThumbnailService(LogicalToDeviceUnits(160), 128L * 1024 * 1024,
            SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
        _grid = new ThumbnailGrid(_thumbnails, _settings.ThumbnailSize ?? 160) { Dock = DockStyle.Fill, ContextMenuStrip = _contextMenu };
        _grid.SelectionChanged += (_, _) => UpdateCommandStates();
        _grid.MarksChanged += (_, _) => UpdateCommandStates();
        _grid.OrderChanged += (_, _) => SaveManualOrder();
        _grid.FolderDropped += async (_, folder) => await LoadFolderAsync(folder);
        _grid.FolderActivated += async (_, dir) => await LoadFolderAsync(dir.FullName);
        _grid.MouseDown += OnMouseBackForward;
        _tree.MouseDown += OnMouseBackForward;
        _tree.FolderSelected += async (_, path) => await LoadFolderAsync(path);
        // フォルダのタイル・ツリーのフォルダへのドロップで移動 / コピー（エクスプローラーからのドロップも）
        _grid.FilesDroppedOnFolder += async (_, drop) => await TransferDroppedAsync(drop);
        _tree.FilesDroppedOnFolder += async (_, drop) => await TransferDroppedAsync(drop);
        // グリッドからエクスプローラー等へドラッグして移動された画像は一覧から外す
        _grid.FilesMovedOut += (_, paths) => FilesRemoved(paths);
        FormClosed += (_, _) =>
        {
            _thumbnails.Dispose();
            _jump.SaveVisits();
            _everything?.Dispose();
        };

        var statusStrip = new StatusStrip();
        _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_selectionInfo);
        AddThumbnailSizeSlider(statusStrip);
        _grid.SelectionChanged += (_, _) =>
        {
            // 選択を素早く切り替えている間は読まない（止まってから読む）
            _infoDelay.Stop();
            _infoDelay.Start();
        };
        _infoDelay.Tick += async (_, _) =>
        {
            _infoDelay.Stop();
            await UpdateSelectionInfoAsync();
        };

        var split = new NoFocusSplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = LogicalToDeviceUnits(4),
        };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_grid);
        split.Panel2.Controls.Add(_quickLook);
        SetUpQuickLook();

        // Dock は後から追加したものから順に場所を取るので、Fill → アドレスバー → メニュー → ステータスバー の順に追加
        Controls.Add(split);
        Controls.Add(BuildNavigationBar());
        Controls.Add(BuildMainMenu());
        Controls.Add(statusStrip);
        split.SplitterDistance = LogicalToDeviceUnits(220);
        BuildContextMenu();

        DragEnter += (_, e) =>
            e.Effect = DroppedFolder(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += async (_, e) =>
        {
            if (DroppedFolder(e) is string f) await LoadFolderAsync(f);
        };

        UpdateCommandStates();
        UpdateNavigationState();
        Activated += (_, _) => UpdateCommandEnabled(); // エクスプローラーでコピーしてから戻ってきたら貼り付けられるように
        _tree.SetHome(HomeFolder);
        SetUpJump();
        // 起動時は指定のフォルダ、無ければホーム（未設定・見つからなければピクチャ）を開く
        string start = initialFolder ?? HomeFolder;
        if (_settings.HomeFolder != null && !Directory.Exists(_settings.HomeFolder) && initialFolder == null)
            Shown += (_, _) => Notify($"ホームフォルダが見つからないのでピクチャを開きました: {_settings.HomeFolder}");
        if (Directory.Exists(start)) Shown += async (_, _) => await LoadFolderAsync(start);
    }

    // ---- Quick Look（Space / ダブルクリックで大きく表示） ----

    private void SetUpQuickLook()
    {
        var markKey = Shortcuts.Parse(_settings.MarkKey ?? "¥");
        var markNextKey = Shortcuts.Parse(_settings.MarkNextKey ?? "^");
        _grid.MarkKey = _quickLook.MarkKey = markKey != Keys.None ? markKey : Keys.Oem5;
        _grid.MarkNextKey = _quickLook.MarkNextKey = markNextKey != Keys.None ? markNextKey : Keys.Oem7;

        _quickLook.IsMarked = _grid.IsImageMarked;
        _quickLook.MarkedCount = () => _grid.MarkedCount;
        _quickLook.PlaceholderProvider = f =>
            _thumbnails.TryGet(ThumbnailKey.From(f), out var bmp) == ThumbnailState.Ready ? bmp : null;

        _grid.PeekRequested += (_, index) => _quickLook.Open(_grid.Items, index, byKey: true);
        _grid.ItemActivated += (_, index) => _quickLook.Open(_grid.Items, index, byKey: false);
        _quickLook.CurrentChanged += (_, index) => _grid.SelectImage(index);
        _quickLook.ToggleMarkRequested += (_, index) => _grid.ToggleImageMark(index);
        _quickLook.Closed += (_, _) => _grid.Focus();
        _grid.MarksChanged += (_, _) => _quickLook.Invalidate();
        // 別のフォルダへ移った・表示中の画像が消えたら閉じる。並べ替え・リネームなら同じ画像を表示し続ける
        _grid.ContentsChanged += (_, _) => _quickLook.ItemsChanged(_grid.Items);
    }

    // ---- サムネイルの大きさ（フッターのスライダー / Ctrl+ホイール） ----

    private void AddThumbnailSizeSlider(StatusStrip strip)
    {
        var slider = new TrackBar
        {
            Minimum = ThumbnailGrid.MinThumbnailSize,
            Maximum = ThumbnailGrid.MaxThumbnailSize,
            SmallChange = ThumbnailGrid.ThumbnailSizeStep,
            LargeChange = ThumbnailGrid.ThumbnailSizeStep * 2,
            TickStyle = TickStyle.None,
            AutoSize = false,
            Width = LogicalToDeviceUnits(140),
            Height = LogicalToDeviceUnits(22),
            Value = _grid.ThumbnailSize,
            BackColor = SystemColors.Control,
        };
        var host = new ToolStripControlHost(slider) { AutoSize = false, Width = slider.Width, Margin = new Padding(0, 1, 4, 0) };
        var label = new ToolStripStatusLabel("サイズ") { ToolTipText = "サムネイルの大きさ（Ctrl+ホイールでも変えられます）" };
        _toolTip.SetToolTip(slider, "サムネイルの大きさ（Ctrl+ホイールでも変えられます）");
        strip.Items.Add(label);
        strip.Items.Add(host);

        slider.ValueChanged += (_, _) => _grid.ThumbnailSize = slider.Value;
        _grid.ThumbnailSizeChanged += (_, size) => slider.Value = Math.Clamp(size, slider.Minimum, slider.Maximum);
        // 設定には終了時に 1 回だけ書く（動かすたびに書かない）
        FormClosing += (_, _) =>
        {
            if ((_settings.ThumbnailSize ?? 160) == _grid.ThumbnailSize) return; // 変えていなければ書かない
            _settings = _settings with { ThumbnailSize = _grid.ThumbnailSize };
            try { _settingsStore.Save(_settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }

    // ---- 選択中の画像の情報（フッター） ----

    private async Task UpdateSelectionInfoAsync()
    {
        _infoCts?.Cancel();
        var images = _grid.SelectedImages;
        var folders = _grid.SelectedFolders;

        if (images.Count == 0 && folders.Count == 1)
        {
            _selectionInfo.Text = $"フォルダー ・ {folders[0].LastWriteTime:yyyy/MM/dd HH:mm}";
            return;
        }
        if (images.Count != 1)
        {
            _selectionInfo.Text = images.Count == 0 ? "" : $"{images.Count} 枚 ・ 合計 {FormatBytes(images.Sum(SafeLength))}";
            return;
        }

        var file = images[0];
        string rest = $"{FormatBytes(SafeLength(file))} ・ {file.LastWriteTime:yyyy/MM/dd HH:mm}";
        var key = ThumbnailKey.From(file);
        if (!_infoCache.TryGetValue(key, out var info))
        {
            _selectionInfo.Text = rest; // 大きさを読んでいる間も、サイズと日時は先に出す
            var cts = _infoCts = new CancellationTokenSource();
            try
            {
                info = await Task.Run(() => ImageLoader.Identify(file.FullName), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (cts.IsCancellationRequested) return;
            if (_infoCache.Count >= 2000) _infoCache.Clear(); // 覚えておく数に上限
            _infoCache[key] = info;
        }
        _selectionInfo.Text = info != null ? $"{info.Width} × {info.Height} ・ {info.Format} ・ {rest}" : rest;
    }

    private static long SafeLength(FileInfo f)
    {
        try { return f.Length; }
        catch (IOException) { return 0; }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    // ---- 新しいフォルダー ----

    private async Task CreateFolderAsync()
    {
        if (_folder == null) return;
        string parent = _folder;
        string? Validate(string name) =>
            RenamePlanner.ValidateName(name)
            ?? (Directory.Exists(Path.Combine(parent, name)) || File.Exists(Path.Combine(parent, name))
                ? "同じ名前のフォルダーまたはファイルがすでにあります" : null);

        using var dlg = new TextInputDialog("新しいフォルダー", $"{parent} に作るフォルダーの名前:", UniqueFolderName(parent), Validate);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string path = Path.Combine(parent, dlg.Value);
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "フォルダーを作れませんでした", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        await LoadFolderAsync(parent, NavKind.Reload);
        _grid.SelectPath(path);
        Notify($"フォルダーを作りました: {dlg.Value}");
    }

    // ---- フォルダー名の変更 ----

    /// <summary>
    /// ショートカット（F2）は画像の名前の変更コマンドと共有なので ProcessCmdKey で振り分け、メニューには表示だけする
    /// </summary>
    private ToolStripMenuItem RenameFolderItem(string text)
    {
        var item = new ToolStripMenuItem(text, null, async (_, _) => await RenameFolderAsync()) { ShortcutKeyDisplayString = "F2", Enabled = false };
        _renameFolderItems.Add(item);
        return item;
    }

    /// <summary>フォルダのタイルだけを 1 つ選んでいるとき、そのフォルダ</summary>
    private DirectoryInfo? SelectedSingleFolder() =>
        _grid.SelectedImages.Count == 0 && _grid.SelectedFolders is [var folder] ? folder : null;

    private async Task RenameFolderAsync()
    {
        if (SelectedSingleFolder() is not { Parent: { } parentDir } dir || _folder == null) return;
        string parent = parentDir.FullName, oldName = dir.Name, oldPath = dir.FullName;
        string? Validate(string name) =>
            RenamePlanner.ValidateName(name)
            ?? (!string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase)
                && (Directory.Exists(Path.Combine(parent, name)) || File.Exists(Path.Combine(parent, name)))
                ? "同じ名前のフォルダーまたはファイルがすでにあります" : null);

        using var dlg = new TextInputDialog("フォルダー名の変更", $"「{oldName}」の新しい名前:", oldName, Validate);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Value == oldName) return;

        string newPath = Path.Combine(parent, dlg.Value);
        try
        {
            Directory.Move(oldPath, newPath); // 大文字小文字だけの変更もこれでできる
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message + "\n\nフォルダーの中のファイルを開いているアプリがあると変更できません。",
                "フォルダー名を変更できませんでした", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _tree.FolderRenamed(oldPath, newPath);
        // ホームがそのフォルダ（の中）なら付け替える
        if (_settings.HomeFolder is string home && RenamedPath(home, oldPath, newPath) is string newHome) SetHome(newHome);
        await LoadFolderAsync(_folder, NavKind.Reload);
        _grid.SelectPath(newPath);
        Notify($"フォルダー名を変更しました: {oldName} → {dlg.Value}");
    }

    /// <summary>path が oldPath 自身かその中なら、newPath に付け替えたパス（違えば null）</summary>
    private static string? RenamedPath(string path, string oldPath, string newPath)
    {
        if (string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase)) return newPath;
        string prefix = oldPath + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? newPath + path[oldPath.Length..] : null;
    }

    /// <summary>「新しいフォルダー」、あれば「新しいフォルダー (2)」…（エクスプローラーと同じ付け方）</summary>
    private static string UniqueFolderName(string parent)
    {
        const string baseName = "新しいフォルダー";
        string name = baseName;
        for (int i = 2; Directory.Exists(Path.Combine(parent, name)) || File.Exists(Path.Combine(parent, name)); i++)
            name = $"{baseName} ({i})";
        return name;
    }

    // ---- アドレスバー・戻る / 進む / 上へ ----

    private Control BuildNavigationBar()
    {
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, AutoSize = true, WrapContents = false, Padding = new Padding(4, 3, 0, 0),
        };
        foreach (var (button, tip) in new[]
                 {
                     (_backButton, "戻る (Alt+←)"), (_forwardButton, "進む (Alt+→)"), (_upButton, "上のフォルダへ (Alt+↑ / Backspace)"),
                     (_homeButton, "ホームへ (Alt+Home)"),
                 })
        {
            button.Size = new Size(LogicalToDeviceUnits(30), _address.PreferredHeight + LogicalToDeviceUnits(2));
            button.Margin = new Padding(0, 0, LogicalToDeviceUnits(2), 0);
            button.TabStop = false;
            _toolTip.SetToolTip(button, tip);
            buttons.Controls.Add(button);
        }
        _backButton.Click += async (_, _) => await GoBackAsync();
        _forwardButton.Click += async (_, _) => await GoForwardAsync();
        _upButton.Click += async (_, _) => await GoUpAsync();
        _homeButton.Click += async (_, _) => await LoadFolderAsync(HomeFolder);

        _address.KeyDown += async (_, e) =>
        {
            if (_jumpList.Visible && e.KeyCode is Keys.Down or Keys.Up or Keys.PageDown or Keys.PageUp)
            {
                e.SuppressKeyPress = true;
                _jumpList.MoveSelection(e.KeyCode switch
                {
                    Keys.Down => 1, Keys.Up => -1, Keys.PageDown => _jumpList.MaxVisibleItems, _ => -_jumpList.MaxVisibleItems,
                });
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (_jumpList.Visible && _jumpList.SelectedPath is string picked) await JumpToAsync(picked);
                else await NavigateFromAddressAsync();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                if (_jumpList.Visible)
                {
                    HideJumpList();
                    return;
                }
                _address.Text = _folder ?? "";
                _grid.Focus();
            }
        };
        // クリックで全体を選択（すぐに上書き入力できる。エクスプローラーと同じ）
        _address.Enter += (_, _) => BeginInvoke(_address.SelectAll);

        var addressHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 4, 6, 4) };
        addressHost.Controls.Add(_address);
        var bar = new Panel { Dock = DockStyle.Top, Height = _address.PreferredHeight + LogicalToDeviceUnits(10) };
        bar.Controls.Add(addressHost);
        bar.Controls.Add(buttons);
        return bar;
    }

    // ---- フォルダジャンプ ----

    private void SetUpJump()
    {
        _jumpList.Font = Font;
        _jumpList.ItemHeight = Font.Height * 2 + LogicalToDeviceUnits(6);
        Controls.Add(_jumpList);
        _jumpList.BringToFront();
        _jumpList.Picked += async (_, path) => await JumpToAsync(path);

        // 入力が止まってから検索する（1 文字ごとに走らせない）
        _address.TextChanged += (_, _) =>
        {
            if (!_address.Focused) return;
            _jumpDelay.Stop();
            _jumpDelay.Start();
        };
        _jumpDelay.Tick += async (_, _) =>
        {
            _jumpDelay.Stop();
            await UpdateJumpListAsync();
        };
        // 候補をクリックしたときはアドレスバーからフォーカスが移るので、それ以外で外れたときだけ閉じる
        _address.LostFocus += (_, _) => BeginInvoke(() =>
        {
            if (!_jumpList.Focused && !_address.Focused) HideJumpList();
        });

        Shown += (_, _) => _ = _jump.StartAsync(_settings.EffectiveJumpRoots);
    }

    private async Task UpdateJumpListAsync()
    {
        string query = _address.Text.Trim();
        if (!_address.Focused || query.Length == 0 || FolderListing.LooksLikePath(query) || string.Equals(query, _folder, StringComparison.OrdinalIgnoreCase))
        {
            HideJumpList();
            return;
        }

        _jumpCts?.Cancel();
        var cts = _jumpCts = new CancellationTokenSource();
        IReadOnlyList<JumpResult> results;
        string? message;
        try
        {
            (results, message) = await SearchFoldersAsync(query, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // 次の入力で検索し直している
        }
        if (cts.IsCancellationRequested || !_address.Focused) return;

        _jumpList.SetResults(results, message);
        var below = PointToClient(_address.Parent!.PointToScreen(new Point(_address.Left, _address.Bottom)));
        _jumpList.SetBounds(below.X, below.Y + 1, _address.Width, _jumpList.Height);
        _jumpList.Visible = true;
        _jumpList.BringToFront();
    }

    /// <summary>フォルダ名で検索（アドレスバーのフォルダジャンプと「フォルダーへ移動 / コピー」で共通）</summary>
    private async Task<(IReadOnlyList<JumpResult> Results, string? Message)> SearchFoldersAsync(string query, CancellationToken ct)
    {
        IReadOnlyList<string>? extra = null;
        if (_settings.UseEverything && EverythingClient.IsRunning)
        {
            _everything ??= new EverythingClient();
            extra = await _everything.QueryFoldersAsync(query, 300, TimeSpan.FromSeconds(1)); // 応答が無ければ null → 自前の索引
        }
        var results = await Task.Run(() => _jump.Search(query, 30, extra, ct), ct);
        string? message = results.Count > 0 ? null
            : _jump.Index == null && _jump.IsBuilding ? "フォルダの索引を作成中です…（少し待ってから入力し直してください）"
            : "見つかりません";
        return (results, message);
    }

    private void HideJumpList()
    {
        _jumpCts?.Cancel();
        _jumpList.Visible = false;
    }

    private async Task JumpToAsync(string path)
    {
        HideJumpList();
        if (!Directory.Exists(path))
        {
            System.Media.SystemSounds.Beep.Play();
            Notify($"フォルダが見つかりません（移動・削除された可能性があります）: {path}");
            return;
        }
        _grid.Focus(); // 先にフォーカスを移しておくと、読み込み後にアドレスバーが移動先のパスに更新される
        await LoadFolderAsync(path);
    }

    /// <summary>Ctrl+J: 空のアドレスバーから入力を始める</summary>
    private void StartJump()
    {
        _address.Focus();
        _address.Text = "";
    }

    private void ShowJumpSettings()
    {
        using var dlg = new JumpSettingsDialog(_settings.EffectiveJumpRoots, _settings.UseEverything, _jump);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        bool rootsChanged = !dlg.Roots.SequenceEqual(_settings.EffectiveJumpRoots, StringComparer.OrdinalIgnoreCase);
        _settings = _settings with { JumpRoots = dlg.Roots.ToList(), UseEverything = dlg.UseEverything };
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"設定を保存できませんでした（この起動中だけ有効）: {ex.Message}");
        }
        if (rootsChanged) _jump.Rebuild(_settings.EffectiveJumpRoots);
    }

    private async Task NavigateFromAddressAsync()
    {
        if (FolderListing.Normalize(_address.Text) is string folder)
        {
            _grid.Focus(); // 先にフォーカスを移しておくと、読み込み後にアドレスバーが正規化したパスに更新される
            await LoadFolderAsync(folder);
        }
        else
        {
            System.Media.SystemSounds.Beep.Play();
            Notify($"フォルダが見つかりません: {_address.Text.Trim()}");
            _address.SelectAll();
        }
    }

    private void SetHome(string folder)
    {
        _settings = _settings with { HomeFolder = folder };
        try
        {
            _settingsStore.Save(_settings);
            Notify($"ホームフォルダを設定しました: {folder}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"設定を保存できませんでした（この起動中だけ有効）: {ex.Message}");
        }
        _tree.SetHome(HomeFolder);
        if (_folder != null) _ = _tree.RevealAsync(_folder);
    }

    private void ChooseHome()
    {
        using var dlg = new FolderBrowserDialog { InitialDirectory = HomeFolder, Description = "ホームフォルダを選んでください" };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetHome(dlg.SelectedPath);
    }

    private async Task GoBackAsync()
    {
        if (_history.GoBack() is string path) await LoadFolderAsync(path, NavKind.Back);
    }

    private async Task GoForwardAsync()
    {
        if (_history.GoForward() is string path) await LoadFolderAsync(path, NavKind.Forward);
    }

    private async Task GoUpAsync()
    {
        if (_folder != null && FolderListing.Parent(_folder) is string parent) await LoadFolderAsync(parent);
    }

    private void UpdateNavigationState()
    {
        _backButton.Enabled = _backItem.Enabled = _history.CanGoBack;
        _forwardButton.Enabled = _forwardItem.Enabled = _history.CanGoForward;
        _upButton.Enabled = _upItem.Enabled = _folder != null && FolderListing.Parent(_folder) != null;
    }

    /// <summary>マウスの戻る / 進むボタン</summary>
    private async void OnMouseBackForward(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.XButton1) await GoBackAsync();
        else if (e.Button == MouseButtons.XButton2) await GoForwardAsync();
    }

    /// <summary>Backspace で上のフォルダへ（アドレスバーで文字を消しているときは除く）</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Back && !_address.Focused)
        {
            _ = GoUpAsync();
            return true;
        }
        // F2: フォルダのタイルを 1 つだけ選んでいればフォルダー名の変更（画像を選んでいれば画像の名前の変更コマンド）
        if (keyData == Keys.F2 && !_address.Focused && SelectedSingleFolder() != null)
        {
            _ = RenameFolderAsync();
            return true;
        }
        // アドレスバーでは文字の編集を優先（画像のコピー・削除などにしない）
        if (_address.Focused && keyData is Keys.Delete or (Keys.Control | Keys.C) or (Keys.Control | Keys.X)
                or (Keys.Control | Keys.V) or (Keys.Control | Keys.A) or (Keys.Control | Keys.Z))
            return false;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RegisterCommands()
    {
        _registry.Register(new CutFilesCommand());
        _registry.Register(new CopyFilesCommand());
        _registry.Register(new PasteFilesCommand(this));
        _registry.Register(new ResizeCommand(this, this));
        _registry.Register(new CropCommand(this));
        _registry.Register(new CombineCommand(this, this));
        _registry.Register(new RenameCommand(this));
        _registry.Register(new MoveToFolderCommand(this, this, SearchFoldersAsync));
        _registry.Register(new CopyToFolderCommand(this, this, SearchFoldersAsync));
        _registry.Register(new DeleteCommand(this));
        _registry.Register(new CopyPathsCommand());
        _registry.Register(new RevealInExplorerCommand());

        foreach (var (shortcut, ids) in _registry.FindShortcutConflicts())
            System.Diagnostics.Debug.WriteLine($"ショートカット重複: {shortcut} → {string.Join(", ", ids)}");
    }

    // ---- メニュー ----

    private MenuStrip BuildMainMenu()
    {
        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("ファイル(&F)");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("フォルダを開く(&O)...", null,
            async (_, _) => await ChooseFolderAsync()) { ShortcutKeys = Keys.Control | Keys.O });
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("新しいフォルダー(&N)...", null,
            async (_, _) => await CreateFolderAsync()) { ShortcutKeys = Keys.Control | Keys.N });
        fileMenu.DropDownItems.Add(RenameFolderItem("フォルダー名の変更(&M)..."));
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("再読み込み(&R)", null,
            (_, _) => RequestRefresh()) { ShortcutKeys = Keys.F5 });
        menu.Items.Add(fileMenu);

        var editMenu = new ToolStripMenuItem("編集(&E)");
        _undoItem = new ToolStripMenuItem("元に戻す(&U)", null, async (_, _) => await UndoRenameAsync())
            { ShortcutKeys = Keys.Control | Keys.Z, Enabled = false };
        editMenu.DropDownItems.Add(_undoItem);
        editMenu.DropDownItems.Add(new ToolStripSeparator());
        editMenu.DropDownItems.Add(new ToolStripMenuItem("すべて選択(&A)", null,
            (_, _) => _grid.SelectAll()) { ShortcutKeys = Keys.Control | Keys.A });
        editMenu.DropDownOpening += (_, _) => UpdateCommandEnabled();
        menu.Items.Add(editMenu);
        menu.Items.Add(BuildMarkMenu());
        menu.Items.Add(BuildViewMenu());
        menu.Items.Add(BuildGoMenu());

        // 登録済みコマンドをカテゴリ名のメニューに追加（同名のメニューがあればそこへ追記）
        foreach (var group in _registry.ByCategory())
        {
            var top = menu.Items.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => StripMnemonic(m.Text) == group.Key);
            if (top == null)
            {
                top = new ToolStripMenuItem(group.Key);
                menu.Items.Add(top);
            }
            else
            {
                top.DropDownItems.Add(new ToolStripSeparator());
            }
            foreach (var cmd in group)
                top.DropDownItems.Add(CreateCommandItem(cmd, withShortcut: true));
        }

        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("終了(&X)", null, (_, _) => Close()));

        var helpMenu = new ToolStripMenuItem("ヘルプ(&H)");
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("対応形式(&F)...", null, (_, _) => ShowSupportedFormats()));
        menu.Items.Add(helpMenu);
        return menu;
    }

    /// <summary>移動メニュー</summary>
    private ToolStripMenuItem BuildGoMenu()
    {
        var go = new ToolStripMenuItem("移動(&G)");
        _backItem = new ToolStripMenuItem("戻る(&B)", null, async (_, _) => await GoBackAsync()) { ShortcutKeys = Keys.Alt | Keys.Left };
        _forwardItem = new ToolStripMenuItem("進む(&F)", null, async (_, _) => await GoForwardAsync()) { ShortcutKeys = Keys.Alt | Keys.Right };
        _upItem = new ToolStripMenuItem("上のフォルダへ(&U)", null, async (_, _) => await GoUpAsync()) { ShortcutKeys = Keys.Alt | Keys.Up };
        go.DropDownItems.AddRange(new ToolStripItem[]
        {
            _backItem, _forwardItem, _upItem,
            new ToolStripMenuItem("ホームへ(&H)", null, async (_, _) => await LoadFolderAsync(HomeFolder)) { ShortcutKeys = Keys.Alt | Keys.Home },
            new ToolStripSeparator(),
            new ToolStripMenuItem("今のフォルダをホームに設定(&S)", null, (_, _) => { if (_folder != null) SetHome(_folder); }),
            new ToolStripMenuItem("ホームフォルダを選ぶ(&C)...", null, (_, _) => ChooseHome()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("アドレスバーに入力(&A)", null, (_, _) => _address.Focus()) { ShortcutKeys = Keys.Control | Keys.L },
            new ToolStripMenuItem("フォルダへジャンプ(&J)", null, (_, _) => StartJump()) { ShortcutKeys = Keys.Control | Keys.J },
            new ToolStripSeparator(),
            new ToolStripMenuItem("フォルダジャンプの設定(&O)...", null, (_, _) => ShowJumpSettings()),
        });
        return go;
    }

    /// <summary>表示メニュー（並び順）</summary>
    private ToolStripMenuItem BuildViewMenu()
    {
        var viewMenu = new ToolStripMenuItem("表示(&V)");
        foreach (var (label, mode) in new[]
                 {
                     ("名前順(&N)", SortMode.Name), ("更新日時順(&D)", SortMode.Modified),
                     ("サイズ順(&S)", SortMode.Size), ("手動（ドラッグで並べ替え）(&M)", SortMode.Manual),
                 })
        {
            var item = new ToolStripMenuItem(label, null, async (_, _) => await ChangeSortAsync(mode));
            _sortItems.Add((item, mode));
            viewMenu.DropDownItems.Add(item);
        }
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("手動の並び順を削除(&R)", null, async (_, _) => await DeleteManualOrderAsync()));
        UpdateSortChecks();
        return viewMenu;
    }

    private void UpdateSortChecks()
    {
        foreach (var (item, mode) in _sortItems) item.Checked = mode == _sortMode;
    }

    /// <summary>
    /// チェック（マーク）メニュー。¥ / Shift+¥ は修飾キー無しでメニューのショートカットにできないので
    /// グリッド側のキー処理で受け、メニューには表示だけする
    /// </summary>
    private ToolStripMenuItem BuildMarkMenu()
    {
        var markMenu = new ToolStripMenuItem("チェック(&K)");
        markMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("チェックを付ける / 外す（複数選択ならまとめて）(&T)", null, (_, _) => _grid.ToggleMarks())
                { ShortcutKeyDisplayString = "¥" },
            new ToolStripMenuItem("選択中の画像にチェック(&M)", null, (_, _) => _grid.SetMarkOnSelected(true))
                { ShortcutKeyDisplayString = "Shift+¥" },
            new ToolStripMenuItem("選択中の画像のチェックを外す(&U)", null, (_, _) => _grid.SetMarkOnSelected(false)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("チェックした画像を選択(&S)", null, (_, _) => _grid.SelectMarked())
                { ShortcutKeys = Keys.Control | Keys.Oem5, ShortcutKeyDisplayString = "Ctrl+¥" },
            new ToolStripSeparator(),
            new ToolStripMenuItem("すべての画像にチェック(&A)", null, (_, _) => _grid.MarkAll()),
            new ToolStripMenuItem("チェックを反転(&I)", null, (_, _) => _grid.InvertMarks()),
            new ToolStripMenuItem("チェックをすべて外す(&C)", null, (_, _) => _grid.ClearMarks())
                { ShortcutKeys = Keys.Control | Keys.Shift | Keys.Oem5, ShortcutKeyDisplayString = "Ctrl+Shift+¥" },
        });
        return markMenu;
    }

    /// <summary>ImageSharp で常に読める形式と、この PC の WIC 拡張機能で読める形式を表示</summary>
    private void ShowSupportedFormats()
    {
        string builtIn = string.Join(" ", ImageFormats.ImageSharpExtensions);
        var extra = WicCodecs.Decoders
            .Select(d => (d.FriendlyName, Exts: d.Extensions.Where(e => !ImageFormats.ImageSharpExtensions.Contains(e)).ToList()))
            .Where(d => d.Exts.Count > 0)
            .Select(d => $"・{d.FriendlyName}\n    {string.Join(" ", d.Exts)}");
        string wic = string.Join("\n", extra);
        if (wic.Length == 0) wic = "（なし）";
        MessageBox.Show(this,
            $"どの PC でも読める形式:\n{builtIn}\n\n" +
            $"この PC の Windows 拡張機能で読める形式:\n{wic}\n\n" +
            "HEIC は「HEIF 画像拡張機能」と「HEVC ビデオ拡張機能」、AVIF は「AV1 Video Extension」、\n" +
            "RAW は「Raw Image Extension」を Microsoft Store から入れると読めるようになります。",
            "対応形式", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BuildContextMenu()
    {
        _contextMenu.Opening += (_, _) => UpdateCommandEnabled();
        _contextMenu.Items.Add(new ToolStripMenuItem("新しいフォルダー...", null, async (_, _) => await CreateFolderAsync())
            { ShortcutKeyDisplayString = "Ctrl+N" });
        _contextMenu.Items.Add(RenameFolderItem("フォルダー名の変更..."));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem("チェックを付ける", null, (_, _) => _grid.SetMarkOnSelected(true))
            { ShortcutKeyDisplayString = "Shift+¥" });
        _contextMenu.Items.Add(new ToolStripMenuItem("チェックを外す", null, (_, _) => _grid.SetMarkOnSelected(false)));
        _contextMenu.Items.Add(new ToolStripMenuItem("チェックした画像を選択", null, (_, _) => _grid.SelectMarked())
            { ShortcutKeyDisplayString = "Ctrl+¥" });
        foreach (var group in _registry.ByCategory())
        {
            if (_contextMenu.Items.Count > 0) _contextMenu.Items.Add(new ToolStripSeparator());
            foreach (var cmd in group)
                _contextMenu.Items.Add(CreateCommandItem(cmd, withShortcut: false));
        }
    }

    /// <summary>ショートカットはメインメニュー側だけに割り当て、右クリックメニューは表示のみ（二重発火防止）</summary>
    private ToolStripMenuItem CreateCommandItem(IImageCommand cmd, bool withShortcut)
    {
        var item = new ToolStripMenuItem(cmd.Name, null, async (_, _) => await ExecuteAsync(cmd));
        var keys = Shortcuts.Parse(_registry.ShortcutOf(cmd));
        if (keys != Keys.None)
        {
            if (withShortcut) item.ShortcutKeys = keys;
            else item.ShortcutKeyDisplayString = new KeysConverter().ConvertToString(keys);
        }
        _commandItems.Add((item, cmd));
        return item;
    }

    private static string StripMnemonic(string? text) =>
        System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\(&.\)|&", "");

    // ---- コマンド実行 ----

    /// <summary>コマンドの対象（選択中の画像。フォルダのタイルは含まない）</summary>
    private IReadOnlyList<string> SelectedPaths() => _grid.SelectedImages.Select(f => f.FullName).ToList();

    private void UpdateCommandStates()
    {
        var paths = SelectedPaths();
        UpdateCommandEnabled(paths);

        string where = _folder ?? "フォルダ未選択（Ctrl+O で開く / フォルダをドロップ）";
        string text = _grid.Folders.Count > 0
            ? $"{where}   フォルダ {_grid.Folders.Count}   画像 {_grid.Items.Count} 枚"
            : $"{where}   画像 {_grid.Items.Count} 枚";
        if (paths.Count > 0) text += $"   選択 {paths.Count} 枚";
        if (_grid.MarkedCount > 0) text += $"   チェック {_grid.MarkedCount} 枚";
        _status.Text = text;
    }

    /// <summary>
    /// メニューの有効 / 無効（無効のままだとショートカットも効かない）。貼り付けはクリップボード次第なので、
    /// メニューを開いたとき・ほかのアプリから戻ったときにも更新する
    /// </summary>
    private void UpdateCommandEnabled(IReadOnlyList<string>? paths = null)
    {
        paths ??= SelectedPaths();
        foreach (var (item, cmd) in _commandItems)
            item.Enabled = cmd.CanExecute(paths);
        bool oneFolder = SelectedSingleFolder() != null;
        foreach (var item in _renameFolderItems) item.Enabled = oneFolder;
    }

    private async Task ExecuteAsync(IImageCommand cmd)
    {
        var paths = SelectedPaths();
        if (!cmd.CanExecute(paths)) return;
        try
        {
            await cmd.ExecuteAsync(new CommandContext(paths, this));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, $"{cmd.Name} に失敗しました",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateCommandEnabled(); // コピー・切り取りで貼り付けができるようになる
    }

    public void Notify(string message) => _status.Text = message;

    public string? CurrentFolder => _folder;

    private async Task TransferDroppedAsync(FileDrop drop)
    {
        try
        {
            await FileTransfer.RunAsync(this, drop.Paths, drop.Folder, drop.Move, Handle);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, drop.Move ? "移動できませんでした" : "コピーできませんでした", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public async Task FilesAddedAsync(string folder, IReadOnlyList<string> paths)
    {
        // 貼り付けている間に別のフォルダへ移っていたら何もしない
        if (!string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase)) return;
        await LoadFolderAsync(folder, NavKind.Reload);
        if (paths.Count > 0 && string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase)) _grid.SelectPaths(paths);
    }

    AppSettings ISettingsAccess.Settings => _settings;

    void ISettingsAccess.UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        _settings = change(_settings);
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"設定を保存できませんでした（この起動中だけ有効）: {ex.Message}");
        }
    }

    public void FilesRenamed(IReadOnlyList<RenameOp> ops)
    {
        if (_folder == null || ops.Count == 0) return;
        _lastRename = (ops, _orderStore.Load(_folder), _sortMode);
        ApplyRenamed(ops, restore: null);
    }

    /// <summary>
    /// 名前の変更を画面・チェック・手動の並び順に反映する。
    /// 手動の並びで、変更後の名前順と並びが一致したら（並べ替え → 連番の振り直しが済んだ）保存した並び順は消して名前順に戻す。
    /// restore を渡したとき（元に戻す）は、変更前の並び順と種類をそのまま復元する
    /// </summary>
    private void ApplyRenamed(IReadOnlyList<RenameOp> ops, (IReadOnlyList<string>? SavedOrder, SortMode Mode)? restore)
    {
        if (_folder == null) return;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in ops) map[op.From] = op.To;
        var items = _grid.Items.Select(f => map.TryGetValue(f.FullName, out var to) ? new FileInfo(to) : f).ToList();

        try
        {
            if (restore is { } r)
            {
                var (savedOrder, mode) = r;
                _sortMode = mode;
                if (savedOrder != null) _orderStore.Save(_folder, savedOrder);
                else _orderStore.Delete(_folder);
            }
            else if (_sortMode == SortMode.Manual)
            {
                var names = items.Select(f => f.Name).ToList();
                if (names.SequenceEqual(FileSorting.Sort(items, SortMode.Name).Select(f => f.Name)))
                {
                    _orderStore.Delete(_folder);
                    _sortMode = SortMode.Name;
                }
                else
                {
                    _orderStore.Save(_folder, names);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"並び順を保存できませんでした: {ex.Message}");
        }

        UpdateSortChecks();
        var saved = _sortMode == SortMode.Manual ? _orderStore.Load(_folder) : null;
        _grid.SetItems(Arrange(items, _sortMode, saved), reload: true, renamed: map);
        _undoItem.Enabled = _lastRename != null;
        _undoItem.Text = _lastRename != null ? "元に戻す: 名前の変更(&U)" : "元に戻す(&U)";
    }

    private void ClearUndo()
    {
        _lastRename = null;
        _undoItem.Enabled = false;
        _undoItem.Text = "元に戻す(&U)";
    }

    public void FilesRemoved(IReadOnlyList<string> paths)
    {
        if (_folder == null || paths.Count == 0) return;
        var gone = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        int first = _grid.Items.ToList().FindIndex(f => gone.Contains(f.FullName));
        if (first < 0) return; // 別のフォルダへ移っていた
        var items = _grid.Items.Where(f => !gone.Contains(f.FullName)).ToList();
        // 消したファイルの名前の変更は元に戻せない
        if (_lastRename is { } last && last.Ops.Any(o => gone.Contains(o.To))) ClearUndo();
        if (_sortMode == SortMode.Manual) SaveManualOrder(items);

        bool peeking = _quickLook.Visible;
        _grid.SetItems(items, reload: true); // 表示中の画像が消えるので Quick Look は一度閉じる
        if (items.Count == 0) return;
        // エクスプローラーと同じく、消した位置にある次の画像を選ぶ（Quick Look ならそのまま次を表示）
        int next = Math.Clamp(first, 0, items.Count - 1);
        _grid.SelectImage(next);
        if (peeking) _quickLook.Open(_grid.Items, next, byKey: false);
    }

    private async Task UndoRenameAsync()
    {
        if (_lastRename is not { } last) return;
        var (ops, savedOrder, mode) = last;
        var reverse = ops.Reverse().Select(o => new RenameOp(o.To, o.From)).ToList();
        UseWaitCursor = true;
        try
        {
            var done = await Task.Run(() => RenameExecutor.Execute(reverse));
            _lastRename = null;
            ApplyRenamed(done, (savedOrder, mode));
            Notify($"{done.Count} 件の名前を元に戻しました");
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "元に戻せませんでした", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    public async void RequestRefresh()
    {
        if (_folder != null) await LoadFolderAsync(_folder);
    }

    // ---- フォルダ読み込み ----

    private async Task ChooseFolderAsync()
    {
        using var dlg = new FolderBrowserDialog { InitialDirectory = _folder ?? "" };
        if (dlg.ShowDialog(this) == DialogResult.OK) await LoadFolderAsync(dlg.SelectedPath);
    }

    private async Task LoadFolderAsync(string folder, NavKind kind = NavKind.New)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        _status.Text = $"{folder} を読み込み中...";
        bool reload = string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase);
        List<DirectoryInfo> folders;
        List<FileInfo> files;
        SortMode mode;
        try
        {
            // 大きなフォルダでも UI を止めないよう列挙は別スレッドで行う。
            // 別のフォルダを開いたときは、手動の並び順が保存されていれば手動、無ければ名前順で始める
            (folders, files, mode) = await Task.Run(() =>
            {
                var listed = ImageFormats.ListImages(folder, cts.Token);
                var subfolders = FolderListing.ListSubfolders(folder, cts.Token);
                var saved = _orderStore.Load(folder);
                var m = reload ? _sortMode : saved != null ? SortMode.Manual : SortMode.Name;
                return (subfolders, Arrange(listed, m, saved), m);
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // 後から別のフォルダが開かれた
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "フォルダを開けません", MessageBoxButtons.OK, MessageBoxIcon.Error);
            UpdateCommandStates();
            return;
        }

        if (!reload) ClearUndo();
        _folder = folder;
        _sortMode = mode;
        if (kind == NavKind.New) _history.Navigate(folder);
        if (!reload)
        {
            _jump.RecordVisit(folder);
            if (++_visitsSinceSave >= 10)
            {
                _visitsSinceSave = 0;
                _ = Task.Run(_jump.SaveVisits);
            }
        }
        UpdateSortChecks();
        _grid.SetContents(folders, files, reload);
        if (!_address.Focused) _address.Text = folder;
        if (!reload) _ = _tree.RevealAsync(folder);
        string title = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        Text = $"{(title.Length > 0 ? title : folder)} - {AppTitle}";
        UpdateNavigationState();
        UpdateCommandStates();
    }

    // ---- 並び順 ----

    /// <summary>並び順を当てる。手動は保存した並び（無ければ名前順）</summary>
    private static List<FileInfo> Arrange(IReadOnlyList<FileInfo> files, SortMode mode, IReadOnlyList<string>? savedOrder)
    {
        var sorted = FileSorting.Sort(files, mode);
        return mode == SortMode.Manual && savedOrder != null ? ManualOrder.Apply(sorted, savedOrder) : sorted;
    }

    private async Task ChangeSortAsync(SortMode mode)
    {
        _sortMode = mode;
        UpdateSortChecks();
        if (_folder == null) return;
        string folder = _folder;
        var saved = mode == SortMode.Manual ? await Task.Run(() => _orderStore.Load(folder)) : null;
        _grid.SetItems(Arrange(_grid.Items, mode, saved), reload: true);
    }

    /// <summary>ドラッグで並べ替えた: 手動に切り替えて今の並びを保存</summary>
    private void SaveManualOrder() => SaveManualOrder(_grid.Items);

    private void SaveManualOrder(IReadOnlyList<FileInfo> items)
    {
        if (_folder == null) return;
        _sortMode = SortMode.Manual;
        UpdateSortChecks();
        try
        {
            _orderStore.Save(_folder, items.Select(f => f.Name).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"並び順を保存できませんでした: {ex.Message}");
        }
    }

    private async Task DeleteManualOrderAsync()
    {
        if (_folder == null) return;
        _orderStore.Delete(_folder);
        Notify("手動の並び順を削除しました");
        if (_sortMode == SortMode.Manual) await ChangeSortAsync(SortMode.Name);
    }

    private static string? DroppedFolder(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && Directory.Exists(paths[0])
            ? paths[0] : null;
}
