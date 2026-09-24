// 削除（ごみ箱へ移動）。選択中の画像をまとめて 1 回のシェル操作で送る（ごみ箱から元に戻せる）
using ImageViewer.Core.Commands;

namespace ImageViewer.App.Commands;

public sealed class DeleteCommand(Form owner) : ImageCommandBase
{
    public override string Id => "file.delete";
    public override string Name => "削除（ごみ箱へ）";
    public override string Category => "ファイル";
    public override string? DefaultShortcut => "Del";

    public override Task ExecuteAsync(CommandContext context)
    {
        var paths = context.Paths;
        string question = paths.Count == 1
            ? $"「{Path.GetFileName(paths[0])}」をごみ箱に移動しますか？"
            : $"選択中の {paths.Count} 枚の画像をごみ箱に移動しますか？";
        if (MessageBox.Show(owner, question, "削除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return Task.CompletedTask;

        ShellFileOps.Recycle(paths, owner.Handle);
        // 途中で失敗・キャンセルされた分は残っているので、実際に無くなったものだけを反映する
        var deleted = paths.Where(p => !File.Exists(p)).ToList();
        if (deleted.Count > 0) context.Host.FilesDeleted(deleted);
        context.Host.Notify(deleted.Count == paths.Count
            ? $"{deleted.Count} 枚をごみ箱に移動しました"
            : $"{deleted.Count} / {paths.Count} 枚をごみ箱に移動しました（残りは削除できませんでした）");
        return Task.CompletedTask;
    }
}
