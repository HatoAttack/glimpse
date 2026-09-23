// 画像ビューア - メイン画面
using ImageViewer.App.Commands;
using ImageViewer.App.Grid;
using ImageViewer.Core.Commands;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Ordering;
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

    private string? _folder;
    private CancellationTokenSource? _loadCts;

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
        FormClosed += (_, _) => _thumbnails.Dispose();

        var statusStrip = new StatusStrip();
        _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        statusStrip.Items.Add(_status);

        // Dock の都合で Fill を先に追加する
        Controls.Add(_grid);
        Controls.Add(BuildMainMenu());
        Controls.Add(statusStrip);
        BuildContextMenu();

        DragEnter += (_, e) =>
            e.Effect = DroppedFolder(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += async (_, e) =>
        {
            if (DroppedFolder(e) is string f) await LoadFolderAsync(f);
        };

        UpdateCommandStates();
        if (initialFolder != null) Shown += async (_, _) => await LoadFolderAsync(initialFolder);
    }

    private void RegisterCommands()
    {
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
        editMenu.DropDownItems.Add(new ToolStripMenuItem("すべて選択(&A)", null,
            (_, _) => _grid.SelectAll()) { ShortcutKeys = Keys.Control | Keys.A });
        menu.Items.Add(editMenu);
        menu.Items.Add(BuildMarkMenu());
        menu.Items.Add(BuildViewMenu());

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

    private IReadOnlyList<string> SelectedPaths() =>
        _grid.SelectedIndices.Select(i => _grid.Items[i].FullName).ToList();

    private void UpdateCommandStates()
    {
        var paths = SelectedPaths();
        foreach (var (item, cmd) in _commandItems)
            item.Enabled = cmd.CanExecute(paths);

        string where = _folder ?? "フォルダ未選択（Ctrl+O で開く / フォルダをドロップ）";
        string text = $"{where}   {_grid.Items.Count} 枚";
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

    private async Task LoadFolderAsync(string folder)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        _status.Text = $"{folder} を読み込み中...";
        bool reload = string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase);
        List<FileInfo> files;
        SortMode mode;
        try
        {
            // 大きなフォルダでも UI を止めないよう列挙は別スレッドで行う。
            // 別のフォルダを開いたときは、手動の並び順が保存されていれば手動、無ければ名前順で始める
            (files, mode) = await Task.Run(() =>
            {
                var listed = ImageFormats.ListImages(folder, cts.Token);
                var saved = _orderStore.Load(folder);
                var m = reload ? _sortMode : saved != null ? SortMode.Manual : SortMode.Name;
                return (Arrange(listed, m, saved), m);
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

        _folder = folder;
        _sortMode = mode;
        UpdateSortChecks();
        _grid.SetItems(files, reload);
        _grid.Focus();
        Text = $"{Path.GetFileName(folder.TrimEnd('\\'))} - {AppTitle}";
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
