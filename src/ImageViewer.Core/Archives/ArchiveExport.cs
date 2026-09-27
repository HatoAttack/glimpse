// ZIP の中の画像を、ほかのアプリへ渡すために一時フォルダへ書き出す（コピー・フォルダーへコピー・ドラッグ）
// エクスプローラーなどはファイルのパスでしか受け取れないので、渡す分だけを書き出す。ZIP そのものは書き換えない。
// 書き出したものは、貼り付ける前にアプリを閉じても使えるよう終了時には消さず、次に起動したときに古いものから片付ける
namespace ImageViewer.Core.Archives;

public static class ArchiveExport
{
    /// <summary>書き出し先の親（%TEMP%\Glimpse\zip）。書き出すたびにこの下へ新しいフォルダを作る</summary>
    public static string Root { get; set; } = Path.Combine(Path.GetTempPath(), "Glimpse", "zip");

    /// <summary>
    /// ZIP の中の画像を一時フォルダへ書き出したパス（名前は同じ）。ZIP の中でないものはそのまま返す。
    /// 同じ名前が重なったら (2)… を付ける
    /// </summary>
    public static IReadOnlyList<string> ToFiles(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (!paths.Any(ArchivePath.IsInside)) return paths;
        string dir = Path.Combine(Root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(dir);
        // 書き出したパス全体が、多くのアプリが扱える長さ（MAX_PATH の 260 未満）に収まる名前の長さ。重なったときの「 (99)」の分も残す
        int limit = Math.Clamp(MaxPath - 1 - (dir.Length + 1) - SuffixRoom, MinNameLength, MaxNameLength);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (!ArchivePath.IsInside(path))
            {
                result.Add(path);
                continue;
            }
            string name = SafeFileName(Path.GetFileName(path), limit);
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; !used.Add(name); i++) name = $"{stem} ({i}){ext}";
            string dest = Path.Combine(dir, name);
            ZipStore.Extract(path, dest);
            result.Add(dest);
        }
        return result;
    }

    /// <summary>Windows の予約された名前（COM¹ などの上付き数字も）</summary>
    private static readonly HashSet<string> ReservedNames = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat("0123456789¹²³".SelectMany(d => new[] { $"COM{d}", $"LPT{d}" })),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Windows で使える名前にする（ほかの OS で作った ZIP には ? * : などを含む名前がある）。
    /// 使えない文字は _ に、末尾の . と空白は除き、CON などの予約された名前には _ を前に付ける（拡張子は残す）。
    /// 予約名かは最初の . より前で決まる（CON.preview.jpg も予約名）
    /// </summary>
    public static string SafeFileName(string name, int maxLength = MaxNameLength)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        string safe = new string(chars).TrimEnd('.', ' ');
        string head = safe.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(head)) safe = "_" + safe;
        if (safe.Length == 0 || Path.GetFileNameWithoutExtension(safe).Length == 0) safe = "_" + safe;
        return Truncate(safe, maxLength);
    }

    /// <summary>
    /// 書き出す名前の長さの上限。実際は書き出し先のフォルダの長さから、パス全体が MAX_PATH（260）未満に収まるよう
    /// さらに短くする（%TEMP% が長い場所にあっても、長いパスを扱えないアプリへ渡せるように）
    /// </summary>
    public const int MaxNameLength = 150;

    /// <summary>一時フォルダがとても長い場所にあっても、名前はこれより短くしない（読める名前を残す）</summary>
    private const int MinNameLength = 16;

    /// <summary>多くのアプリが扱えるパスの長さ（終わりの文字を含めて 260）と、重なったときの「 (99)」の分</summary>
    private const int MaxPath = 260, SuffixRoom = 5;

    /// <summary>長すぎる名前は、拡張子を残して名前の部分を切り詰める（サロゲートペアの途中では切らない）</summary>
    private static string Truncate(string name, int maxLength)
    {
        if (name.Length <= maxLength) return name;
        string ext = Path.GetExtension(name);
        if (ext.Length > 16 || ext.Length >= maxLength) ext = ""; // 拡張子に見えない長い末尾は名前として切る
        int keep = maxLength - ext.Length;
        if (char.IsHighSurrogate(name[keep - 1])) keep--;
        return name[..keep].TrimEnd('.', ' ') + ext;
    }

    /// <summary>前に書き出したもののうち、古いものを消す（使用中などで消せないものは残す）</summary>
    public static void CleanUp(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var limit = DateTime.UtcNow - olderThan;
            foreach (var dir in new DirectoryInfo(Root).EnumerateDirectories())
            {
                try
                {
                    if (dir.CreationTimeUtc < limit) dir.Delete(recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
