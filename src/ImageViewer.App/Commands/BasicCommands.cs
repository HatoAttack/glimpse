// 動作確認用の基本コマンド。リサイズ・切り抜き・連結も同じ形で追加していく
using System.Diagnostics;
using ImageViewer.Core.Commands;

namespace ImageViewer.App.Commands;

/// <summary>選択した画像のフルパスをクリップボードへ（複数可、改行区切り）</summary>
public sealed class CopyPathsCommand : ImageCommandBase
{
    public override string Id => "file.copyPaths";
    public override string Name => "パスをコピー";
    public override string Category => "ファイル";
    public override string? DefaultShortcut => "Ctrl+Shift+C";

    public override Task ExecuteAsync(CommandContext context)
    {
        Clipboard.SetText(string.Join(Environment.NewLine, context.Paths));
        context.Host.Notify($"{context.Paths.Count} 件のパスをコピーしました");
        return Task.CompletedTask;
    }
}

/// <summary>選択した画像をエクスプローラーで表示（1枚のみ）</summary>
public sealed class RevealInExplorerCommand : ImageCommandBase
{
    public override string Id => "file.revealInExplorer";
    public override string Name => "エクスプローラーで表示";
    public override string Category => "ファイル";
    public override string? DefaultShortcut => "Ctrl+E";
    protected override int MaxSelection => 1;

    public override Task ExecuteAsync(CommandContext context)
    {
        Process.Start("explorer.exe", $"/select,\"{context.Paths[0]}\"");
        return Task.CompletedTask;
    }
}
