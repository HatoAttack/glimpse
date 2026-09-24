// フォルダーへ移動 / フォルダーへコピー。移動先はフォルダジャンプと同じ検索で選ぶ
using ImageViewer.App.Dialogs;
using ImageViewer.Core.Commands;

namespace ImageViewer.App.Commands;

public abstract class TransferToFolderCommand(Form owner, ISettingsAccess settings, FolderSearch search, bool move) : ImageCommandBase
{
    public override string Category => "ファイル";

    public override async Task ExecuteAsync(CommandContext context)
    {
        string verb = move ? "移動" : "コピー";
        string what = context.Paths.Count == 1 ? $"「{Path.GetFileName(context.Paths[0])}」" : $"{context.Paths.Count} 枚の画像";
        string folder;
        using (var dialog = new FolderPickDialog($"フォルダーへ{verb}", $"{what}の{verb}先（フォルダー名の一部かパスを入力、↑↓ で選んで Enter）",
                   settings.Settings.RecentDestinations ?? Array.Empty<string>(), search))
        {
            if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.SelectedFolder is not string picked) return;
            folder = picked;
        }
        settings.UpdateSettings(s => s.WithRecentDestination(folder));
        await FileTransfer.RunAsync(context.Host, context.Paths, folder, move, owner.Handle);
    }
}

public sealed class MoveToFolderCommand(Form owner, ISettingsAccess settings, FolderSearch search)
    : TransferToFolderCommand(owner, settings, search, move: true)
{
    public override string Id => "file.moveTo";
    public override string Name => "フォルダーへ移動...";
    public override string? DefaultShortcut => "Ctrl+Shift+M";
}

public sealed class CopyToFolderCommand(Form owner, ISettingsAccess settings, FolderSearch search)
    : TransferToFolderCommand(owner, settings, search, move: false)
{
    public override string Id => "file.copyTo";
    public override string Name => "フォルダーへコピー...";
    public override string? DefaultShortcut => "Ctrl+Shift+D";
}
