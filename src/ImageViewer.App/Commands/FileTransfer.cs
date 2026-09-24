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
        await ShellFileOps.RunInBackground(() =>
        {
            if (move) ShellFileOps.Move(sources, folder, owner);
            else ShellFileOps.Copy(sources, folder, renameOnCollision, owner);
        });

        // 途中で失敗・キャンセルされた分は残っているので、実際の結果をファイルの有無で確かめる
        var done = sources.Where(p => move
            ? !File.Exists(p) && !Directory.Exists(p)
            : FolderListing.IsDirectlyIn(p, folder) || Exists(Path.Combine(folder, Path.GetFileName(p)))).ToList();

        if (move && done.Count > 0) host.FilesRemoved(done);
        if (host.CurrentFolder is string current && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase))
            await host.FilesAddedAsync(folder, done.Select(p => Path.Combine(folder, Path.GetFileName(p))).ToList());

        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        string verb = move ? "移動" : "コピー";
        host.Notify(done.Count == sources.Count
            ? $"{done.Count} 件を「{(name.Length > 0 ? name : folder)}」へ{verb}しました"
            : $"{done.Count} / {sources.Count} 件を「{(name.Length > 0 ? name : folder)}」へ{verb}しました（残りはできませんでした）");
        return done.Count;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}

/// <summary>フォルダへドロップされた（Move = false ならコピー）</summary>
public sealed record FileDrop(IReadOnlyList<string> Paths, string Folder, bool Move);
