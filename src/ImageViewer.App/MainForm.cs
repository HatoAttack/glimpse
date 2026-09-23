// 画像ビューア - メイン画面
using ImageViewer.App.Commands;
using ImageViewer.App.Filer;
using ImageViewer.App.Grid;
using ImageViewer.Core.Commands;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Navigation;
using ImageViewer.Core.Ordering;
using ImageViewer.Core.Rename;
using ImageViewer.Core.Thumbnails;

namespace ImageViewer.App;

public class MainForm : Form, ICommandHost
{
    public const string AppTitle = "画像ビューア";

    private readonly CommandRegistry _registry = new();
    private readonly ThumbnailService _thumbnails;
    private readonly ThumbnailGrid _grid;
    private readonly ToolStripStatusLabel _status;
    private readonly ContextMenuStrip _contextMenu = new();
    // メニュー項目とコマンドの対応（選択が変わるたびに有効/無効を更新する）
    private readonly List<(ToolStripMenuItem Item, IImageCommand Command)> _commandItems = new();

    private readonly FolderOrderStore _orderStore = FolderOrderStore.CreateDefault();
    private readonly List<(ToolStripMenuItem Item, SortMode Mode)> _sortItems = new();
    private SortMode _sortMode = SortMode.Name;

    // 直前の名前の変更（元に戻す用）。変更前の手動の並び順と並び順の種類も一緒に覚えておく
    private (IReadOnlyList<RenameOp> Ops, IReadOnlyList<string>? SavedOrder, SortMode Mode)? _lastRename;
    private ToolStripMenuItem _undoItem = null!;

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
    private readonly ToolTip _toolTip = new();
    private ToolStripMenuItem _backItem = null!, _forwardItem = null!, _upItem = null!;

    /// <summary>移動の種類（履歴の扱いが変わる）</summary>
    private enum NavKind { New, Back, Forward, Reload }

    public MainForm(string? initialFolder = null)
    {
        Text = AppTitle;
        Font = new Font("Yu Gothic UI", 9F);
        ClientSize = new Size(980, 700);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        RegisterCommands();

        // サムネイルは長辺 160（DPI 反映）で生成し、メモリ上には最大 128MB（この大きさで約 300〜1000 枚）まで持つ
        _thumbnails = new ThumbnailService(LogicalToDeviceUnits(160), 128L * 1024 * 1024,
            SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
        _grid = new ThumbnailGrid(_thumbnails) { Dock = DockStyle.Fill, ContextMenuStrip = _contextMenu };
        _grid.SelectionChanged += (_, _) => UpdateCommandStates();
        _grid.MarksChanged += (_, _) => UpdateCommandStates();
        _grid.OrderChanged += (_, _) => SaveManualOrder();
        _grid.FolderDropped += async (_, folder) => await LoadFolderAsync(folder);
        _grid.FolderActivated += async (_, dir) => await LoadFolderAsync(dir.FullName);
        _grid.MouseDown += OnMouseBackForward;
        _tree.MouseDown += OnMouseBackForward;
        _tree.FolderSelected += async (_, path) => await LoadFolderAsync(path);
        FormClosed += (_, _) => _thumbnails.Dispose();

        var statusStrip = new StatusStrip();
        _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        statusStrip.Items.Add(_status);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = LogicalToDeviceUnits(4),
        };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_grid);

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
        // 起動時は指定のフォルダ、無ければピクチャを開く
        string? start = initialFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) Shown += async (_, _) => await LoadFolderAsync(start);
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

        _address.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await NavigateFromAddressAsync();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
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

    private async Task NavigateFromAddressAsync()
    {
        if (FolderListing.Normalize(_address.Text) is string folder)
        {
            await LoadFolderAsync(folder);
            _grid.Focus();
        }
        else
        {
            System.Media.SystemSounds.Beep.Play();
            Notify($"フォルダが見つかりません: {_address.Text.Trim()}");
            _address.SelectAll();
        }
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
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RegisterCommands()
    {
        _registry.Register(new RenameCommand(this));
        _registry.Register(new CopyPathsCommand());
        _registry.Register(new RevealInExplorerCommand());
        // TODO: リサイズ・切り抜き・連結をここに追加

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
            _backItem, _forwardItem, _upItem, new ToolStripSeparator(),
            new ToolStripMenuItem("アドレスバーに入力(&A)", null, (_, _) => _address.Focus()) { ShortcutKeys = Keys.Control | Keys.L },
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
            new ToolStripMenuItem("チェックを付ける / 外す(&T)", null, (_, _) => _grid.ToggleFocusedMark())
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
        foreach (var (item, cmd) in _commandItems)
            item.Enabled = cmd.CanExecute(paths);

        string where = _folder ?? "フォルダ未選択（Ctrl+O で開く / フォルダをドロップ）";
        string text = _grid.Folders.Count > 0
            ? $"{where}   フォルダ {_grid.Folders.Count}   画像 {_grid.Items.Count} 枚"
            : $"{where}   画像 {_grid.Items.Count} 枚";
        if (paths.Count > 0) text += $"   選択 {paths.Count} 枚";
        if (_grid.MarkedCount > 0) text += $"   チェック {_grid.MarkedCount} 枚";
        _status.Text = text;
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
    }

    public void Notify(string message) => _status.Text = message;

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

        if (!reload)
        {
            _lastRename = null;
            _undoItem.Enabled = false;
            _undoItem.Text = "元に戻す(&U)";
        }
        _folder = folder;
        _sortMode = mode;
        if (kind == NavKind.New) _history.Navigate(folder);
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
    private void SaveManualOrder()
    {
        if (_folder == null) return;
        _sortMode = SortMode.Manual;
        UpdateSortChecks();
        try
        {
            _orderStore.Save(_folder, _grid.Items.Select(f => f.Name).ToList());
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
