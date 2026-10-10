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

/// <summary>
/// 前回の設定のままリサイズ・形式変換（設定画面を出さない）。新しいファイルを作るだけのときに限り、
/// 元の画像の置き換え・上書き・名前の問題があるときや前回の設定が無いときは、理由を出して設定画面を開く
/// </summary>
public sealed class QuickResizeCommand(Form owner, ISettingsAccess settings, ResizeCommand dialog) : ImageCommandBase
{
    public override string Id => "image.resizeQuick";
    public override string Name => "前回の設定でリサイズ";
    public override string? DefaultShortcut => "Ctrl+Shift+R";

    public override async Task ExecuteAsync(CommandContext context)
    {
        if (settings.Settings.Resize is not { } options)
        {
            context.Host.Notify("前回の設定がまだないので、設定画面を開きます");
            await dialog.ExecuteAsync(context);
            return;
        }
        var plan = Converter.Plan(context.Paths, options);
        if (Converter.QuickRunBlocker(plan, options) is string reason)
        {
            context.Host.Notify($"{reason}。設定画面で確かめてください");
            await dialog.ExecuteAsync(context);
            return;
        }

        var progress = new Progress<ConvertProgress>(p =>
        {
            if (p.Done < p.Total) context.Host.Notify($"リサイズ中 {p.Done + 1} / {p.Total}: {p.Name}");
        });
        // 新しいファイルを作るだけ（確かめた後に保存先ができても上書きしない）
        var result = await Task.Run(() => Converter.Run(plan, options, progress, createOnly: true));
        if (result.Converted > 0) context.Host.RequestRefresh();
        context.Host.Notify($"{ResizeDialog.Summarize(result)}（{Converter.Describe(options)}・{Converter.DescribeOutput(options)}）");
        if (result.Errors.Count > 0)
            MessageBox.Show(owner, string.Join("\n", result.Errors.Take(15)) + (result.Errors.Count > 15 ? $"\n…ほか {result.Errors.Count - 15} 件" : ""),
                $"変換できなかった画像（{result.Errors.Count} 枚）", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

/// <summary>選んだ範囲にモザイク・ぼかしをかける（範囲は画像ごとに選ぶか、全部の画像に同じ範囲でかける）</summary>
public sealed class MaskCommand(Form owner) : ImageCommandBase
{
    public override string Id => "image.mask";
    public override string Name => "モザイク・ぼかし...";
    public override string? DefaultShortcut => "Ctrl+B";

    public override Task ExecuteAsync(CommandContext context)
    {
        using var dialog = new MaskDialog(context.Paths);
        dialog.ShowDialog(owner);
        if (dialog.SavedCount > 0)
        {
            context.Host.RequestRefresh();
            context.Host.Notify($"{dialog.SavedCount} 枚にモザイク・ぼかしをかけて保存しました");
        }
        return Task.CompletedTask;
    }
}

/// <summary>画像の特定の所を指し示すために、四角の枠・矢印・文字（吹き出し）・番号を描く</summary>
public sealed class AnnotateCommand(Form owner) : ImageCommandBase
{
    public override string Id => "image.annotate";
    public override string Name => "枠・矢印・文字...";
    public override string? DefaultShortcut => "Ctrl+D";

    public override Task ExecuteAsync(CommandContext context)
    {
        using var dialog = new AnnotateDialog(context.Paths);
        dialog.ShowDialog(owner);
        if (dialog.SavedCount > 0)
        {
            context.Host.RequestRefresh();
            context.Host.Notify($"{dialog.SavedCount} 枚に枠・矢印・文字を描いて保存しました");
        }
        return Task.CompletedTask;
    }
}

/// <summary>選んだ画像にまとめて色調補正をかける。値は 1 枚表示の補正と「前回の補正」を共有する</summary>
public sealed class AdjustCommand(Form owner, ISettingsAccess settings) : ImageCommandBase
{
    public override string Id => "image.adjust";
    public override string Name => "補正...";
    public override string? DefaultShortcut => "Ctrl+Shift+E";

    public override Task ExecuteAsync(CommandContext context)
    {
        using var dialog = new AdjustBatchDialog(context.Paths, settings.Settings.LastAdjust?.Normalize(), settings.Settings.AdjustBatch ?? new AdjustBatchOptions());
        dialog.ShowDialog(owner);
        if (dialog.UsedOptions is { } used)
        {
            var adjust = dialog.UsedAdjust;
            settings.UpdateSettings(s => s with
            {
                AdjustBatch = used,
                LastAdjust = adjust is { IsIdentity: false } ? adjust : s.LastAdjust,
            });
        }
        if (dialog.Result is { } result)
        {
            if (result.Converted > 0) context.Host.RequestRefresh();
            context.Host.Notify(AdjustBatchDialog.Summarize(result));
        }
        return Task.CompletedTask;
    }
}

/// <summary>選んだ画像を 90° 単位で回して上書き保存する（画素を回す。JPEG / WEBP は保存の画質で圧縮し直す）</summary>
public sealed class RotateCommand(Form owner, RotateDirection direction) : ImageCommandBase
{
    /// <summary>
    /// 回転は 1 つずつ順に行う（右・左・180° で共通）。終わる前にもう一度押されたら、前のが終わってから回す
    /// （同じファイルを同時に読んで書くと、回した結果の片方が消えて、押した回数どおりに回らないため）
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public override string Id => direction switch
    {
        RotateDirection.Right90 => "image.rotateRight",
        RotateDirection.Left90 => "image.rotateLeft",
        _ => "image.rotate180",
    };

    public override string Name => direction switch
    {
        RotateDirection.Right90 => "右に 90° 回転",
        RotateDirection.Left90 => "左に 90° 回転",
        _ => "180° 回転",
    };

    public override async Task ExecuteAsync(CommandContext context)
    {
        var paths = context.Paths;
        var progress = new Progress<ConvertProgress>(p =>
        {
            if (p.Done < p.Total && p.Total > 1) context.Host.Notify($"回転中 {p.Done + 1} / {p.Total}: {p.Name}");
        });
        if (!Gate.Wait(0))
        {
            context.Host.Notify("前の回転が終わってから回します…");
            await Gate.WaitAsync();
        }
        ConvertResult result;
        try
        {
            result = await Task.Run(() => Rotator.RotateFiles(paths, direction, progress));
        }
        finally
        {
            Gate.Release();
        }
        if (result.Converted > 0) context.Host.RequestRefresh();
        context.Host.Notify($"{result.Converted} 枚を{Name.Replace(" 回転", "")}回転しました" + (result.Errors.Count > 0 ? $"・{result.Errors.Count} 枚は回転できませんでした" : ""));
        if (result.Errors.Count > 0)
            MessageBox.Show(owner, string.Join("\n", result.Errors.Take(15)) + (result.Errors.Count > 15 ? $"\n…ほか {result.Errors.Count - 15} 件" : ""),
                $"回転できなかった画像（{result.Errors.Count} 枚）", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
