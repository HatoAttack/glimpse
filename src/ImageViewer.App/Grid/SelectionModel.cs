// 複数選択の状態（クリック・Ctrl・Shift・キー移動）。エクスプローラーと同じ操作感に合わせる
namespace ImageViewer.App.Grid;

/// <summary>ドラッグ範囲選択での既存の選択の扱い</summary>
public enum BandMode
{
    /// <summary>修飾キーなし: 囲んだものだけを選択</summary>
    Replace,
    /// <summary>Shift: 既存の選択に追加</summary>
    Add,
    /// <summary>Ctrl: 囲んだものの選択を反転</summary>
    Toggle,
}

public sealed class SelectionModel
{
    private readonly HashSet<int> _selected = new();
    private int _itemCount;

    // ドラッグ範囲選択中: 開始時点の選択（枠が動くたびにここから計算し直す）
    private HashSet<int>? _bandBase;
    private BandMode _bandMode;

    /// <summary>Shift で範囲選択するときの起点</summary>
    public int Anchor { get; private set; } = -1;

    /// <summary>キーボード操作の現在位置</summary>
    public int Focus { get; private set; } = -1;

    public int Count => _selected.Count;

    public bool IsSelected(int index) => _selected.Contains(index);

    public IReadOnlyList<int> SelectedIndices => _selected.Order().ToList();

    public void Reset(int itemCount)
    {
        _itemCount = itemCount;
        _selected.Clear();
        _bandBase = null;
        Anchor = Focus = -1;
    }

    /// <summary>通常クリック: それだけを選択</summary>
    public void Click(int index)
    {
        if (!Valid(index)) return;
        _selected.Clear();
        _selected.Add(index);
        Anchor = Focus = index;
    }

    /// <summary>Ctrl+クリック: 選択を反転</summary>
    public void CtrlClick(int index)
    {
        if (!Valid(index)) return;
        if (!_selected.Remove(index)) _selected.Add(index);
        Anchor = Focus = index;
    }

    /// <summary>Shift+クリック: 起点からの範囲を選択（keepOthers=Ctrl も押下で既存の選択を残す）</summary>
    public void ShiftClick(int index, bool keepOthers = false)
    {
        if (!Valid(index)) return;
        if (!Valid(Anchor))
        {
            Click(index);
            return;
        }
        if (!keepOthers) _selected.Clear();
        int from = Math.Min(Anchor, index), to = Math.Max(Anchor, index);
        for (int i = from; i <= to; i++) _selected.Add(i);
        Focus = index; // 起点は動かさない
    }

    public void SelectAll()
    {
        for (int i = 0; i < _itemCount; i++) _selected.Add(i);
        if (Focus < 0 && _itemCount > 0) Anchor = Focus = 0;
    }

    public void Clear() => _selected.Clear();

    // ---- ドラッグ範囲選択 ----

    public bool IsBanding => _bandBase != null;

    public void BeginBand(BandMode mode)
    {
        _bandMode = mode;
        _bandBase = mode == BandMode.Replace ? new HashSet<int>() : new HashSet<int>(_selected);
        if (mode == BandMode.Replace) _selected.Clear();
    }

    /// <summary>枠に掛かっている項目で選択を作り直す（枠が縮んだら外れた項目は元に戻る）</summary>
    public void UpdateBand(IEnumerable<int> inBand)
    {
        if (_bandBase == null) return;
        _selected.Clear();
        _selected.UnionWith(_bandBase);
        int first = -1;
        foreach (int i in inBand)
        {
            if (!Valid(i)) continue;
            if (first < 0 || i < first) first = i;
            if (_bandMode == BandMode.Toggle && _bandBase.Contains(i)) _selected.Remove(i);
            else _selected.Add(i);
        }
        if (first >= 0) Anchor = Focus = first;
    }

    public void EndBand() => _bandBase = null;

    /// <summary>
    /// キー移動: 現在位置から delta 進める（範囲外は端で止まる）。
    /// shift=範囲選択を伸ばす / ctrl=選択を変えずに位置だけ動かす / どちらも無し=移動先だけを選択
    /// </summary>
    public void Move(int delta, bool shift, bool ctrl) => MoveTo(Focus < 0 ? 0 : Focus + delta, shift, ctrl);

    public void MoveTo(int index, bool shift, bool ctrl)
    {
        if (_itemCount == 0) return;
        index = Math.Clamp(index, 0, _itemCount - 1);
        if (shift) ShiftClick(index, keepOthers: ctrl);
        else if (ctrl) Focus = index;
        else Click(index);
    }

    private bool Valid(int index) => index >= 0 && index < _itemCount;
}
