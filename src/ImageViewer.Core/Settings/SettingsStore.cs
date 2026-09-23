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
