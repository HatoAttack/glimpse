// リネームの実行
// 1) 変更するファイルをすべて一時的な名前にする 2) 一時的な名前から変更後の名前にする、の 2 段階で行う。
//    a→b・b→a の入れ替えや、並べ替えてからの振り直し（a001→a003 のとき a003 がまだある）も衝突しない。
// 途中で失敗したら、それまでに変えたものをすべて元の名前に戻してから例外を投げる（中途半端な状態を残さない）
namespace ImageViewer.Core.Rename;

public static class RenameExecutor
{
    /// <summary>実行した名前変更の一覧（元に戻すときは From/To を入れ替えて渡す）</summary>
    public static IReadOnlyList<RenameOp> Execute(IEnumerable<RenameOp> ops)
    {
        var list = ops.Where(o => !string.Equals(o.From, o.To, StringComparison.Ordinal)).ToList();
        if (list.Count == 0) return list;

        string tag = Guid.NewGuid().ToString("N")[..8];
        var temps = list.Select((o, i) => Path.Combine(Path.GetDirectoryName(o.From)!, $"~ivren_{tag}_{i}.tmp")).ToList();
        // 各ファイルの現在地: 0=元の名前 / 1=一時的な名前 / 2=変更後の名前
        var stage = new int[list.Count];
        try
        {
            for (int i = 0; i < list.Count; i++)
            {
                File.Move(list[i].From, temps[i]);
                stage[i] = 1;
            }
            for (int i = 0; i < list.Count; i++)
            {
                File.Move(temps[i], list[i].To);
                stage[i] = 2;
            }
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var notRestored = Rollback(list, temps, stage);
            string detail = notRestored.Count == 0
                ? "変更はすべて元に戻しました。"
                : "次のファイルは元に戻せませんでした:\n" + string.Join("\n", notRestored);
            throw new IOException($"名前を変更できませんでした（{ex.Message.Trim()}）\n{detail}", ex);
        }
    }

    /// <summary>
    /// 元の名前へ戻す。戻す側も 2 段階（変更後の名前 → 一時的な名前 → 元の名前）にしないと、
    /// 入れ替えの途中で失敗したとき「元の名前」を別のファイルが使っていて戻せない。戻せなかったもの（今ある場所）を返す
    /// </summary>
    private static List<string> Rollback(List<RenameOp> list, List<string> temps, int[] stage)
    {
        var failed = new List<string>();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (stage[i] != 2) continue;
            if (TryMove(list[i].To, temps[i])) stage[i] = 1;
        }
        for (int i = list.Count - 1; i >= 0; i--)
        {
            string? current = stage[i] switch { 1 => temps[i], 2 => list[i].To, _ => null };
            if (current == null) continue;
            if (stage[i] != 1 || !TryMove(current, list[i].From))
                failed.Add($"{current} （元の名前: {Path.GetFileName(list[i].From)}）");
        }
        return failed;
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
