// 削除（ごみ箱へ移動）。選択中の画像（画像以外のファイルも）をまとめて 1 回のシェル操作で送る（ごみ箱から元に戻せる）。確認は複数枚のときだけ
using ImageViewer.Core.Commands;

namespace ImageViewer.App.Commands;

public sealed class DeleteCommand(Form owner) : ImageCommandBase, IWorksOnAnyFile
{
    public override string Id => "file.delete";
    public override string Name => "削除（ごみ箱へ）";
    public override string Category => "ファイル";
    public override string? DefaultShortcut => "Del";

    public override Task ExecuteAsync(CommandContext context)
    {
        var paths = context.Paths;
        // 1 枚はすぐにごみ箱へ（ごみ箱から戻せる）。まとめて消すときだけ確認する
        if (paths.Count > 1 && MessageBox.Show(owner, $"選択中の {FileWording.CountWithNoun(paths)}をごみ箱に移動しますか？", "削除",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return Task.CompletedTask;

        ShellFileOps.Recycle(paths, owner.Handle);
        // 途中で失敗・キャンセルされた分は残っているので、実際に無くなったものだけを反映する
        var deleted = paths.Where(p => !File.Exists(p)).ToList();
        if (deleted.Count > 0) context.Host.FilesRemoved(deleted);
        context.Host.Notify(deleted.Count == paths.Count
            ? $"{FileWording.Count(paths)}をごみ箱に移動しました"
            : $"{deleted.Count} / {FileWording.Count(paths)}をごみ箱に移動しました（残りは削除できませんでした）");
        return Task.CompletedTask;
    }
}
