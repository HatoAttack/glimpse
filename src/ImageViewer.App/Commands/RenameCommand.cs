// 名前の変更（連番・置換・小文字化）。選択中の画像を画面の並び順で処理する
using ImageViewer.App.Dialogs;
using ImageViewer.Core.Commands;
using ImageViewer.Core.Rename;

namespace ImageViewer.App.Commands;

public sealed class RenameCommand(Form owner) : ImageCommandBase
{
    public override string Id => "file.rename";
    public override string Name => "名前の変更（連番など）...";
    public override string Category => "ファイル";
    public override string? DefaultShortcut => "F2";

    public override async Task ExecuteAsync(CommandContext context)
    {
        IReadOnlyList<RenameOp> ops;
        using (var dialog = new RenameDialog(context.Paths))
        {
            if (dialog.ShowDialog(owner) != DialogResult.OK) return;
            ops = dialog.Operations;
        }
        if (ops.Count == 0) return;

        owner.UseWaitCursor = true;
        try
        {
            // 失敗したときは RenameExecutor が元の名前に戻してから例外を投げる（呼び出し側でメッセージ表示）
            var done = await Task.Run(() => RenameExecutor.Execute(ops));
            context.Host.FilesRenamed(done);
            context.Host.Notify($"{done.Count} 件の名前を変更しました（Ctrl+Z で元に戻せます）");
        }
        finally
        {
            owner.UseWaitCursor = false;
        }
    }
}
