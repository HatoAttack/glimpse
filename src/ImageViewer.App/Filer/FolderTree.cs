// 左側のフォルダツリー
// - 子フォルダは、そのノードを開いたときに初めて（別スレッドで）読む。起動時に全部は読まないので軽い
// - ルートは よく使うフォルダ（デスクトップ・ピクチャ等）＋ ローカルのドライブ。
//   ネットワークドライブは応答待ちで固まる原因になるので出さない（アドレスバーからは開ける）
using System.Runtime.InteropServices;
using ImageViewer.Core.Navigation;

namespace ImageViewer.App.Filer;

public sealed class FolderTree : TreeView
{
    private static readonly object LoadingTag = new();
    private readonly System.Windows.Forms.Timer _keyboardDelay = new() { Interval = 350 };
    private bool _suppressSelect;
    private int _revealVersion;

    /// <summary>ユーザーがツリーでフォルダを選んだ（マウスは即時、キー操作は少し待ってから）</summary>
    public event EventHandler<string>? FolderSelected;

    public FolderTree()
    {
        HideSelection = false;
        ShowRootLines = false;
        FullRowSelect = true;
        ShowLines = false;
        BorderStyle = BorderStyle.None;
        _keyboardDelay.Tick += (_, _) =>
        {
            _keyboardDelay.Stop();
            if (SelectedNode?.Tag is string path) FolderSelected?.Invoke(this, path);
        };
        BuildRoots();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _keyboardDelay.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
