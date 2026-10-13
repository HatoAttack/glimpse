// サムネイルの下に出す名前を 1 行に収める（長い名前は拡張子を残して途中を「…」にする）
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
        // 拡張子の前の数文字も残す（「IMG_0012.jpg」と「IMG_0013.jpg」を見分けられるように）
        int tailLength = ext.Length == 0 ? 0 : Math.Min(stem.Length / 2, 3);
        if (tailLength > 0 && tailLength < stem.Length && char.IsLowSurrogate(stem[^tailLength])) tailLength++; // 絵文字などの途中で切らない
        string tail = stem[^tailLength..] + ext;
        string head = stem[..^tailLength];

        // 前半を何文字残せるかを二分探索する
        int lo = 0, hi = head.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (measure(Compose(head, mid, tail)) <= width) lo = mid;
            else hi = mid - 1;
        }
        if (lo > 0) return Compose(head, lo, tail);
        // 前半を全部削っても入らないときは、拡張子だけでも見えるように末尾の数文字もあきらめる
        // （それでも入らなければ描画側の切り捨てに任せる）
        return measure(Ellipsis + tail) <= width ? Ellipsis + tail : Ellipsis + ext;
    }

    private static string Compose(string head, int count, string tail)
    {
        // サロゲートペアの途中で切らない
        if (count > 0 && count < head.Length && char.IsHighSurrogate(head[count - 1])) count--;
        return head[..count].TrimEnd() + Ellipsis + tail;
    }
}
