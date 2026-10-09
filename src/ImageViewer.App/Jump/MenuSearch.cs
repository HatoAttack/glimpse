// ☰ メニューの項目を名前で探す（アドレスバーに「>」や名前を入れたときの、コマンドの候補）
using System.Globalization;

namespace ImageViewer.App.Jump;

public static class MenuSearch
{
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

    /// <summary>
    /// メニューの項目（コマンド・表示の切り替えなど）を名前とメニューの場所で探す。スペース区切りはすべてを含むもの。
    /// ひらがな / カタカナ・全角 / 半角・大文字 / 小文字は区別しない。名前の先頭が合うもの → 名前に含むもの → 場所だけ合うもの の順
    /// （同じなら、今使える項目・メニューで上にある項目を先に）
    /// </summary>
    public static List<CommandCandidate> Find(ToolStripItemCollection menu, string text, int max)
    {
        var compare = CultureInfo.GetCultureInfo("ja-JP").CompareInfo;
        var tokens = text.Split(' ', '　').Where(t => t.Length > 0).ToArray();

        var found = new List<(CommandCandidate Candidate, int Score, int Order)>();
        void Walk(ToolStripItemCollection items, string where)
        {
            foreach (var item in items.OfType<ToolStripMenuItem>())
            {
                if (!item.Available) continue;
                string name = StripMnemonic(item.Text).TrimEnd('.', '…').Trim();
                if (item.DropDownItems.Count > 0)
                {
                    Walk(item.DropDownItems, where.Length > 0 ? $"{where} › {name}" : name);
                    continue;
                }
                string all = $"{name} {where}";
                if (!tokens.All(t => compare.IndexOf(all, t, Loose) >= 0)) continue;
                int score = tokens.Length == 0 || compare.IsPrefix(name, tokens[0], Loose) ? 0
                    : tokens.All(t => compare.IndexOf(name, t, Loose) >= 0) ? 1 : 2;
                var target = item;
                found.Add((new CommandCandidate(name, where, item.ShortcutKeyDisplayString ?? "", item.Enabled, () => target.PerformClick()), score, found.Count));
            }
        }
        Walk(menu, "");
        return found.OrderBy(f => f.Score).ThenBy(f => f.Candidate.Enabled ? 0 : 1).ThenBy(f => f.Order)
            .Take(max).Select(f => f.Candidate).ToList();
    }

    /// <summary>メニューの文字から、アクセスキーの印を除く（「画像(&amp;I)」→「画像」、「&amp;File」→「File」）</summary>
    public static string StripMnemonic(string? text) =>
        System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\(&.\)|&", "");
}
