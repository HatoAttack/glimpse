// リネームの計画（変更後の名前の決定と検査）。ファイルには触らないので、プレビューにそのまま使える
using System.Text.RegularExpressions;

namespace ImageViewer.Core.Rename;

/// <summary>1 件の名前変更（フルパス）</summary>
public sealed record RenameOp(string From, string To);

public sealed record RenameOptions
{
    /// <summary>true: 文字列＋連番＋文字列 / false: 元の名前を元に置換・付け足し</summary>
    public bool UseSequence { get; init; } = true;

    // ---- 連番 ----
    public string Prefix { get; init; } = "";
    public long Start { get; init; } = 1;
    public int Digits { get; init; } = 3;
    public long Step { get; init; } = 1;
    public string SequenceSuffix { get; init; } = "";

    // ---- 元の名前を元にする ----
    public string ReplaceSearch { get; init; } = "";
    public string ReplaceWith { get; init; } = "";
    public string Suffix { get; init; } = "";

    // ---- 共通 ----
    /// <summary>名前も拡張子もすべて小文字</summary>
    public bool Lowercase { get; init; }

    /// <summary>拡張子だけ小文字</summary>
    public bool LowercaseExtension { get; init; }
}

public enum RenameStatus { Ok, Unchanged, Error }

public sealed record RenamePlanItem(string Source, string Target, RenameStatus Status, string? Error)
{
    public string SourceName => Path.GetFileName(Source);
    public string TargetName => Path.GetFileName(Target);
}

public static class RenamePlanner
{
    /// <summary>paths の順（＝画面の並び順）に連番を振った計画</summary>
    public static List<RenamePlanItem> Plan(IReadOnlyList<string> paths, RenameOptions options)
    {
        var targets = new List<string>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
            targets.Add(Path.Combine(Path.GetDirectoryName(paths[i])!, NewName(Path.GetFileName(paths[i]), i, options)));

        // 変更後の名前どうしの重複（Windows は大文字小文字を区別しない）
        var duplicated = targets.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

        var plan = new List<RenamePlanItem>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            string src = paths[i], dst = targets[i];
            string? error = ValidateName(Path.GetFileName(dst));
            if (error == null && duplicated.Contains(dst)) error = "変更後の名前が重複しています";
            // 対象外の既存ファイルと同名になるのは不可（対象どうしの入れ替えは 2 段階で行うので可）
            if (error == null && !sources.Contains(dst) && File.Exists(dst)) error = "同じ名前のファイルがすでにあります";

            var status = error != null ? RenameStatus.Error
                : string.Equals(src, dst, StringComparison.Ordinal) ? RenameStatus.Unchanged
                : RenameStatus.Ok;
            plan.Add(new RenamePlanItem(src, dst, status, error));
        }
        return plan;
    }

    public static string NewName(string fileName, int index, RenameOptions o)
    {
        string ext = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);

        if (o.UseSequence)
        {
            long number = o.Start + index * o.Step;
            stem = o.Prefix + number.ToString(new string('0', Math.Clamp(o.Digits, 1, 18))) + o.SequenceSuffix;
        }
        else
        {
            if (o.ReplaceSearch.Length > 0) stem = stem.Replace(o.ReplaceSearch, o.ReplaceWith, StringComparison.Ordinal);
            stem += o.Suffix;
        }

        if (o.Lowercase) return (stem + ext).ToLowerInvariant();
        if (o.LowercaseExtension) ext = ext.ToLowerInvariant();
        return stem + ext;
    }

    private static readonly Regex Reserved = new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase);

    /// <summary>Windows のファイル名として使えるか。使えなければ理由</summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name))) return "名前が空です";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "使えない文字（\\ / : * ? \" < > |）が含まれています";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "末尾に . や空白は使えません";
        if (Reserved.IsMatch(name)) return "Windows の予約名は使えません";
        if (name.Length > 255) return "名前が長すぎます";
        return null;
    }

    /// <summary>
    /// ダイアログの初期値をファイル名から推測する（末尾の数字を連番とみなす）。
    /// 文字列と桁数は先頭のファイルから、開始番号は同じ形の名前のうち一番小さい番号にする。
    /// 例: 並べ替えた a260019, a260003, a260000 … → 文字列 "a"・開始 260000・桁数 6（元の番号の範囲で振り直せる）
    /// </summary>
    public static RenameOptions GuessSequence(IReadOnlyList<string> fileNames)
    {
        var first = ParseNumbered(fileNames[0]);
        if (first == null)
            return new RenameOptions { Prefix = Path.GetFileNameWithoutExtension(fileNames[0]) + "_", Start = 1, Digits = 3 };

        long min = first.Value.Number;
        foreach (var name in fileNames)
            if (ParseNumbered(name) is { } p && p.Prefix == first.Value.Prefix && p.Digits == first.Value.Digits)
                min = Math.Min(min, p.Number);
        return new RenameOptions { Prefix = first.Value.Prefix, Start = min, Digits = first.Value.Digits };
    }

    private static (string Prefix, long Number, int Digits)? ParseNumbered(string fileName)
    {
        var m = Regex.Match(Path.GetFileNameWithoutExtension(fileName), @"^(.*?)(\d+)$");
        if (!m.Success || m.Groups[2].Value.Length > 18) return null;
        return (m.Groups[1].Value, long.Parse(m.Groups[2].Value), m.Groups[2].Value.Length);
    }
}
