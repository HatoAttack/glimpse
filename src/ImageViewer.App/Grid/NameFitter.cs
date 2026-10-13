// サムネイルの下に出す名前を 1 行に収める（長い名前は拡張子を残して途中を「…」にする）
using System.Globalization;

namespace ImageViewer.App.Grid;

public static class NameFitter
{
    private const string Ellipsis = "…";

    /// <summary>
    /// 幅に収まるように名前を詰める。収まればそのまま、収まらなければ「名前の前半…末尾の数文字.拡張子」にする。
    /// 拡張子（とその前の数文字）を残すので、似た名前や種類の違いが見分けられる
    /// </summary>
    /// <param name="measure">文字列の描画幅（px）</param>
    /// <param name="keepExtension">false なら拡張子を区切らず、末尾を「…」にする（普通のフォルダ）</param>
    public static string Fit(string name, int width, Func<string, int> measure, bool keepExtension = true)
    {
        if (name.Length == 0 || measure(name) <= width) return name;

        int dot = keepExtension ? name.LastIndexOf('.') : -1;
        // 先頭の「.」だけ（.gitignore など）や、拡張子が長すぎるものは拡張子として扱わない
        if (dot <= 0 || name.Length - dot > 16) dot = name.Length;
        string stem = name[..dot], ext = name[dot..];
        // 切る位置は見た目の 1 文字の境目だけ（結合した絵文字や濁点の付いた文字を途中で切らない）
        int[] starts = StringInfo.ParseCombiningCharacters(stem);
        // 拡張子の前の数文字も残す（「IMG_0012.jpg」と「IMG_0013.jpg」を見分けられるように）
        int tailCount = ext.Length == 0 ? 0 : Math.Min(starts.Length / 2, 3);
        int split = tailCount == 0 ? stem.Length : starts[^tailCount];
        string tail = stem[split..] + ext;
        int headCount = starts.Length - tailCount;

        // 前半を何文字残せるかを二分探索する
        int lo = 0, hi = headCount;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (measure(Compose(stem, starts, mid, tail)) <= width) lo = mid;
            else hi = mid - 1;
        }
        if (lo > 0) return Compose(stem, starts, lo, tail);
        // 前半を全部削っても入らないときは、拡張子だけでも見えるように末尾の数文字もあきらめる
        // （それでも入らなければ描画側の切り捨てに任せる）
        return measure(Ellipsis + tail) <= width ? Ellipsis + tail : Ellipsis + ext;
    }

    /// <summary>先頭から count 文字（見た目の文字数）＋「…」＋ tail</summary>
    private static string Compose(string stem, int[] starts, int count, string tail)
    {
        int end = count < starts.Length ? starts[count] : stem.Length;
        return stem[..end].TrimEnd() + Ellipsis + tail;
    }
}
