// 更新の確認（GitHub の最新リリース）。問い合わせは起動時に 1 回だけで、応答が無ければ諦める（常駐・定期的な通信はしない）
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ImageViewer.Core.Updates;

/// <summary>最新リリースの情報（exe・チェックサムのファイルが無いリリースもある）</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string PageUrl, string? ExeUrl, long ExeSize, string? Sha256Url);

public static class UpdateChecker
{
    public const string LatestReleaseApi = "https://api.github.com/repos/HatoAttack/ima-ge-viewer/releases/latest";
    public const string ReleasesPage = "https://github.com/HatoAttack/ima-ge-viewer/releases/latest";
    public const string ExeName = "ImageViewer.exe";
    public const string Sha256Name = ExeName + ".sha256";

    /// <summary>更新の確認・ダウンロード用。GitHub の API は User-Agent が無いと断られる</summary>
    public static HttpClient CreateClient(Version current)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // 打ち切りは呼び出し側の CancellationToken で
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ima-ge-viewer", Normalize(current).ToString()));
        return http;
    }

    /// <summary>最新リリースを問い合わせる。つながらない・読めないときは null（例外にしない）</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            return Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>GitHub の API の応答（releases/latest）を読む。形が違えば null</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (ParseVersion(tag) is not Version version) return null;
            string? exeUrl = null, shaUrl = null;
            long exeSize = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.GetProperty("name").GetString();
                    string? url = asset.GetProperty("browser_download_url").GetString();
                    if (string.Equals(name, ExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        exeUrl = url;
                        exeSize = asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0;
                    }
                    else if (string.Equals(name, Sha256Name, StringComparison.OrdinalIgnoreCase))
                    {
                        shaUrl = url;
                    }
                }
            }
            string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
            return new ReleaseInfo(version, tag, notes, page, exeUrl, exeSize, shaUrl);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>"v0.1.4" → 0.1.4（3 桁にそろえる）。読めなければ null</summary>
    public static Version? ParseVersion(string tag)
    {
        string s = tag.Trim().TrimStart('v', 'V');
        int suffix = s.IndexOfAny(new[] { '+', '-' });
        if (suffix >= 0) s = s[..suffix];
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    /// <summary>比べられるよう 3 桁（major.minor.build）にそろえる</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public static bool IsNewer(ReleaseInfo release, Version current) => release.Version > Normalize(current);

    /// <summary>
    /// リリースの説明文（GitHub の Markdown）を、アプリの文字欄で読みやすい形にする。
    /// 見出し「## 追加」→「【追加】」、太字・コードの記号（** と `）は外す。改行は Windows の形に
    /// </summary>
    public static string NotesToPlainText(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                string heading = trimmed.TrimStart('#').Trim();
                line = heading.Length > 0 ? $"【{heading}】" : "";
            }
            return line.Replace("**", "").Replace("`", "");
        });
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>チェックサムのファイル（sha256sum 形式・値だけのどちらでも）から 64 桁の 16 進を取り出す</summary>
    public static string? ParseSha256(string text)
    {
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = token.Trim().TrimStart('*');
            if (t.Length == 64 && t.All(Uri.IsHexDigit)) return t.ToLowerInvariant();
        }
        return null;
    }
}
