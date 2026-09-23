// 画像ビューア - メイン画面
// 現状は一覧（仮想モードの ListView）＋コマンドの仕組みまで。サムネイルグリッドは ThumbnailGrid で置き換え予定
using System.Runtime.InteropServices;
using ImageViewer.App.Commands;
using ImageViewer.Core.Commands;
using ImageViewer.Core.Imaging;

namespace ImageViewer.App;

public class MainForm : Form, ICommandHost
{
    public const string AppTitle = "画像ビューア";

    private readonly CommandRegistry _registry = new();
    private readonly ListView _list;
    private readonly ToolStripStatusLabel _status;
    private readonly ContextMenuStrip _contextMenu = new();
    // メニュー項目とコマンドの対応（選択が変わるたびに有効/無効を更新する）
    private readonly List<(ToolStripMenuItem Item, IImageCommand Command)> _commandItems = new();

    private List<FileInfo> _files = new();
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

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            VirtualMode = true,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            ContextMenuStrip = _contextMenu,
        };
        _list.Columns.Add("名前", 420);
        _list.Columns.Add("サイズ", 100, HorizontalAlignment.Right);
        _list.Columns.Add("更新日時", 160);
        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
        // 仮想モードでは Shift による範囲選択は VirtualItemsSelectionRangeChanged でしか通知されない
        _list.SelectedIndexChanged += (_, _) => UpdateCommandStates();
        _list.VirtualItemsSelectionRangeChanged += (_, _) => UpdateCommandStates();

        var statusStrip = new StatusStrip();
        _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        statusStrip.Items.Add(_status);

        // Dock の都合で Fill を先に追加する
        Controls.Add(_list);
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
            (_, _) => SelectAll()) { ShortcutKeys = Keys.Control | Keys.A });
        menu.Items.Add(editMenu);

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
        _list.SelectedIndices.Cast<int>().Order().Select(i => _files[i].FullName).ToList();

    private void UpdateCommandStates()
    {
        var paths = SelectedPaths();
        foreach (var (item, cmd) in _commandItems)
            item.Enabled = cmd.CanExecute(paths);

        string where = _folder ?? "フォルダ未選択（Ctrl+O で開く / フォルダをドロップ）";
        _status.Text = paths.Count > 0
            ? $"{where}   {_files.Count} 枚中 {paths.Count} 枚選択"
            : $"{where}   {_files.Count} 枚";
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
        List<FileInfo> files;
        try
        {
            // 大きなフォルダでも UI を止めないよう列挙は別スレッドで行う
            files = await Task.Run(() => ImageFormats.ListImages(folder, cts.Token), cts.Token);
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
        _files = files;
        _list.SelectedIndices.Clear();
        _list.VirtualListSize = files.Count;
        _list.Invalidate();
        Text = $"{Path.GetFileName(folder.TrimEnd('\\'))} - {AppTitle}";
        UpdateCommandStates();
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        var f = _files[e.ItemIndex];
        e.Item = new ListViewItem(new[]
        {
            f.Name,
            $"{Math.Max(1, (f.Length + 1023) / 1024):N0} KB",
            f.LastWriteTime.ToString("yyyy/MM/dd HH:mm"),
        });
    }

    private static string? DroppedFolder(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && Directory.Exists(paths[0])
            ? paths[0] : null;

    // ---- 全選択 ----
    // 仮想モードで Items[i].Selected を全件回すと遅いので、LVM_SETITEMSTATE で一括指定する

    private void SelectAll()
    {
        if (_files.Count == 0) return;
        var item = new LVITEM { stateMask = LVIS_SELECTED, state = LVIS_SELECTED };
        SendMessage(_list.Handle, LVM_SETITEMSTATE, -1, ref item);
        UpdateCommandStates();
    }

    private const int LVM_SETITEMSTATE = 0x1000 + 43;
    private const int LVIS_SELECTED = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LVITEM
    {
        public int mask, iItem, iSubItem, state, stateMask;
        public IntPtr pszText;
        public int cchTextMax, iImage;
        public IntPtr lParam;
        public int iIndent, iGroupId, cColumns;
        public IntPtr puColumns, piColFmt;
        public int iGroup;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, ref LVITEM lParam);
}
