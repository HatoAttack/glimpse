// 手動の並び順の計算（保存した並びの適用・ドラッグでの移動）
namespace ImageViewer.Core.Ordering;

public static class ManualOrder
{
    /// <summary>
    /// 保存した並び（ファイル名の列）を今のファイル一覧に当てる。
    /// 見つからない名前は捨て、保存後に増えたファイルは files の順（通常は名前順）で末尾に足す
    /// </summary>
    public static List<FileInfo> Apply(IReadOnlyList<FileInfo> files, IReadOnlyList<string> savedNames)
    {
        var byName = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) byName[f.Name] = f;

        var result = new List<FileInfo>(files.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in savedNames)
            if (byName.TryGetValue(name, out var f) && used.Add(name)) result.Add(f);
        foreach (var f in files)
            if (!used.Contains(f.Name)) result.Add(f);
        return result;
    }

    /// <summary>
    /// moving の項目（元の並びでの番号）を、元の並びでの挿入位置 insertAt（0〜count）の前へまとめて動かしたときの新しい並び。
    /// 戻り値は「新しい位置 → 元の番号」。動かす項目どうしの順番は保つ
    /// </summary>
    public static int[] Move(int count, IEnumerable<int> moving, int insertAt)
    {
        var moved = new SortedSet<int>(moving.Where(i => i >= 0 && i < count));
        insertAt = Math.Clamp(insertAt, 0, count);
        var rest = Enumerable.Range(0, count).Where(i => !moved.Contains(i)).ToList();
        // 挿入位置より前にあった「動かす項目」の分だけ、残りの並びでの位置は前にずれる
        int at = insertAt - moved.Count(i => i < insertAt);
        rest.InsertRange(at, moved);
        return rest.ToArray();
    }
}
