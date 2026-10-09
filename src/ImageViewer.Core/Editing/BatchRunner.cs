// 画像を 1 枚ずつ処理する一括処理の進め方（リサイズ・まとめて補正・回転で共通）。
// 何をするか・上書きしてよいかは、渡された「1 枚を処理する関数」の側で決める
namespace ImageViewer.Core.Editing;

internal static class BatchRunner
{
    /// <summary>計画の Ok のものを 1 枚ずつ処理する。計画で飛ばすことにしたものは「飛ばした」に数える</summary>
    public static ConvertResult Run(IReadOnlyList<ConvertPlanItem> plan, Action<ConvertPlanItem> processOne,
        IProgress<ConvertProgress>? progress, CancellationToken ct) =>
        Run(plan.Where(p => p.Status == ConvertStatus.Ok).ToList(), p => p.SourceName, processOne,
            plan.Count(p => p.Status == ConvertStatus.Skip), progress, ct);

    /// <summary>
    /// items を順に処理する。1 枚ごとに始める前に打ち切りを確かめ、進み具合を知らせる。
    /// 上書きしない保存で保存先ができていたもの（DestinationExistsException）は「飛ばした」、ほかの失敗は「名前: 理由」で返す
    /// </summary>
    public static ConvertResult Run<T>(IReadOnlyList<T> items, Func<T, string> nameOf, Action<T> processOne,
        int skippedBefore, IProgress<ConvertProgress>? progress, CancellationToken ct)
    {
        int done = 0, skipped = skippedBefore;
        var errors = new List<string>();
        for (int i = 0; i < items.Count; i++)
        {
            if (ct.IsCancellationRequested) return new(done, skipped, errors, true);
            string name = nameOf(items[i]);
            progress?.Report(new(i, items.Count, name));
            try
            {
                processOne(items[i]);
                done++;
            }
            catch (DestinationExistsException)
            {
                skipped++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }
        progress?.Report(new(items.Count, items.Count, ""));
        return new(done, skipped, errors, false);
    }
}
