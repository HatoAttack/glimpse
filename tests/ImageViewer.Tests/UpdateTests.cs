// 更新の確認（リリース情報の読み取り・バージョン比較・チェックサム）と exe の入れ替えの動作確認（通信はしない）
using ImageViewer.Core.Updates;

static class UpdateTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        // ---- リリース情報 ----
        const string json = """
            {
              "tag_name": "v0.1.4",
              "html_url": "https://github.com/HatoAttack/ima-ge-viewer/releases/tag/v0.1.4",
              "body": "## 追加\r\n- フォルダー名の変更",
              "assets": [
                { "name": "ImageViewer.exe.sha256", "size": 81, "browser_download_url": "https://example.invalid/ImageViewer.exe.sha256" },
                { "name": "ImageViewer.exe", "size": 72445677, "browser_download_url": "https://example.invalid/ImageViewer.exe" }
              ]
            }
            """;
        var r = UpdateChecker.Parse(json);
        check(r != null && r.Version == new Version(0, 1, 4) && r.Tag == "v0.1.4" && r.ExeSize == 72445677
              && r.ExeUrl!.EndsWith("/ImageViewer.exe") && r.Sha256Url!.EndsWith(".sha256") && r.Notes.Contains("フォルダー名"),
            "リリース情報を読む（バージョン・exe・チェックサム・説明文）");
        var noAssets = UpdateChecker.Parse("""{ "tag_name": "v0.1.3", "assets": [] }""");
        check(noAssets != null && noAssets.ExeUrl == null && noAssets.Sha256Url == null, "exe が付いていないリリースも読める（自動更新はしない）");
        check(UpdateChecker.Parse("{ 壊れた") == null && UpdateChecker.Parse("""{ "tag_name": "latest" }""") == null,
            "壊れた応答・バージョンでないタグは null");

        // ---- バージョン ----
        check(UpdateChecker.ParseVersion("v0.1.4") == new Version(0, 1, 4) && UpdateChecker.ParseVersion("0.2") == new Version(0, 2, 0)
              && UpdateChecker.ParseVersion("v1.0.0-beta") == new Version(1, 0, 0), "タグからバージョン（v・後ろの印を除き 3 桁に）");
        check(UpdateChecker.IsNewer(r!, new Version(0, 1, 3, 0)) && !UpdateChecker.IsNewer(r!, new Version(0, 1, 4, 0))
              && !UpdateChecker.IsNewer(r!, new Version(0, 2, 0)), "新しいかどうか（4 桁目は無視）");

        // ---- チェックサム ----
        const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        check(UpdateChecker.ParseSha256($"{abc.ToUpperInvariant()}  ImageViewer.exe") == abc && UpdateChecker.ParseSha256(abc + "\n") == abc
              && UpdateChecker.ParseSha256("not a hash") == null, "チェックサムのファイルを読む（sha256sum 形式・値だけ・大文字）");
        string upd = Path.Combine(dir, "update");
        Directory.CreateDirectory(upd);
        string abcFile = Path.Combine(upd, "abc.txt");
        File.WriteAllText(abcFile, "abc");
        check(SelfUpdate.ComputeSha256(abcFile) == abc, "ファイルのチェックサムを計算");

        // ---- 入れ替え ----
        string exe = Path.Combine(upd, "ImageViewer.exe"), fresh = SelfUpdate.NewPath(exe);
        File.WriteAllText(exe, "old");
        File.WriteAllText(fresh, "new");
        SelfUpdate.Swap(exe, fresh);
        check(File.ReadAllText(exe) == "new" && File.ReadAllText(SelfUpdate.OldPath(exe)) == "old" && !File.Exists(fresh),
            "入れ替え: 今の exe を .old にずらして新しい exe を置く");
        File.WriteAllText(fresh, "newer");
        SelfUpdate.Swap(exe, fresh);
        check(File.ReadAllText(exe) == "newer" && File.ReadAllText(SelfUpdate.OldPath(exe)) == "new", "入れ替え: 前の .old が残っていても上書きする");
        bool threw = false;
        try { SelfUpdate.Swap(exe, Path.Combine(upd, "無い.new")); }
        catch (IOException) { threw = true; }
        check(threw && File.ReadAllText(exe) == "newer", "入れ替え: 新しい exe を置けなければ元に戻す");
        File.WriteAllText(fresh, "途中");
        SelfUpdate.CleanUp(exe);
        check(!File.Exists(SelfUpdate.OldPath(exe)) && !File.Exists(fresh) && File.Exists(exe), "起動時の片付け: .old と途中の .new を消す");
    }
}
