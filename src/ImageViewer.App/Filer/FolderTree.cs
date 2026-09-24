// 左側のフォルダツリー
// - 子フォルダは、そのノードを開いたときに初めて（別スレッドで）読む。起動時に全部は読まないので軽い
// - ルートは よく使うフォルダ（デスクトップ・ピクチャ等）＋ ローカルのドライブ。
//   ネットワークドライブは応答待ちで固まる原因になるので出さない（アドレスバーからは開ける）
using System.Runtime.InteropServices;
using ImageViewer.App.Commands;
using ImageViewer.App.Theming;
using ImageViewer.Core.Navigation;

namespace ImageViewer.App.Filer;

public sealed class FolderTree : TreeView
{
    private static readonly object LoadingTag = new();
    private readonly System.Windows.Forms.Timer _keyboardDelay = new() { Interval = 350 };
    private bool _suppressSelect;
    private int _revealVersion;
    private TreeNode? _homeNode;

    /// <summary>ユーザーがツリーでフォルダを選んだ（マウスは即時、キー操作は少し待ってから）</summary>
    public event EventHandler<string>? FolderSelected;

    /// <summary>フォルダに画像・ファイルがドロップされた（移動 / コピーは本体が行う）</summary>
    public event EventHandler<FileDrop>? FilesDroppedOnFolder;

    public FolderTree()
    {
        HideSelection = false;
        AllowDrop = true;
        // 開閉の印を一番上の階層にも出し、子フォルダは字下げして階層を見やすくする
        ShowRootLines = true;
        ShowPlusMinus = true;
        // 線は引かず、行全体を選択の色にする（エクスプローラーと同じ見た目）
        ShowLines = false;
        FullRowSelect = true;
        Indent = LogicalToDeviceUnits(18);
        BorderStyle = BorderStyle.None;
        ItemHeight = LogicalToDeviceUnits(26);
        BackColor = Theme.Current.Background;
        ForeColor = Theme.Current.Text;
        _keyboardDelay.Tick += (_, _) =>
        {
            _keyboardDelay.Stop();
            if (SelectedNode?.Tag is string path) FolderSelected?.Invoke(this, path);
        };
        BuildRoots();
    }

    /// <summary>今の配色に（開閉の印・スクロールバー・選択の色は Windows のダーク用 / 通常の見た目に切り替える）</summary>
    public void ApplyTheme()
    {
        BackColor = Theme.Current.Background;
        ForeColor = Theme.Current.Text;
        Theme.ApplyNativeTheme(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyNativeTheme(this);
    }

    private void BuildRoots()
    {
        BeginUpdate();
        foreach (var (label, path) in QuickFolders())
            if (Directory.Exists(path)) Nodes.Add(MakeNode(label, path));

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom)) continue;
            string letter = drive.Name.TrimEnd('\\');
            string label;
            try
            {
                if (!drive.IsReady) continue;
                label = string.IsNullOrEmpty(drive.VolumeLabel)
                    ? (drive.DriveType == DriveType.Fixed ? "ローカル ディスク" : "リムーバブル ディスク")
                    : drive.VolumeLabel;
            }
            catch (IOException)
            {
                continue;
            }
            Nodes.Add(MakeNode($"{label} ({letter})", drive.RootDirectory.FullName));
        }
        EndUpdate();
    }

    private static IEnumerable<(string Label, string Path)> QuickFolders()
    {
        yield return ("デスクトップ", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        yield return ("ピクチャ", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        yield return ("ダウンロード", DownloadsFolder());
        yield return ("ドキュメント", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    /// <summary>先頭に「ホーム」を出す（変更されたら差し替える）。今のフォルダがホーム配下ならホーム側から選択される</summary>
    public void SetHome(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var node = MakeNode($"ホーム（{(name.Length > 0 ? name : path)}）", path);
        BeginUpdate();
        if (_homeNode != null) Nodes.Remove(_homeNode);
        Nodes.Insert(0, node);
        _homeNode = node;
        EndUpdate();
    }

    /// <summary>ダウンロードフォルダ（場所を変えている人もいるので既知フォルダの API で取る）</summary>
    public static string DownloadsFolder()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref downloads, 0, IntPtr.Zero, out var p) == 0)
        {
            try { return Marshal.PtrToStringUni(p)!; }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    private static TreeNode MakeNode(string text, string path)
    {
        var node = new TreeNode(text) { Tag = path };
        // 子があるかは開くまで調べない（調べるだけで各フォルダの中身を読むことになるため）
        node.Nodes.Add(new TreeNode("読み込み中…") { Tag = LoadingTag, ForeColor = SystemColors.GrayText });
        return node;
    }

    private static bool IsUnloaded(TreeNode node) => node.Nodes.Count == 1 && node.Nodes[0].Tag == LoadingTag;

    protected override void OnBeforeExpand(TreeViewCancelEventArgs e)
    {
        base.OnBeforeExpand(e);
        if (e.Node != null && IsUnloaded(e.Node)) _ = LoadChildrenAsync(e.Node);
    }

    private async Task LoadChildrenAsync(TreeNode node)
    {
        if (!IsUnloaded(node) || node.Tag is not string path) return;
        node.Nodes[0].Tag = null; // 二重に読みに行かないよう、読み込み中の印を外しておく
        var dirs = await Task.Run(() => FolderListing.ListSubfolders(path));
        if (node.TreeView == null) return;
        BeginUpdate();
        node.Nodes.Clear();
        foreach (var d in dirs) node.Nodes.Add(MakeNode(d.Name, d.FullName));
        EndUpdate();
    }

    protected override void OnAfterSelect(TreeViewEventArgs e)
    {
        base.OnAfterSelect(e);
        if (_suppressSelect || e.Node?.Tag is not string path) return;
        switch (e.Action)
        {
            case TreeViewAction.ByMouse:
                FolderSelected?.Invoke(this, path);
                break;
            case TreeViewAction.ByKeyboard:
                // 矢印キーで次々に移るたびに開くと重いので、止まってから開く
                _keyboardDelay.Stop();
                _keyboardDelay.Start();
                break;
            // それ以外（選択中のノードが消えたときに TreeView が勝手に選び直す等）では移動しない
        }
    }

    /// <summary>
    /// 今のフォルダをツリーで選択状態にする（途中の階層は必要な分だけ読む）。
    /// ピクチャ等の配下なら、ドライブからたどるより短い「よく使うフォルダ」側を使う
    /// </summary>
    public async Task RevealAsync(string folder)
    {
        int version = ++_revealVersion;
        string target = Path.TrimEndingDirectorySeparator(folder);
        TreeNode? root = null;
        string rootPath = "";
        foreach (TreeNode n in Nodes)
        {
            if (n.Tag is not string p) continue;
            // ドライブのルート（C:\）は末尾の \ が消えないので、区切り付きで比べる
            string rp = Path.TrimEndingDirectorySeparator(p);
            string prefix = rp.EndsWith(Path.DirectorySeparatorChar) ? rp : rp + Path.DirectorySeparatorChar;
            bool match = string.Equals(target, rp, StringComparison.OrdinalIgnoreCase)
                         || target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            if (match && rp.Length > rootPath.Length)
            {
                root = n;
                rootPath = rp;
            }
        }
        if (root == null)
        {
            SelectSilently(null);
            return;
        }

        var node = root;
        var rest = target.Length > rootPath.Length
            ? target[rootPath.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        foreach (var name in rest)
        {
            if (IsUnloaded(node)) await LoadChildrenAsync(node);
            if (version != _revealVersion) return; // その間に別のフォルダへ移動した
            var child = node.Nodes.Cast<TreeNode>().FirstOrDefault(c =>
                c.Tag is string cp && string.Equals(Path.GetFileName(cp), name, StringComparison.OrdinalIgnoreCase));
            if (child == null && node.Tag is string parentPath)
            {
                // 隠しフォルダ（AppData 等）はツリーに出していないが、その中へ移動したときは
                // 経路をたどれるよう、そのフォルダだけを薄い色で差し込む
                string hiddenPath = Path.Combine(parentPath, name);
                if (!Directory.Exists(hiddenPath)) break;
                child = MakeNode(new DirectoryInfo(hiddenPath).Name, hiddenPath);
                child.ForeColor = SystemColors.GrayText;
                node.Nodes.Add(child);
            }
            if (child == null) break;
            node.Expand();
            node = child;
        }
        SelectSilently(node);
    }

    private void SelectSilently(TreeNode? node)
    {
        _suppressSelect = true;
        try
        {
            SelectedNode = node;
            node?.EnsureVisible();
        }
        finally
        {
            _suppressSelect = false;
        }
    }

    /// <summary>
    /// フォルダー名を変えた。読み込み済みのノードのパスを付け替える（そのフォルダ自身は表示名も変える。
    /// よく使うフォルダ・ドライブ・ホームの見出しはそのまま）
    /// </summary>
    public void FolderRenamed(string oldPath, string newPath)
    {
        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                if (node.Tag is string path && FolderListing.Retarget(path, oldPath, newPath) is string moved)
                {
                    // そのフォルダ自身（見出しの無い普通のノード）は表示名も変える
                    if (node.Parent != null && string.Equals(moved, newPath, StringComparison.OrdinalIgnoreCase))
                        node.Text = Path.GetFileName(newPath);
                    node.Tag = moved;
                }
                Walk(node.Nodes);
            }
        }
        BeginUpdate();
        Walk(Nodes);
        EndUpdate();
    }

    // ---- ドロップ（フォルダへ移動 / コピー） ----
    // ドロップ先の強調は選択とは別の「ドロップ先」の表示を使う（選択を変えるとそのフォルダへ移動してしまうため）

    private TreeNode? _dropNode;
    private TreeNode? _hoverNode;
    private readonly System.Diagnostics.Stopwatch _hoverTime = new();

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        OnDragOver(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        var client = PointToClient(new Point(e.X, e.Y));
        var node = GetNodeAt(client);

        // 上下の端では少しずつスクロール、同じフォルダの上で止まっていたら開く（エクスプローラーと同じ）
        int edge = ItemHeight;
        if (client.Y < edge) TopNode?.PrevVisibleNode?.EnsureVisible();
        else if (client.Y > ClientSize.Height - edge) VisibleBottomNode()?.NextVisibleNode?.EnsureVisible();
        if (node != _hoverNode)
        {
            _hoverNode = node;
            _hoverTime.Restart();
        }
        else if (node is { IsExpanded: false } && _hoverTime.ElapsedMilliseconds > 800)
        {
            node.Expand();
        }

        e.Effect = node?.Tag is string path && Directory.Exists(path)
            ? FileTransfer.ChooseEffect(e, FileTransfer.DraggedPaths(e), path)
            : DragDropEffects.None;
        SetDropNode(e.Effect != DragDropEffects.None ? node : null);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _hoverNode = null;
        SetDropNode(null);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        var node = _dropNode;
        _hoverNode = null;
        SetDropNode(null);
        if (node?.Tag is not string path) return;
        var paths = FileTransfer.DraggedPaths(e);
        var effect = FileTransfer.ChooseEffect(e, paths, path);
        if (effect != DragDropEffects.None)
        {
            // ドラッグ元（エクスプローラー等）を待たせないよう、実際の移動 / コピーはドロップを終えてから
            var drop = new FileDrop(paths.ToList(), path, effect == DragDropEffects.Move);
            BeginInvoke(() => FilesDroppedOnFolder?.Invoke(this, drop));
        }
        // 移動はこちらで行うので、ドラッグ元には「移動した」を返さない（返すとドラッグ元が元のファイルを消すことがある）
        e.Effect = effect == DragDropEffects.Move ? DragDropEffects.None : effect;
    }

    private TreeNode? VisibleBottomNode()
    {
        var node = TopNode;
        for (int i = 1; node != null && i < VisibleCount; i++) node = node.NextVisibleNode;
        return node;
    }

    private void SetDropNode(TreeNode? node)
    {
        if (node == _dropNode) return;
        _dropNode = node;
        if (IsHandleCreated) SendMessage(Handle, TVM_SELECTITEM, TVGN_DROPHILITE, node?.Handle ?? IntPtr.Zero);
    }

    private const int TVM_SELECTITEM = 0x110B;
    private static readonly IntPtr TVGN_DROPHILITE = 8;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _keyboardDelay.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
