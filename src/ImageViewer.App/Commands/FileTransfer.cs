// フォルダへの移動・コピー（ドラッグ＆ドロップ・「フォルダーへ移動 / コピー」で共通）
using ImageViewer.Core.Commands;
using ImageViewer.Core.Navigation;

namespace ImageViewer.App.Commands;

internal static class FileTransfer
{
    /// <summary>
    /// ドラッグ＆ドロップで落とせるか。自分自身・自分の中へ、今あるフォルダへの移動は受け付けない
    /// </summary>
    public static bool CanDrop(IReadOnlyList<string> sources, string folder, bool move) =>
        sources.Count > 0
        && !sources.Any(p => Directory.Exists(p) && FolderListing.IsSameOrInside(folder, p))
        && !(move && sources.All(p => FolderListing.IsDirectlyIn(p, folder)));

    /// <summary>
    /// ドロップの効果をエクスプローラーと同じ規則で決める: Ctrl = コピー、Shift = 移動、
    /// 何も押さなければ同じドライブなら移動・別のドライブならコピー。ドラッグ元が許していない効果は使わない
    /// </summary>
    public static DragDropEffects ChooseEffect(DragEventArgs e, IReadOnlyList<string> sources, string folder)
    {
        const int Shift = 4, Ctrl = 8;
        bool ctrl = (e.KeyState & Ctrl) != 0, shift = (e.KeyState & Shift) != 0;
        var effect = ctrl && !shift ? DragDropEffects.Copy
            : shift && !ctrl ? DragDropEffects.Move
            : sources.Count > 0 && FolderListing.SameVolume(sources[0], folder) ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((e.AllowedEffect & effect) == 0)
            effect = (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                : (e.AllowedEffect & DragDropEffects.Move) != 0 ? DragDropEffects.Move : DragDropEffects.None;
        return effect != DragDropEffects.None && CanDrop(sources, folder, effect == DragDropEffects.Move) ? effect : DragDropEffects.None;
    }

    /// <summary>ドラッグされているファイル・フォルダのパス（無ければ空）</summary>
    public static IReadOnlyList<string> DraggedPaths(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();

    /// <summary>
    /// 移動・コピーして画面に反映する。移動で今のフォルダから無くなったものは一覧から外し、
    /// 今のフォルダへ入れたものは読み直して選択する。実際に移動・コピーできた数を返す
    /// </summary>
    public static async Task<int> RunAsync(ICommandHost host, IReadOnlyList<string> sources, string folder, bool move, IntPtr owner)
    {
        if (!Directory.Exists(folder))
        {
            // 無いフォルダを指定すると、その名前のファイルとして移動・コピーされてしまう
            host.Notify($"フォルダが見つかりません: {folder}");
            return 0;
        }
        // 今あるフォルダへの移動は何もしない（エクスプローラーと同じ）。フォルダを自分自身・自分の中へは入れない
        if (move) sources = sources.Where(p => !FolderListing.IsDirectlyIn(p, folder)).ToList();
        sources = sources.Where(p => !(Directory.Exists(p) && FolderListing.IsSameOrInside(folder, p))).ToList();
        if (sources.Count == 0)
        {
            host.Notify("既にそのフォルダにあるか、フォルダをそれ自身の中へ入れようとしています");
            return 0;
        }

        bool renameOnCollision = !move && sources.Any(p => FolderListing.IsDirectlyIn(p, folder)); // 同じフォルダへのコピーは「- コピー」
        // 実際に増えた・置き換わったものは、前後のフォルダの中身（名前・更新日時・サイズ）を比べて調べる。
        // 同じ名前が前からあるだけでは成功と見なさない（上書きを断った・キャンセルしたとき）
        Dictionary<string, (DateTime, long)> before = new(), after = new();
        await ShellFileOps.RunInBackground(() =>
        {
            before = Snapshot(folder);
            if (move) ShellFileOps.Move(sources, folder, owner);
            else ShellFileOps.Copy(sources, folder, renameOnCollision, owner);
            after = Snapshot(folder);
        });
        var arrived = after.Where(kv => !before.TryGetValue(kv.Key, out var old) || old != kv.Value).Select(kv => kv.Key).ToList();

        // 移動は元の場所から無くなったものが成功。途中で失敗・キャンセルされた分は残っている
        var moved = move ? sources.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList() : new List<string>();
        int done = move ? moved.Count : Math.Min(arrived.Count, sources.Count);

        if (moved.Count > 0) host.FilesRemoved(moved);
        if (host.CurrentFolder is string current && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase))
            await host.FilesAddedAsync(folder, arrived);

        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        string verb = move ? "移動" : "コピー";
        host.Notify(done == sources.Count
            ? $"{done} 件を「{(name.Length > 0 ? name : folder)}」へ{verb}しました"
            : $"{done} / {sources.Count} 件を「{(name.Length > 0 ? name : folder)}」へ{verb}しました（残りはできませんでした）");
        return done;
    }

    /// <summary>フォルダ直下の項目と、その更新日時・サイズ（読めなければ空）</summary>
    private static Dictionary<string, (DateTime, long)> Snapshot(string folder)
    {
        var map = new Dictionary<string, (DateTime, long)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                map[info.FullName] = (info.LastWriteTimeUtc, info is FileInfo f ? f.Length : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return map;
    }
}

/// <summary>フォルダへドロップされた（Move = false ならコピー）</summary>
public sealed record FileDrop(IReadOnlyList<string> Paths, string Folder, bool Move);
