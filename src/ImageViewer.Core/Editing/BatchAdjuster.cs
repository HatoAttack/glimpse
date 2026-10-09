// 選んだ画像にまとめて色調補正をかける（一覧のフッターの「補正」から）。
// 出力先と同名の扱いはリサイズと同じ決め方（OutputDestination）で、形式は元のまま（HEIC / RAW など書き出せないものは JPG）
namespace ImageViewer.Core.Editing;

/// <summary>まとめて補正の設定（補正の値そのものは「前回の補正」として別に持つ）</summary>
public sealed record AdjustBatchOptions
{
    /// <summary>レベル補正（黒点・白点・ガンマ）は 1 枚ずつ自動補正で決める</summary>
    public bool AutoLevels { get; init; }

    public OutputFolderMode OutputMode { get; init; } = OutputFolderMode.Subfolder;
    public string SubfolderName { get; init; } = "adjusted";
    public string? CustomFolder { get; init; }

    /// <summary>出力先に同名のファイルがあるとき上書きする（false ならその画像は飛ばす）</summary>
    public bool Overwrite { get; init; }

    /// <summary>出力先の設定だけを取り出す</summary>
    public OutputDestination Output() => new(OutputMode, SubfolderName, CustomFolder, Overwrite);
}

public static class BatchAdjuster
{
    /// <summary>出力先の名前を決めて検査する（ファイルには触らない）</summary>
    public static List<ConvertPlanItem> Plan(IReadOnlyList<string> sources, AdjustBatchOptions options) =>
        options.Output().Plan(sources,
            name => Path.GetFileNameWithoutExtension(name) + ImageSaver.ExtensionFor(OutputFormat.Keep, Path.GetExtension(name)),
            src => Converter.KeepsMetadata(src) ? null : Converter.MetadataLossNote);

    /// <summary>補正するものがあるか（値が既定のままで、自動補正もしないなら何も変わらない）</summary>
    public static bool HasWork(AdjustOptions adjust, AdjustBatchOptions options) => options.AutoLevels || !adjust.IsIdentity;

    /// <summary>計画の Ok のものを 1 枚ずつ補正して保存する（重い処理なので呼び出し側で別スレッドへ）</summary>
    /// <remarks>
    /// 撮影情報（EXIF）を残せないのが形式から分かっているもの（HEIC / RAW など。計画にも書いてある）はそのまま保存する。
    /// JPEG の亜種など、読んでみて初めて残せないと分かったものは保存せずに失敗として数える（黙って撮影情報を消さない）
    /// </remarks>
    /// <param name="overwriteSourcesOnly">
    /// true なら上書きするのは元の画像を置き換えるものだけ。ほかの保存先（HEIC の代わりに作る JPG など）は新しく作るだけで、
    /// 保存するときにその名前のファイルがあれば（計画の後にできたものでも）別の画像なので飛ばす
    /// </param>
    public static ConvertResult Run(IReadOnlyList<ConvertPlanItem> plan, AdjustOptions adjust, AdjustBatchOptions options,
        IProgress<ConvertProgress>? progress = null, CancellationToken ct = default, bool overwriteSourcesOnly = false)
    {
        return BatchRunner.Run(plan, item =>
        {
            bool overwrite = overwriteSourcesOnly ? item.ReplacesSource : options.Overwrite;
            Adjuster.ApplyToFile(item.Source, item.Target, adjust, overwrite,
                allowMetadataLoss: !Converter.KeepsMetadata(item.Source), autoLevels: options.AutoLevels);
        }, progress, ct);
    }
}
