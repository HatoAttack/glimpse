// チェック（マーク）の状態。選択とは独立していて、クリックやスクロールでは変わらない
// 並び順が変わっても、フォルダを再読み込みしても外れないよう、番号ではなくパスで覚える
namespace ImageViewer.App.Grid;

public sealed class MarkSet
{
    private readonly HashSet<string> _marked = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _marked.Count;

    public bool IsMarked(string path) => _marked.Contains(path);

    public void Toggle(string path)
    {
        if (!_marked.Remove(path)) _marked.Add(path);
    }

    public void Set(IEnumerable<string> paths, bool marked)
    {
        foreach (var p in paths)
        {
            if (marked) _marked.Add(p);
            else _marked.Remove(p);
        }
    }

    /// <summary>all のうち、チェックのあるものは外し、無いものは付ける</summary>
    public void Invert(IEnumerable<string> all)
    {
        foreach (var p in all) Toggle(p);
    }

    /// <summary>
    /// まとめて付け外し: 1 つでもチェックの無いものがあれば全部に付け、全部付いていれば全部外す
    /// （一部だけ付いているときに反転すると、ばらばらのまま残るため）。付けたら true
    /// </summary>
    public bool ToggleGroup(IReadOnlyCollection<string> paths)
    {
        bool mark = !paths.All(IsMarked);
        Set(paths, mark);
        return mark;
    }

    public void Clear() => _marked.Clear();

    /// <summary>名前を変えたファイルのチェックを新しいパスに付け替える（入れ替えにも対応）</summary>
    public void Remap(IReadOnlyDictionary<string, string> renamed)
    {
        var next = _marked.Select(p => renamed.TryGetValue(p, out var to) ? to : p).ToList();
        _marked.Clear();
        _marked.UnionWith(next);
    }

    /// <summary>existing に含まれないもの（削除・改名されたファイル）のチェックを捨てる</summary>
    public void Retain(IEnumerable<string> existing)
    {
        var keep = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        _marked.RemoveWhere(p => !keep.Contains(p));
    }
}
