// サムネイルグリッドの配置計算（描画・スクロール・当たり判定で共通に使う）
namespace ImageViewer.App.Grid;

/// <param name="ClientWidth">スクロールバーを除いた描画領域の幅</param>
/// <param name="Gap">セル同士・外周の余白</param>
public readonly record struct GridLayout(int ItemCount, int ClientWidth, int CellWidth, int CellHeight, int Gap)
{
    public int Columns => Math.Max(1, (ClientWidth - Gap) / (CellWidth + Gap));
    public int Rows => (ItemCount + Columns - 1) / Columns;
    public int RowHeight => CellHeight + Gap;
    public int ContentHeight => Rows * RowHeight + Gap;

    /// <summary>余った横幅を左右に振り分けて中央寄せする</summary>
    private int OffsetX => Math.Max(0, (ClientWidth - (Columns * (CellWidth + Gap) + Gap)) / 2);

    /// <summary>コンテンツ座標（スクロール前）でのセルの位置</summary>
    public Rectangle CellBounds(int index)
    {
        int col = index % Columns, row = index / Columns;
        return new Rectangle(OffsetX + Gap + col * (CellWidth + Gap), Gap + row * RowHeight, CellWidth, CellHeight);
    }

    /// <summary>コンテンツ座標の点にあるセル。余白や範囲外なら -1</summary>
    public int IndexAt(int x, int contentY)
    {
        int lx = x - OffsetX - Gap, ly = contentY - Gap;
        if (lx < 0 || ly < 0) return -1;
        int col = lx / (CellWidth + Gap), row = ly / RowHeight;
        if (col >= Columns || lx % (CellWidth + Gap) >= CellWidth || ly % RowHeight >= CellHeight) return -1;
        int index = row * Columns + col;
        return index < ItemCount ? index : -1;
    }

    /// <summary>コンテンツ座標の矩形（範囲選択の枠）に少しでも掛かるセル</summary>
    public IEnumerable<int> IndicesIntersecting(Rectangle rect)
    {
        if (ItemCount == 0 || rect.Width <= 0 || rect.Height <= 0) yield break;
        int firstRow = Math.Max(0, (rect.Top - Gap) / RowHeight);
        int lastRow = Math.Min(Rows - 1, Math.Max(0, rect.Bottom - Gap) / RowHeight);
        for (int row = firstRow; row <= lastRow; row++)
            for (int col = 0; col < Columns; col++)
            {
                int i = row * Columns + col;
                if (i >= ItemCount) yield break;
                if (CellBounds(i).IntersectsWith(rect)) yield return i;
            }
    }

    /// <summary>scrollY から高さ viewHeight の範囲に少しでも掛かるセルの範囲（無ければ Count=0）</summary>
    public (int First, int Count) VisibleRange(int scrollY, int viewHeight)
    {
        if (ItemCount == 0 || viewHeight <= 0) return (0, 0);
        int firstRow = Math.Max(0, (scrollY - Gap) / RowHeight);
        int bottom = scrollY + viewHeight - 1 - Gap;
        if (bottom < 0) return (0, 0);
        int lastRow = Math.Min(Rows - 1, bottom / RowHeight);
        int first = firstRow * Columns;
        int last = Math.Min(ItemCount - 1, (lastRow + 1) * Columns - 1);
        return last < first ? (0, 0) : (first, last - first + 1);
    }
}
