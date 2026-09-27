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
            string name = Path.GetFileName(path);
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; !used.Add(name); i++) name = $"{stem} ({i}){ext}";
            string dest = Path.Combine(dir, name);
            ZipStore.Extract(path, dest);
            result.Add(dest);
        }
        return result;
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
