// 自分自身（exe）の入れ替え。起動中の exe は上書きできないが名前は変えられるので、
// 今の exe を .old にずらして新しい exe を置き、再起動する。.old は次の起動時に消す（更新用の別プログラムは要らない）
using System.Net.Http;
using System.Security.Cryptography;

namespace ImageViewer.Core.Updates;

public static class SelfUpdate
{
    public static string OldPath(string exe) => exe + ".old";
    public static string NewPath(string exe) => exe + ".new";

    /// <summary>前回の更新で残った .old と、途中で止まった .new を消す（起動時。消せなくても気にしない）</summary>
    public static void CleanUp(string exe)
    {
        foreach (var path in new[] { OldPath(exe), NewPath(exe) })
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>ダウンロードして dest に保存する。progress には (受け取った量, 全体の量) を渡す</summary>
    public static async Task DownloadAsync(HttpClient http, string url, string dest, IProgress<(long Done, long? Total)>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            progress?.Report((done, total));
        }
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>今の exe を .old にずらして、新しい exe を置く。置けなかったら元に戻して例外を投げる</summary>
    public static void Swap(string exe, string newFile)
    {
        string old = OldPath(exe);
        File.Move(exe, old, overwrite: true);
        try
        {
            File.Move(newFile, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }
}
