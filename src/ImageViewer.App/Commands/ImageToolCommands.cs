// リサイズ・形式変換 / 切り抜き / 連結（image-sizechange から移植）。選択中の画像を画面の並び順で処理する
using ImageViewer.App.Dialogs;
using ImageViewer.Core.Commands;
using ImageViewer.Core.Editing;
using ImageViewer.Core.Settings;

namespace ImageViewer.App.Commands;

/// <summary>コマンドが前回の設定を読み書きするための窓口（MainForm が持つ設定を更新して保存する）</summary>
public interface ISettingsAccess
{
    AppSettings Settings { get; }
    void UpdateSettings(Func<AppSettings, AppSettings> change);
}

public sealed class ResizeCommand(Form owner, ISettingsAccess settings) : ImageCommandBase
{
    public override string Id => "image.resize";
    public override string Name => "リサイズ・形式変換...";
    public override string? DefaultShortcut => "Ctrl+R";

    public override Task ExecuteAsync(CommandContext context)
    {
        using var dialog = new ResizeDialog(context.Paths, settings.Settings.Resize ?? new ConvertOptions());
        dialog.ShowDialog(owner);
        if (dialog.UsedOptions is { } used) settings.UpdateSettings(s => s with { Resize = used });
        if (dialog.Result is { } result)
        {
            if (result.Converted > 0) context.Host.RequestRefresh();
            context.Host.Notify(ResizeDialog.Summarize(result));
        }
        return Task.CompletedTask;
    }
}

public sealed class CropCommand(Form owner) : ImageCommandBase
{
    public override string Id => "image.crop";
    public override string Name => "切り抜き...";
    public override string? DefaultShortcut => "Ctrl+K";

    public override Task ExecuteAsync(CommandContext context)
    {
        using var dialog = new CropDialog(context.Paths);
        dialog.ShowDialog(owner);
        if (dialog.SavedCount > 0)
        {
            context.Host.RequestRefresh();
            context.Host.Notify($"{dialog.SavedCount} 枚を切り抜いて保存しました");
        }
        return Task.CompletedTask;
    }
}

public sealed class CombineCommand(Form owner, ISettingsAccess settings) : ImageCommandBase
{
    public override string Id => "image.combine";
    public override string Name => "連結...";
    public override string? DefaultShortcut => "Ctrl+M";
    protected override int MinSelection => 2;

    public override Task ExecuteAsync(CommandContext context)
    {
        using var dialog = new CombineDialog(context.Paths, settings.Settings.Combine ?? new CombineOptions());
        dialog.ShowDialog(owner);
        if (dialog.UsedOptions is { } used) settings.UpdateSettings(s => s with { Combine = used });
        if (dialog.SavedPath is { } saved)
        {
            context.Host.RequestRefresh();
            context.Host.Notify($"連結して保存しました: {Path.GetFileName(saved)}（{dialog.SavedSize.Width} × {dialog.SavedSize.Height}）");
        }
        return Task.CompletedTask;
    }
}
