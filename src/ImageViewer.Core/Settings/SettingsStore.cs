// アプリの設定（%LOCALAPPDATA%\ima-ge-viewer\settings.json）
// 小さな JSON 1 つだけ。読めない・壊れているときは初期値で起動する（設定のせいで起動できない状態にしない）
using System.Text.Json;

namespace ImageViewer.Core.Settings;

public sealed record AppSettings
{
    /// <summary>ホームフォルダ（未設定なら null = ピクチャ）</summary>
    public string? HomeFolder { get; init; }

    /// <summary>サムネイルの表示サイズ（論理 px。null なら既定の 160）</summary>
    public int? ThumbnailSize { get; init; }

    /// <summary>チェックを付け外しするキー（既定 ¥。その場に留まる）</summary>
    public string? MarkKey { get; init; }

    /// <summary>チェックを付け外しして次の画像へ進むキー（既定 ^）</summary>
    public string? MarkNextKey { get; init; }

    /// <summary>フォルダジャンプの索引を作る範囲（null ならユーザーフォルダだけ）</summary>
    public IReadOnlyList<string>? JumpRoots { get; init; }

    /// <summary>フォルダジャンプで Everything を使う（既定は使わない。起動していなければ自前の索引）</summary>
    public bool UseEverything { get; init; }

    /// <summary>リサイズ・形式変換で前回使った設定（null なら既定）</summary>
    public Editing.ConvertOptions? Resize { get; init; }

    /// <summary>連結で前回使った設定（null なら既定）</summary>
    public Editing.CombineOptions? Combine { get; init; }

    /// <summary>「フォルダーへ移動 / コピー」で最近選んだフォルダ（新しい順、最大 MaxRecentDestinations 件）</summary>
    public IReadOnlyList<string>? RecentDestinations { get; init; }

    public const int MaxRecentDestinations = 10;

    /// <summary>起動時に新しいバージョンを確認する（null は既定 = 確認する）</summary>
    public bool? CheckUpdatesOnStartup { get; init; }

    /// <summary>「このバージョンは飛ばす」を選んだバージョンのタグ（起動時の確認では知らせない）</summary>
    public string? SkippedVersion { get; init; }

    /// <summary>配色（"system" / "light" / "dark"。null はシステムに合わせる）</summary>
    public string? Theme { get; init; }

    /// <summary>左のサイドバー（フォルダツリー）を表示する（null は既定 = 表示）</summary>
    public bool? SidebarVisible { get; init; }

    /// <summary>一覧の右側の詳細パネル（インスペクタ）を表示する（null は既定 = 表示）</summary>
    public bool? InspectorVisible { get; init; }

    /// <summary>1 枚表示で詳細パネルを表示する（null は既定 = 表示しない。I キーで切り替え）</summary>
    public bool? QuickLookDetailsVisible { get; init; }

    /// <summary>JPEG で保存するときの画質（1〜100。null は既定の 90）</summary>
    public int? JpegQuality { get; init; }

    /// <summary>WEBP で保存するときの画質（1〜100。null は既定の 90）</summary>
    public int? WebpQuality { get; init; }

    /// <summary>最近の移動先の先頭に追加した設定（同じものは前から外す。件数に上限）</summary>
    public AppSettings WithRecentDestination(string folder) => this with
    {
        RecentDestinations = new[] { folder }
            .Concat((RecentDestinations ?? Array.Empty<string>()).Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxRecentDestinations).ToList(),
    };

    /// <summary>実際に使う範囲（保存はしない）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EffectiveJumpRoots =>
        JumpRoots is { Count: > 0 } roots ? roots : new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
}

public sealed class SettingsStore
{
    private readonly string _path;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SettingsStore(string path) => _path = path;

    public static SettingsStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ima-ge-viewer", "settings.json"));

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings) => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
}
