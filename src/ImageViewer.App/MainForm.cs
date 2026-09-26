// Glimpse - メイン画面
using ImageViewer.App.Chrome;
using ImageViewer.App.Commands;
using ImageViewer.App.Dialogs;
using ImageViewer.App.Filer;
using ImageViewer.App.Grid;
using ImageViewer.App.Jump;
using ImageViewer.App.Theming;
using ImageViewer.App.Viewer;
using ImageViewer.Core.Commands;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Jump;
using ImageViewer.Core.Navigation;
using ImageViewer.Core.Ordering;
using ImageViewer.Core.Rename;
using ImageViewer.Core.Settings;
using ImageViewer.Core.Thumbnails;
using ImageViewer.Core.Updates;

namespace ImageViewer.App;

public class MainForm : Form, ICommandHost, ISettingsAccess
{
    public const string AppTitle = "Glimpse";

    private readonly CommandRegistry _registry = new();
    private readonly ThumbnailService _thumbnails;
    private readonly ThumbnailGrid _grid;
    private readonly QuickLookView _quickLook = new() { Dock = DockStyle.Fill };
    private readonly FooterBar _footer = new();
    private readonly DetailsPanel _inspector = new() { Dock = DockStyle.Right };
    private readonly IconButton _inspectorButton = new() { Icon = Icons.Info, AccessibleName = "詳細パネル" };
    private ToolStripMenuItem _inspectorItem = null!;
    private string _selectionText = ""; // フッターに出す選択中の情報（詳細パネルを出している間は出さない）
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
        // Windows 標準のフォルダ名補完（追加の処理・索引は不要）
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.FileSystemDirectories,
    };
    private readonly AddressBox _addressBox;
    private readonly IconButton _backButton = new() { Icon = Icons.Back, AccessibleName = "戻る" };
    private readonly IconButton _forwardButton = new() { Icon = Icons.Forward, AccessibleName = "進む" };
    private readonly IconButton _upButton = new() { Icon = Icons.Up, AccessibleName = "上のフォルダへ" };
    private readonly ToolTip _toolTip = new();
    private ToolStripMenuItem _backItem = null!, _forwardItem = null!, _upItem = null!;

    // ---- ツールバー・☰ メニュー・サイドバー ----
    private readonly ToolBar _toolbar = new();
    private readonly IconButton _menuButton = new() { Icon = Icons.Menu, AccessibleName = "メニュー" };
    private readonly IconButton _sidebarButton = new() { Icon = Icons.Sidebar, AccessibleName = "サイドバー" };
    private readonly IconButton _sortButton = new() { DropDown = true, AccessibleName = "並び順" };
    private ContextMenuStrip _mainMenu = null!;
    private readonly ContextMenuStrip _sortMenu = new();
    private readonly NoFocusSplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
    private ToolStripMenuItem _sidebarItem = null!;
    private readonly List<(ToolStripMenuItem Item, ThemeMode Mode)> _themeItems = new();
    // メニュー項目のショートカット（メニューバーが無いので自分で振り分ける）
    private readonly Dictionary<Keys, ToolStripMenuItem> _menuShortcuts = new();
    private DateTime _menuClosedAt;

    // ---- フッターの操作ボタン ----
    /// <summary>操作の対象（選択中の画像 / チェックした画像）。選択が変わると「選択」に戻る</summary>
    private enum ActionTarget { Selection, Checked }
    private ActionTarget _target;
    private readonly IconButton _targetSelection = new() { AccessibleName = "選択中の画像を対象にする" };
    private readonly IconButton _targetChecked = new() { AccessibleName = "チェックした画像を対象にする" };
    private readonly List<(IconButton Button, IImageCommand Command)> _actionButtons = new();
    private readonly ContextMenuStrip _moveMenu = new(), _moreMenu = new();

    // ---- フォルダジャンプ ----
    private readonly FolderJumpService _jump = new(FolderJumpService.DefaultDataDir);
    private readonly JumpList _jumpList = new();
    private readonly System.Windows.Forms.Timer _jumpDelay = new() { Interval = 120 };
    private EverythingClient? _everything;
    private CancellationTokenSource? _jumpCts;
    private int _visitsSinceSave;

    // ---- 更新の確認 ----
    private ToolStripMenuItem _updateItem = null!;
    private HttpClient? _http;
    private ReleaseInfo? _available;

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
        Icon = AppIcon.Current;
        Font = new Font("Yu Gothic UI", 9F);
        ClientSize = new Size(980, 700);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        _settings = _settingsStore.Load();
        Theme.Initialize(Theme.ParseMode(_settings.Theme));
        RegisterCommands();
        _addressBox = new AddressBox(_address);

        // サムネイルはメモリ上に最大 128MB まで持つ（大きさは表示サイズに合わせて段階的に決まる）
        _thumbnails = new ThumbnailService(LogicalToDeviceUnits(160), 128L * 1024 * 1024,
            SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
        _grid = new ThumbnailGrid(_thumbnails, _settings.ThumbnailSize ?? 160) { Dock = DockStyle.Fill, ContextMenuStrip = _contextMenu };
        _grid.SelectionChanged += (_, _) =>
        {
            _target = ActionTarget.Selection; // 選び直したら、選んだ画像が対象
            UpdateCommandStates();
        };
        _grid.MarksChanged += (_, _) =>
        {
            if (_grid.MarkedCount == 0) _target = ActionTarget.Selection;
            UpdateCommandStates();
        };
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
            _http?.Dispose();
            _thumbnails.Dispose();
            _jump.SaveVisits();
            _everything?.Dispose();
        };

        _footer.UpdateClicked += (_, _) => ShowUpdateDialog();
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _noticeActive = false;
            UpdateCommandStates();
        };
        SetUpActionBar();
        // フッター右端のボタンはライト ↔ ダークだけ（システムに合わせるは ☰ → 表示 → テーマ）
        _footer.ThemeButton.Click += (_, _) => SetThemeMode(Theme.Current.IsDark ? ThemeMode.Light : ThemeMode.Dark);
        SetUpThumbnailSizeSlider();
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

        _split.SplitterWidth = LogicalToDeviceUnits(4);
        _split.Panel1.Controls.Add(_tree);
        _split.Panel1.Padding = new Padding(0, 0, 1, 0); // サイドバーの右端の線（Panel1 の地の色で描く）
        _split.Panel2.Controls.Add(_grid);
        _split.Panel2.Controls.Add(_quickLook);
        SetUpQuickLook();

        // Dock は後から追加したものから順に場所を取るので、Fill → 右の詳細パネル → ツールバー → フッター の順に追加
        _mainMenu = BuildMainMenu();
        Controls.Add(_split);
        Controls.Add(_inspector);
        Controls.Add(BuildToolBar());
        Controls.Add(_footer);
        _inspector.Visible = _inspectorButton.Active = _settings.InspectorVisible ?? true;
        // 1 枚表示の間は一覧の右の詳細パネルを隠す（1 枚表示には I で出す自分の詳細パネルがある。画像を広く見せる）
        _quickLook.VisibleChanged += (_, _) =>
        {
            _inspector.Visible = _inspectorItem.Checked && !_quickLook.Visible;
            _inspectorButton.Active = _quickLook.Visible ? _quickLook.Details.Visible : _inspectorItem.Checked;
        };
        _quickLook.Details.Visible = _settings.QuickLookDetailsVisible ?? false;
        _quickLook.DetailsToggled += (_, _) =>
        {
            _inspectorButton.Active = _quickLook.Details.Visible;
            ((ISettingsAccess)this).UpdateSettings(s => s with { QuickLookDetailsVisible = _quickLook.Details.Visible ? true : null });
        };
        _split.SplitterDistance = LogicalToDeviceUnits(220);
        _split.Panel1Collapsed = !(_settings.SidebarVisible ?? true);
        BuildContextMenu();
        SetUpMenuKeys();
        ApplyTheme();
        Theme.Changed += OnThemeChanged;
        FormClosed += (_, _) => Theme.Changed -= OnThemeChanged;

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
        SetUpUpdateCheck();
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
        // 開くときは一覧のサムネイルから広がり、閉じるときはそこへ戻る
        // 一覧の右の詳細パネルは 1 枚表示の間は隠れるので、後ろの画像に入れておく（開いた直後にその場所が空かないように）
        _quickLook.BackdropProvider = () => new Control[] { _grid, _inspector };
        _quickLook.ThumbBoundsProvider = i => _grid.ImageThumbBounds(i) is Rectangle r ? _grid.RectangleToScreen(r) : null;
        // 閉じる動きの行き先は、詳細パネルを戻して一覧が並び直した後の位置で決める
        _quickLook.Closing += (_, _) =>
        {
            _inspector.Visible = _inspectorItem.Checked;
            PerformLayout();
        };

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

    private void SetUpThumbnailSizeSlider()
    {
        var slider = _footer.Slider;
        slider.Minimum = ThumbnailGrid.MinThumbnailSize;
        slider.Maximum = ThumbnailGrid.MaxThumbnailSize;
        slider.Step = ThumbnailGrid.ThumbnailSizeStep;
        slider.Value = _grid.ThumbnailSize;
        slider.AccessibleName = "サムネイルの大きさ";
        _footer.SetSliderToolTip("サムネイルの大きさ（Ctrl+ホイールでも変えられます）");

        slider.ValueChanged += (_, _) => _grid.ThumbnailSize = slider.Value;
        _grid.ThumbnailSizeChanged += (_, size) => slider.Value = size;
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

    /// <summary>選択中の画像の情報を、フッター（詳細パネルを閉じているとき）と詳細パネル（一覧の右・1 枚表示）に出す</summary>
    private async Task UpdateSelectionInfoAsync()
    {
        _infoCts?.Cancel();
        var images = _grid.SelectedImages;
        var folders = _grid.SelectedFolders;
        DetailsPanel[] panels = { _inspector, _quickLook.Details };

        if (images.Count == 0 && folders.Count == 1)
        {
            SetSelectionText($"フォルダー ・ {folders[0].LastWriteTime:yyyy/MM/dd HH:mm}");
            foreach (var panel in panels) panel.ShowFolder(folders[0]);
            return;
        }
        if (images.Count != 1)
        {
            SetSelectionText(images.Count == 0 ? "" : $"{images.Count} 枚 ・ 合計 {FormatBytes(images.Sum(SafeLength))}");
            foreach (var panel in panels)
            {
                if (images.Count == 0) panel.ShowNothing();
                else panel.ShowMultiple(images);
            }
            return;
        }

        var file = images[0];
        string rest = $"{FormatBytes(SafeLength(file))} ・ {file.LastWriteTime:yyyy/MM/dd HH:mm}";
        var key = ThumbnailKey.From(file);
        if (!_infoCache.TryGetValue(key, out var info))
        {
            // 大きさ・撮影情報を読んでいる間も、サイズと日時は先に出す
            SetSelectionText(rest);
            foreach (var panel in panels) panel.ShowImage(file, null, loading: true);
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
        SetSelectionText(info != null ? $"{info.Width} × {info.Height} ・ {info.Format} ・ {rest}" : rest);
        foreach (var panel in panels) panel.ShowImage(file, info, loading: false);
    }

    private void SetSelectionText(string text)
    {
        _selectionText = text;
        _footer.SelectionInfo = _inspectorItem.Checked ? "" : text;
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

        // 古いパスを覚えているもの（ツリー・戻る / 進む・フォルダジャンプ・ホーム）を新しいパスに付け替える
        _tree.FolderRenamed(oldPath, newPath);
        _history.Retarget(oldPath, newPath);
        _jump.FolderRenamed(oldPath, newPath);
        if (_settings.HomeFolder is string home && FolderListing.Retarget(home, oldPath, newPath) is string newHome) SetHome(newHome);
        await LoadFolderAsync(_folder, NavKind.Reload);
        _grid.SelectPath(newPath);
        Notify($"フォルダー名を変更しました: {oldName} → {dlg.Value}");
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

    private Control BuildToolBar()
    {
        foreach (var (button, tip) in new[]
                 {
                     (_menuButton, "メニュー (Alt)"), (_sidebarButton, "サイドバーの表示 / 非表示"),
                     (_backButton, "戻る (Alt+←)"), (_forwardButton, "進む (Alt+→)"), (_upButton, "上のフォルダへ (Alt+↑ / Backspace)"),
                     (_sortButton, "並び順"), (_inspectorButton, "詳細パネル (Ctrl+I)"),
                 })
            _toolTip.SetToolTip(button, tip);
        _toolbar.AddLeft(_menuButton, _sidebarButton, ToolBar.Separator, _backButton, _forwardButton, _upButton);
        _toolbar.SetFill(_addressBox);
        _addressBox.SegmentClicked += async (_, path) => await LoadFolderAsync(path);
        _toolbar.AddRight(_sortButton, _inspectorButton);
        _inspectorButton.Click += (_, _) => ToggleDetails();
        _toolbar.MouseDown += OnMouseBackForward;

        _menuButton.Click += (_, _) => ShowMainMenu(selectFirst: false);
        _mainMenu.Closed += (_, _) =>
        {
            _menuButton.Active = false;
            _menuClosedAt = DateTime.UtcNow;
        };
        _sidebarButton.Click += (_, _) => SetSidebarVisible(_split.Panel1Collapsed);
        _backButton.Click += async (_, _) => await GoBackAsync();
        _forwardButton.Click += async (_, _) => await GoForwardAsync();
        _upButton.Click += async (_, _) => await GoUpAsync();

        AddSortItems(_sortMenu.Items);
        _sortButton.Click += (_, _) =>
        {
            if ((DateTime.UtcNow - _menuClosedAt).TotalMilliseconds < 150) return; // 開いているメニューを閉じるためのクリック
            _sortButton.Active = true;
            _sortMenu.Show(_sortButton, new Point(0, _sortButton.Height));
        };
        _sortMenu.Closed += (_, _) =>
        {
            _sortButton.Active = false;
            _menuClosedAt = DateTime.UtcNow;
        };
        UpdateSortChecks();

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
                if (_jumpList.Visible && _jumpList.SelectedCommand is { } command) RunCommandCandidate(command);
                else if (_jumpList.Visible && _jumpList.SelectedPath is string picked) await JumpToAsync(picked);
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

        return _toolbar;
    }

    // ---- フォルダジャンプ ----

    private void SetUpJump()
    {
        _jumpList.Font = Font;
        _jumpList.ItemHeight = Font.Height * 2 + LogicalToDeviceUnits(6);
        Controls.Add(_jumpList);
        _jumpList.BringToFront();
        _jumpList.Picked += async (_, path) => await JumpToAsync(path);
        _jumpList.CommandPicked += (_, command) => RunCommandCandidate(command);
        // 候補をクリックすると一覧にフォーカスが移る。その間はアドレスバーの入力を続け、一覧からも離れたら閉じる
        _addressBox.KeepEditing = () => _jumpList.Focused;
        _jumpList.LostFocus += (_, _) => BeginInvoke(() =>
        {
            if (!_jumpList.Focused && !_address.Focused) HideJumpList();
        });

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

    /// <summary>
    /// アドレスバーの入力から候補を出す: ☰ メニューのコマンド（先に数件）とフォルダ。
    /// 「>」で始めるとコマンドだけ（「>」だけなら全部）。パスらしい入力・今のフォルダのままなら出さない
    /// </summary>
    private async Task UpdateJumpListAsync()
    {
        string query = _address.Text.Trim();
        bool commandsOnly = query.StartsWith('>');
        string text = commandsOnly ? query[1..].Trim() : query;
        if (!_address.Focused || query.Length == 0
            || (!commandsOnly && (FolderListing.LooksLikePath(query) || string.Equals(query, _folder, StringComparison.OrdinalIgnoreCase))))
        {
            HideJumpList();
            return;
        }

        _jumpCts?.Cancel();
        var cts = _jumpCts = new CancellationTokenSource();
        var commands = FindCommands(text, commandsOnly ? int.MaxValue : 5);
        IReadOnlyList<JumpResult> results = Array.Empty<JumpResult>();
        string? message = commandsOnly ? "コマンドが見つかりません" : null;
        if (!commandsOnly)
        {
            try
            {
                (results, message) = await SearchFoldersAsync(text, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // 次の入力で検索し直している
            }
        }
        if (cts.IsCancellationRequested || !_address.Focused) return;

        _jumpList.SetResults(commands, results, message);
        var below = PointToClient(_addressBox.Parent!.PointToScreen(new Point(_addressBox.Left, _addressBox.Bottom)));
        _jumpList.SetBounds(below.X, below.Y + LogicalToDeviceUnits(4), _addressBox.Width, _jumpList.Height);
        _jumpList.Visible = true;
        _jumpList.BringToFront();
    }

    /// <summary>
    /// ☰ メニューの項目（コマンド・表示の切り替えなど）を名前とメニューの場所で探す。スペース区切りはすべてを含むもの。
    /// ひらがな / カタカナ・全角 / 半角・大文字 / 小文字は区別しない。名前の先頭が合うもの → 名前に含むもの → 場所だけ合うもの の順
    /// </summary>
    private List<CommandCandidate> FindCommands(string text, int max)
    {
        UpdateCommandEnabled();
        var compare = System.Globalization.CultureInfo.GetCultureInfo("ja-JP").CompareInfo;
        const System.Globalization.CompareOptions options = System.Globalization.CompareOptions.IgnoreCase
            | System.Globalization.CompareOptions.IgnoreKanaType | System.Globalization.CompareOptions.IgnoreWidth;
        var tokens = text.Split(' ', '　').Where(t => t.Length > 0).ToArray();

        var found = new List<(CommandCandidate Candidate, int Score, int Order)>();
        void Walk(ToolStripItemCollection items, string where)
        {
            foreach (var item in items.OfType<ToolStripMenuItem>())
            {
                if (!item.Available) continue;
                string name = StripMnemonic(item.Text).TrimEnd('.', '…').Trim();
                if (item.DropDownItems.Count > 0)
                {
                    Walk(item.DropDownItems, where.Length > 0 ? $"{where} › {name}" : name);
                    continue;
                }
                string all = $"{name} {where}";
                if (!tokens.All(t => compare.IndexOf(all, t, options) >= 0)) continue;
                int score = tokens.Length == 0 || compare.IsPrefix(name, tokens[0], options) ? 0
                    : tokens.All(t => compare.IndexOf(name, t, options) >= 0) ? 1 : 2;
                var target = item;
                found.Add((new CommandCandidate(name, where, item.ShortcutKeyDisplayString ?? "", item.Enabled, () => target.PerformClick()), score, found.Count));
            }
        }
        Walk(_mainMenu.Items, "");
        return found.OrderBy(f => f.Score).ThenBy(f => f.Candidate.Enabled ? 0 : 1).ThenBy(f => f.Order)
            .Take(max).Select(f => f.Candidate).ToList();
    }

    private void RunCommandCandidate(CommandCandidate command)
    {
        if (!command.Enabled)
        {
            System.Media.SystemSounds.Beep.Play();
            Notify($"「{command.Name}」は今は実行できません（対象の画像を選んでから）");
            // クリックで一覧にフォーカスが移っていたら、入力の続きができるようにアドレスバーへ戻す（入力した文字はそのまま）
            if (!_address.Focused)
            {
                _address.Focus();
                BeginInvoke(() => _address.Select(_address.TextLength, 0));
            }
            return;
        }
        HideJumpList();
        _address.Text = _folder ?? "";
        _grid.Focus(); // アドレスバーから離れてから実行する（ダイアログ・キー操作が一覧に戻るように）
        command.Run();
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
        if (!_address.Focused) _addressBox.EndEdit(); // 一覧をクリックしていた: 入力もやめる
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
        _addressBox.BeginEdit();
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
        if (keyData == Keys.Escape && _grid.Focused && _grid.SelectedCount > 0)
        {
            _grid.ClearSelection();
            return true;
        }
        if (keyData == Keys.F10)
        {
            ShowMainMenu(selectFirst: true);
            return true;
        }
        if (_menuShortcuts.TryGetValue(keyData, out var item))
        {
            UpdateCommandEnabled(); // 貼り付けなどはクリップボード次第なので押したときに確かめる
            if (item.Enabled)
            {
                item.PerformClick();
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- フッターの操作ボタン（画像を選んでいる間） ----

    private void SetUpActionBar()
    {
        var bar = _footer.Actions;
        _targetChecked.Icon = Icons.Dot(Theme.Current.Check);
        _targetSelection.Click += (_, _) => SetTarget(ActionTarget.Selection);
        _targetChecked.Click += (_, _) => SetTarget(ActionTarget.Checked);
        _toolTip.SetToolTip(_targetSelection, "選択中の画像に対して実行");
        _toolTip.SetToolTip(_targetChecked, "チェックした画像に対して実行（選択し直さなくてよい）");
        bar.AddTargets(_targetSelection, _targetChecked);

        IconButton Action(string id, string text, IconPainter icon, bool danger = false)
        {
            var cmd = _registry.Find(id) ?? throw new InvalidOperationException($"コマンドが登録されていません: {id}");
            string shortcut = ShortcutText(cmd);
            var button = new IconButton { Text = text, Icon = icon, Danger = danger, Hint = shortcut, AccessibleName = cmd.Name };
            _toolTip.SetToolTip(button, shortcut.Length > 0 ? $"{cmd.Name.TrimEnd('.')} ({shortcut})" : cmd.Name.TrimEnd('.'));
            button.Click += async (_, _) => await ExecuteAsync(cmd);
            _actionButtons.Add((button, cmd));
            return button;
        }

        // リサイズ ▾: 本体は設定画面、▾ は前回の設定のまま実行
        var resizeMenu = new ContextMenuStrip();
        var resizeMore = new IconButton { DropDown = true, AccessibleName = "リサイズのその他" };
        _toolTip.SetToolTip(resizeMore, "前回の設定のまま実行");
        resizeMore.Click += (_, _) => ShowFooterMenu(resizeMore, resizeMenu, () => BuildResizeMenu(resizeMenu));

        var move = new IconButton { Text = "移動", Icon = Icons.Move, DropDown = true, AccessibleName = "移動" };
        _toolTip.SetToolTip(move, "最近の移動先へ移動（Ctrl を押しながら選ぶとコピー）");
        move.Click += (_, _) => ShowFooterMenu(move, _moveMenu, BuildMoveMenu);
        var more = new IconButton { Icon = Icons.More, AccessibleName = "その他の操作" };
        _toolTip.SetToolTip(more, "その他の操作");
        more.Click += (_, _) => ShowFooterMenu(more, _moreMenu, null);
        bar.AddActions(Action("image.resize", "リサイズ", Icons.Resize), resizeMore, Action("image.crop", "切り抜き", Icons.Crop),
            Action("image.combine", "連結", Icons.Combine), Action("file.rename", "名前", Icons.Rename), move, more);

        var clear = new IconButton { Icon = Icons.Close, AccessibleName = "選択を解除" };
        _toolTip.SetToolTip(clear, "選択を解除 (Esc)");
        clear.Click += (_, _) => _grid.ClearSelection();
        bar.AddTrailing(Action("file.delete", "削除", Icons.Trash, danger: true), clear);

        // ⋯: ボタンに出していない操作
        foreach (var id in new[] { "edit.cut", "edit.copy", "file.copyTo", "file.copyPaths", "file.revealInExplorer" })
            if (_registry.Find(id) is { } cmd) _moreMenu.Items.Add(CreateCommandItem(cmd, withShortcut: false));
        _moreMenu.Items.Add(new ToolStripSeparator());
        _moreMenu.Items.Add(new ToolStripMenuItem("チェックを付ける", null, (_, _) => _grid.SetMarkOnSelected(true)) { ShortcutKeyDisplayString = "Shift+¥" });
        _moreMenu.Items.Add(new ToolStripMenuItem("チェックを外す", null, (_, _) => _grid.SetMarkOnSelected(false)));
        _moreMenu.Opening += (_, _) => UpdateCommandEnabled();
    }

    private string ShortcutText(IImageCommand cmd)
    {
        var keys = Shortcuts.Parse(_registry.ShortcutOf(cmd));
        return keys switch
        {
            Keys.None => "",
            Keys.Delete => "Del",
            _ => new KeysConverter().ConvertToString(keys) ?? "",
        };
    }

    private void SetTarget(ActionTarget target)
    {
        _target = target;
        UpdateCommandStates();
    }

    /// <summary>画像を選んでいる間はフッターを操作ボタンに（高さは変えない）</summary>
    private void UpdateActionBar()
    {
        int selected = _grid.SelectedImages.Count, marked = _grid.MarkedCount;
        _footer.ActionMode = selected > 0;
        if (selected == 0) return;
        _targetSelection.Text = $"選択 {selected}";
        _targetSelection.Active = _target == ActionTarget.Selection;
        _targetChecked.Text = $"チェック {marked}";
        _targetChecked.Active = _target == ActionTarget.Checked;
        _footer.Actions.SetShown(_targetChecked, marked > 0);
        _footer.Actions.PerformLayout();
    }

    /// <summary>フッターのボタンから上向きにメニューを開く（開いているときのクリックは閉じるだけ）</summary>
    private void ShowFooterMenu(IconButton button, ContextMenuStrip menu, Action? build)
    {
        if ((DateTime.UtcNow - _menuClosedAt).TotalMilliseconds < 150) return;
        build?.Invoke();
        button.Active = true;
        void Closed(object? s, ToolStripDropDownClosedEventArgs e)
        {
            menu.Closed -= Closed;
            button.Active = false;
            _menuClosedAt = DateTime.UtcNow;
        }
        menu.Closed += Closed;
        menu.Show(button, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
    }

    /// <summary>リサイズ ▾: 前回の設定のまま実行（設定の説明付き）と、設定を開く</summary>
    private void BuildResizeMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        if (_registry.Find("image.resizeQuick") is not { } quick || _registry.Find("image.resize") is not { } dialog) return;
        var options = _settings.Resize;
        var paths = TargetPaths();
        menu.Items.Add(new ToolStripMenuItem(quick.Name, null, async (_, _) => await ExecuteAsync(quick))
        {
            ShortcutKeyDisplayString = ShortcutText(quick),
            Enabled = quick.CanExecute(paths),
            ToolTipText = options == null ? "前回の設定がまだありません（設定画面を開きます）" : null,
        });
        menu.Items.Add(new ToolStripMenuItem(options == null ? "（前回の設定なし）"
            : $"{Core.Editing.Converter.Describe(options)}・{Core.Editing.Converter.DescribeOutput(options)}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("設定を開く...", null, async (_, _) => await ExecuteAsync(dialog))
        {
            ShortcutKeyDisplayString = ShortcutText(dialog),
            Enabled = dialog.CanExecute(paths),
        });
    }

    /// <summary>移動 ▾: 最近の移動先（数字キーで選べる。Ctrl を押しながらでコピー）と、探して移動 / コピー</summary>
    private void BuildMoveMenu()
    {
        _moveMenu.Items.Clear();
        var recent = (_settings.RecentDestinations ?? Array.Empty<string>())
            .Where(f => Directory.Exists(f) && !string.Equals(f, _folder, StringComparison.OrdinalIgnoreCase))
            .Take(9).ToList();
        if (recent.Count == 0)
            _moveMenu.Items.Add(new ToolStripMenuItem("最近の移動先はまだありません") { Enabled = false });
        for (int i = 0; i < recent.Count; i++)
        {
            string folder = recent[i];
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            _moveMenu.Items.Add(new ToolStripMenuItem($"&{i + 1}  {(name.Length > 0 ? name : folder)}", null,
                async (_, _) => await TransferToAsync(folder, move: (ModifierKeys & Keys.Control) == 0))
            {
                ShortcutKeyDisplayString = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) ?? "",
                ToolTipText = folder,
            });
        }
        _moveMenu.Items.Add(new ToolStripSeparator());
        foreach (var id in new[] { "file.moveTo", "file.copyTo" })
        {
            if (_registry.Find(id) is not { } cmd) continue;
            _moveMenu.Items.Add(new ToolStripMenuItem(cmd.Name.Replace("フォルダーへ", "フォルダーを探して"), null, async (_, _) => await ExecuteAsync(cmd))
            {
                ShortcutKeyDisplayString = ShortcutText(cmd),
                Enabled = cmd.CanExecute(TargetPaths()),
            });
        }
    }

    private async Task TransferToAsync(string folder, bool move)
    {
        var paths = TargetPaths();
        if (paths.Count == 0) return;
        ((ISettingsAccess)this).UpdateSettings(s => s.WithRecentDestination(folder));
        try
        {
            await FileTransfer.RunAsync(this, paths, folder, move, Handle);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, move ? "移動できませんでした" : "コピーできませんでした", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- ☰ メニュー・サイドバー・テーマ ----

    private void ShowMainMenu(bool selectFirst)
    {
        if (_mainMenu.Visible || (DateTime.UtcNow - _menuClosedAt).TotalMilliseconds < 150) return; // 開いているメニューを閉じるためのクリック
        _menuButton.Active = true;
        _mainMenu.Show(_menuButton, new Point(0, _menuButton.Height));
        if (selectFirst && _mainMenu.Items.Count > 0) _mainMenu.Items[0].Select();
    }

    /// <summary>
    /// ☰ メニューの項目のショートカットを覚えて、項目には表示だけ残す（表示していないメニューのショートカットは
    /// WinForms では働かないため ProcessCmdKey で振り分ける）。Alt だけを押して離すと ☰ を開く
    /// </summary>
    private void SetUpMenuKeys()
    {
        var converter = new KeysConverter();
        void Collect(ToolStripItemCollection items)
        {
            foreach (var item in items.OfType<ToolStripMenuItem>())
            {
                if (item.ShortcutKeys != Keys.None)
                {
                    item.ShortcutKeyDisplayString ??= converter.ConvertToString(item.ShortcutKeys);
                    _menuShortcuts[item.ShortcutKeys] = item;
                    item.ShortcutKeys = Keys.None;
                }
                Collect(item.DropDownItems);
            }
        }
        Collect(_mainMenu.Items);

        var altFilter = new AltKeyFilter(this, () => ShowMainMenu(selectFirst: true));
        Application.AddMessageFilter(altFilter);
        FormClosed += (_, _) => Application.RemoveMessageFilter(altFilter);
    }

    /// <summary>Alt を押して、ほかのキーを押さずに離したとき（メニューバーと同じ操作で ☰ を開く）</summary>
    private sealed class AltKeyFilter(Form form, Action open) : IMessageFilter
    {
        private const int WmKeyDown = 0x100, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105, WmLButtonDown = 0x201, WmRButtonDown = 0x204;
        private const int VkMenu = 0x12;
        private bool _armed;

        public bool PreFilterMessage(ref Message m)
        {
            switch (m.Msg)
            {
                case WmSysKeyDown when (int)m.WParam == VkMenu:
                    if (((long)m.LParam & (1L << 30)) == 0) _armed = Form.ActiveForm == form; // 押し続けの繰り返しは無視
                    break;
                case WmSysKeyDown or WmKeyDown or WmLButtonDown or WmRButtonDown:
                    _armed = false;
                    break;
                case WmSysKeyUp when (int)m.WParam == VkMenu:
                    if (!_armed) break;
                    _armed = false;
                    open();
                    return true; // Windows のシステムメニューにフォーカスを移さない
            }
            return false;
        }
    }

    private void SetSidebarVisible(bool visible)
    {
        _split.Panel1Collapsed = !visible;
        _sidebarItem.Checked = visible;
        ((ISettingsAccess)this).UpdateSettings(s => s with { SidebarVisible = visible ? null : false });
    }

    /// <summary>ⓘ / Ctrl+I: 1 枚表示の間はその詳細パネル、それ以外は一覧の右の詳細パネルを出す / 閉じる</summary>
    private void ToggleDetails()
    {
        if (_quickLook.Visible) _quickLook.ToggleDetails();
        else SetInspectorVisible(!_inspectorItem.Checked);
    }

    private void SetInspectorVisible(bool visible)
    {
        _inspectorItem.Checked = _inspectorButton.Active = visible;
        _inspector.Visible = visible && !_quickLook.Visible;
        _footer.SelectionInfo = visible ? "" : _selectionText;
        ((ISettingsAccess)this).UpdateSettings(s => s with { InspectorVisible = visible ? null : false });
    }

    private void SetThemeMode(ThemeMode mode)
    {
        ((ISettingsAccess)this).UpdateSettings(s => s with { Theme = Theme.ToSetting(mode) });
        foreach (var (item, m) in _themeItems) item.Checked = m == mode;
        Theme.SetMode(mode); // 変わっていれば OnThemeChanged が呼ばれる
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    /// <summary>今の配色を画面の各部品に当てる</summary>
    private void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Background;
        _toolbar.ApplyTheme();
        _footer.ApplyTheme();
        _addressBox.ApplyTheme();
        _split.BackColor = p.Background;
        _split.Panel1.BackColor = p.Border;
        _split.Panel2.BackColor = p.Background;
        _tree.ApplyTheme();
        _grid.ApplyTheme();
        _jumpList.ApplyTheme();
        _inspector.Invalidate();
        Theme.ApplyTitleBar(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    private void RegisterCommands()
    {
        _registry.Register(new CutFilesCommand());
        _registry.Register(new CopyFilesCommand());
        _registry.Register(new PasteFilesCommand(this));
        var resize = new ResizeCommand(this, this);
        _registry.Register(resize);
        _registry.Register(new QuickResizeCommand(this, this, resize));
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

    /// <summary>☰ メニュー（今までのメニューバーの中身をそのまま入れる。ショートカットは SetUpMenuKeys で振り分ける）</summary>
    private ContextMenuStrip BuildMainMenu()
    {
        var menu = new ContextMenuStrip();
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
        _updateItem = new ToolStripMenuItem("更新(&N)...", null, (_, _) => ShowUpdateDialog()) { Visible = false };
        var checkOnStartup = new ToolStripMenuItem("起動時に更新を確認(&S)") { CheckOnClick = true, Checked = _settings.CheckUpdatesOnStartup ?? true };
        checkOnStartup.CheckedChanged += (_, _) =>
            ((ISettingsAccess)this).UpdateSettings(s => s with { CheckUpdatesOnStartup = checkOnStartup.Checked });
        helpMenu.DropDownItems.Add(_updateItem);
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("対応形式(&F)...", null, (_, _) => ShowSupportedFormats()));
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("更新を確認(&U)...", null, async (_, _) => await CheckForUpdatesAsync(manual: true)));
        helpMenu.DropDownItems.Add(checkOnStartup);
        menu.Items.Add(helpMenu);

        // よく使うものを上に（カテゴリで増えたメニューはヘルプの前）
        string[] order = { "ファイル", "編集", "画像", "移動", "表示", "チェック" };
        var sorted = menu.Items.Cast<ToolStripItem>()
            .OrderBy(i => i == helpMenu ? int.MaxValue : Array.IndexOf(order, StripMnemonic(i.Text)) is var n and >= 0 ? n : order.Length)
            .ToList();
        menu.Items.Clear();
        menu.Items.AddRange(sorted.ToArray());
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
            new ToolStripMenuItem("アドレスバーに入力(&A)", null, (_, _) => _addressBox.BeginEdit()) { ShortcutKeys = Keys.Control | Keys.L },
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
        AddSortItems(viewMenu.DropDownItems);
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        _sidebarItem = new ToolStripMenuItem("サイドバー(&B)", null, (_, _) => SetSidebarVisible(_split.Panel1Collapsed))
            { Checked = _settings.SidebarVisible ?? true };
        viewMenu.DropDownItems.Add(_sidebarItem);
        _inspectorItem = new ToolStripMenuItem("詳細パネル(&I)", null, (_, _) => ToggleDetails())
            { Checked = _settings.InspectorVisible ?? true, ShortcutKeys = Keys.Control | Keys.I };
        viewMenu.DropDownItems.Add(_inspectorItem);
        var themeMenu = new ToolStripMenuItem("テーマ(&T)");
        foreach (var (label, mode) in new[] { ("システムに合わせる(&S)", ThemeMode.System), ("ライト(&L)", ThemeMode.Light), ("ダーク(&D)", ThemeMode.Dark) })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => SetThemeMode(mode)) { Checked = Theme.Mode == mode };
            _themeItems.Add((item, mode));
            themeMenu.DropDownItems.Add(item);
        }
        viewMenu.DropDownItems.Add(themeMenu);
        return viewMenu;
    }

    /// <summary>並び順の項目（☰ の表示メニューとツールバーの並び順ボタンの両方に作る）</summary>
    private void AddSortItems(ToolStripItemCollection items)
    {
        foreach (var (label, mode) in new[]
                 {
                     ("名前順(&N)", SortMode.Name), ("更新日時順(&D)", SortMode.Modified),
                     ("サイズ順(&S)", SortMode.Size), ("手動（ドラッグで並べ替え）(&M)", SortMode.Manual),
                 })
        {
            var item = new ToolStripMenuItem(label, null, async (_, _) => await ChangeSortAsync(mode));
            _sortItems.Add((item, mode));
            items.Add(item);
        }
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("手動の並び順を削除(&R)", null, async (_, _) => await DeleteManualOrderAsync()));
    }

    private void UpdateSortChecks()
    {
        foreach (var (item, mode) in _sortItems) item.Checked = mode == _sortMode;
        _sortButton.Text = _sortMode switch
        {
            SortMode.Modified => "更新日時順",
            SortMode.Size => "サイズ順",
            SortMode.Manual => "手動の並び",
            _ => "名前順",
        };
        _toolbar.PerformLayout();
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

    // ---- 更新の確認（起動時に 1 回。新しければステータスバーとヘルプメニューに出すだけで、更新はユーザーが選んだときだけ） ----

    private static Version CurrentVersion => typeof(MainForm).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// 自分で入れ替えられる exe（リリース用の単一ファイル）のパス。
    /// 開発用のビルド（横に Glimpse.dll がある）では null（確認はするが入れ替えない）
    /// </summary>
    private static string? UpdatableExe =>
        Environment.ProcessPath is string exe && !File.Exists(Path.Combine(Path.GetDirectoryName(exe) ?? "", typeof(MainForm).Assembly.GetName().Name + ".dll")) ? exe : null;

    private void SetUpUpdateCheck()
    {
        if (UpdatableExe is not string exe) return; // 開発用のビルドでは起動時に確認しない（手動の確認はできる）
        SelfUpdate.CleanUp(exe); // 前回の更新で残った古い exe を消す
        if (_settings.CheckUpdatesOnStartup ?? true) Shown += async (_, _) => await CheckForUpdatesAsync(manual: false);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        _http ??= UpdateChecker.CreateClient(CurrentVersion);
        // 起動時は短めに打ち切る（つながらなければ何も出さない）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(manual ? 15 : 5));
        var latest = await UpdateChecker.GetLatestAsync(_http, cts.Token);
        if (latest == null)
        {
            if (manual)
                MessageBox.Show(this, "新しいバージョンを確認できませんでした。インターネットにつながっているか確かめてください。", "更新を確認",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!UpdateChecker.IsNewer(latest, CurrentVersion))
        {
            if (manual)
                MessageBox.Show(this, $"最新のバージョンです（v{UpdateChecker.Normalize(CurrentVersion)}）。", "更新を確認",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!manual && latest.Tag == _settings.SkippedVersion) return; // 「このバージョンは飛ばす」を選んだもの

        _available = latest;
        _footer.UpdateText = $"新しいバージョン {latest.Tag} があります（クリックで更新）";
        _updateItem.Text = $"{latest.Tag} に更新(&N)...";
        _updateItem.Visible = true;
        if (manual) ShowUpdateDialog();
    }

    private void ShowUpdateDialog()
    {
        if (_available is not { } release || _http == null) return;
        UpdateDialog.Outcome outcome;
        using (var dlg = new UpdateDialog(release, CurrentVersion, _http, UpdatableExe))
        {
            dlg.ShowDialog(this);
            outcome = dlg.Result;
        }
        switch (outcome)
        {
            case UpdateDialog.Outcome.Skip:
                ((ISettingsAccess)this).UpdateSettings(s => s with { SkippedVersion = release.Tag });
                _footer.UpdateText = "";
                _updateItem.Visible = false;
                Notify($"{release.Tag} は飛ばします（☰ → ヘルプ →「更新を確認」からいつでも更新できます）");
                break;
            case UpdateDialog.Outcome.Updated when UpdatableExe is string exe:
                // 新しい exe で、今のフォルダを開いた状態で起動し直す
                var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
                if (_folder != null) start.ArgumentList.Add(_folder);
                System.Diagnostics.Process.Start(start);
                Close();
                break;
        }
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

    /// <summary>コマンドを実行する対象（フッターで「チェック」を選んでいればチェックした画像、それ以外は選択中の画像）</summary>
    private IReadOnlyList<string> TargetPaths() =>
        _target == ActionTarget.Checked ? _grid.MarkedImages.Select(f => f.FullName).ToList() : SelectedPaths();

    private void UpdateCommandStates()
    {
        var paths = TargetPaths();
        UpdateCommandEnabled(paths);
        UpdateActionBar();

        // 場所はアドレスバーとタイトルに出ているので、ここは件数だけ（選択中の枚数は右側の情報に出る）
        if (!_noticeActive)
            _footer.Status = _folder == null ? "フォルダを開いてください（Ctrl+O / フォルダをドロップ）"
            : _grid.Folders.Count > 0 ? $"画像 {_grid.Items.Count} · フォルダ {_grid.Folders.Count}"
            : $"画像 {_grid.Items.Count}";
        _footer.CheckCount = _grid.MarkedCount;
    }

    /// <summary>
    /// メニューの有効 / 無効（無効のままだとショートカットも効かない）。貼り付けはクリップボード次第なので、
    /// メニューを開いたとき・ほかのアプリから戻ったときにも更新する
    /// </summary>
    private void UpdateCommandEnabled(IReadOnlyList<string>? paths = null)
    {
        paths ??= TargetPaths();
        foreach (var (item, cmd) in _commandItems)
            item.Enabled = cmd.CanExecute(paths);
        foreach (var (button, cmd) in _actionButtons)
            button.Enabled = cmd.CanExecute(paths);
        bool oneFolder = SelectedSingleFolder() != null;
        foreach (var item in _renameFolderItems) item.Enabled = oneFolder;
    }

    private async Task ExecuteAsync(IImageCommand cmd)
    {
        var paths = TargetPaths();
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

    /// <summary>
    /// フッターにお知らせを出す。少しの間は件数の表示で上書きしない（実行後の読み直しで、結果のお知らせがすぐ消えないように）
    /// </summary>
    public void Notify(string message)
    {
        _footer.Status = message;
        _noticeActive = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private bool _noticeActive;
    private readonly System.Windows.Forms.Timer _noticeTimer = new() { Interval = 6000 };

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
        bool reload = string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase);
        if (!reload) _noticeActive = false; // 別のフォルダへ移ったら、前のフォルダでのお知らせは消す
        if (!_noticeActive) _footer.Status = "読み込み中…";
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
        _addressBox.Path = folder;
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
