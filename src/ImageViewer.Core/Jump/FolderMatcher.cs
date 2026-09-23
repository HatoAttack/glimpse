// フォルダジャンプの一致判定と点数付け（Listary に近い考え方）
// - 入力をスペースで区切り、最後の語はフォルダ名に、それ以外の語はそのフォルダまでのパスに含まれること
//   例: "2024 旅行" → パスに 2024 を含み、名前に 旅行 を含むフォルダ
// - 名前との一致の仕方で点数を付ける: 完全一致 > 先頭一致 > 単語の先頭 > 途中 > あいまい（imgtrp → img_trip）
namespace ImageViewer.Core.Jump;

public static class FolderMatcher
{
    public const int NoMatch = -1;

    public static string[] Tokenize(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>フォルダ名と最後の語の一致の点数（一致しなければ NoMatch）</summary>
    public static int ScoreName(string name, string token)
    {
        if (token.Length == 0) return NoMatch;
        if (name.Equals(token, StringComparison.OrdinalIgnoreCase)) return 1000;
        int at = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (at == 0) return 800 + 100 * token.Length / name.Length;
        if (at > 0)
        {
            // 単語の先頭（区切り文字の後・小文字→大文字の切り替わり）に一致するものを探す
            for (int i = at; i >= 0 && i < name.Length; i = name.IndexOf(token, i + 1, StringComparison.OrdinalIgnoreCase))
                if (IsWordStart(name, i)) return 600 + 100 * token.Length / name.Length;
            return 400 + 100 * token.Length / name.Length;
        }
        return token.Length >= 2 ? FuzzyScore(name, token) : NoMatch;
    }

    private static bool IsWordStart(string s, int i) =>
        i == 0 || s[i - 1] is ' ' or '_' or '-' or '.' or '(' or '[' || (char.IsLower(s[i - 1]) && char.IsUpper(s[i]))
        || (char.IsDigit(s[i]) != char.IsDigit(s[i - 1]));

    /// <summary>
    /// 文字が順番どおりに現れれば一致（あいまい）。ただし一致した範囲が入力の 3 倍より長いものは、
    /// 偶然そろっただけ（img → Anime_Streaming_Calendar 等）とみなして除く。連続して一致するほど高い。200〜299
    /// </summary>
    private static int FuzzyScore(string name, string token)
    {
        if (name.Length > 128) return NoMatch;
        int ti = 0, run = 0, bestRun = 0, first = -1, last = -1;
        for (int ni = 0; ni < name.Length && ti < token.Length; ni++)
        {
            if (char.ToLowerInvariant(name[ni]) == char.ToLowerInvariant(token[ti]))
            {
                if (first < 0) first = ni;
                last = ni;
                ti++;
                run++;
                bestRun = Math.Max(bestRun, run);
            }
            else
            {
                run = 0;
            }
        }
        if (ti < token.Length || last - first + 1 > token.Length * 3) return NoMatch;
        return 200 + Math.Min(99, 99 * bestRun / token.Length);
    }

    /// <summary>最後の語以外の語が、すべてパス（名前より上の部分も含む）に含まれるか</summary>
    public static bool PathContainsOthers(string fullPath, string[] tokens)
    {
        for (int i = 0; i < tokens.Length - 1; i++)
            if (fullPath.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }
}
