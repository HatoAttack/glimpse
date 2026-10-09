// 編集ダイアログ（モザイク・ぼかし / 枠・矢印）で、画像ごとに覚えておく「まだ保存していない編集」
namespace ImageViewer.Core.Editing;

/// <summary>
/// 画像を行き来しても消えないよう、画像（番号）ごとに編集を覚える。
/// 元の画像に上書きした分は Discard で捨てる（戻ってきたときに、もうかけてある画像へ二重にかけないように）
/// </summary>
public sealed class PendingEdits<T>
{
    private readonly Dictionary<int, List<T>> _byIndex = new();

    /// <summary>表示中の画像の編集</summary>
    public List<T> Current { get; private set; } = new();

    /// <summary>別の画像へ移る前に、今の編集を index の画像の分として覚える</summary>
    public void Remember(int index) => _byIndex[index] = Current;

    /// <summary>index の画像を出した: 覚えていた編集に切り替える（無ければ空）</summary>
    public void Recall(int index) => Current = _byIndex.TryGetValue(index, out var saved) ? saved : new();

    /// <summary>今の編集を置き換える（ほかの画像から引き継いだ範囲など。覚えている分は変えない）</summary>
    public void Set(List<T> items) => Current = items;

    /// <summary>index の画像に上書きした: 今の編集も、覚えていた分も捨てる</summary>
    public void Discard(int index)
    {
        Current = new();
        _byIndex.Remove(index);
    }

    /// <summary>全部の画像に上書きした: 覚えていた分をすべて捨てる</summary>
    public void DiscardAll()
    {
        Current = new();
        _byIndex.Clear();
    }
}
