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

    public void Clear() => _marked.Clear();

    /// <summary>existing に含まれないもの（削除・改名されたファイル）のチェックを捨てる</summary>
    public void Retain(IEnumerable<string> existing)
    {
        var keep = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        _marked.RemoveWhere(p => !keep.Contains(p));
    }
}
