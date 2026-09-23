// ファイラとしての移動（戻る / 進むの履歴・サブフォルダの一覧・アドレスバーの入力）の動作確認
using ImageViewer.Core.Navigation;

static class NavigationTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        // ---- 戻る / 進む ----
        var h = new NavigationHistory(limit: 3);
        check(!h.CanGoBack && !h.CanGoForward && h.GoBack() == null, "最初は戻れない・進めない");
        h.Navigate(@"C:\a");
        h.Navigate(@"C:\b");
        h.Navigate(@"C:\B"); // 同じ場所（大文字小文字違い）は履歴に積まない
        h.Navigate(@"C:\c");
        check(h.GoBack() == @"C:\b" && h.GoBack() == @"C:\a" && !h.CanGoBack, "戻る");
        check(h.GoForward() == @"C:\b" && h.CanGoForward, "進む");
        h.Navigate(@"C:\x");
        check(!h.CanGoForward && h.Current == @"C:\x", "戻ってから別の場所へ行くと 進む は消える");
        foreach (var p in new[] { @"C:\1", @"C:\2", @"C:\3", @"C:\4" }) h.Navigate(p);
        int backs = 0;
        while (h.GoBack() != null) backs++;
        check(backs == 3, $"履歴は上限（3）まで。古いものから捨てる（戻れた回数 {backs}）");

        // ---- サブフォルダの一覧 ----
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "img10", "img2", "Album" }) Directory.CreateDirectory(Path.Combine(dir, name));
        var hidden = Directory.CreateDirectory(Path.Combine(dir, ".cache"));
        hidden.Attributes |= FileAttributes.Hidden;
        File.WriteAllText(Path.Combine(dir, "file.jpg"), "x");
        var subs = FolderListing.ListSubfolders(dir).Select(d => d.Name).ToArray();
        check(subs.SequenceEqual(new[] { "Album", "img2", "img10" }), $"サブフォルダは名前順・隠しフォルダとファイルは除く ({string.Join(",", subs)})");
        check(FolderListing.ListSubfolders(Path.Combine(dir, "nothing")).Count == 0, "存在しないフォルダは空（例外にしない）");

        check(FolderListing.Parent(Path.Combine(dir, "img2")) == Path.GetFullPath(dir).TrimEnd('\\'), "上のフォルダ");
        check(FolderListing.Parent(@"C:\") == null, "ドライブのルートの上は無い");

        // ---- アドレスバーの入力 ----
        check(FolderListing.Normalize($"  \"{Path.Combine(dir, "img2")}\"  ") == Path.Combine(dir, "img2"), "前後の空白・引用符を除く");
        check(FolderListing.Normalize("%TEMP%")?.TrimEnd('\\') == Path.GetTempPath().TrimEnd('\\'), "環境変数を展開");
        check(FolderListing.Normalize("c:") == @"c:\", "ドライブ名だけ（C:）はルート");
        check(FolderListing.Normalize("img2") == null, "相対パスは受け付けない");
        check(FolderListing.Normalize(Path.Combine(dir, "nope")) == null && FolderListing.Normalize(Path.Combine(dir, "file.jpg")) == null,
            "無いフォルダ・ファイルは null");
        check(FolderListing.Normalize("C:\\a<b") == null && FolderListing.Normalize("") == null, "不正な文字・空は null");
    }
}
