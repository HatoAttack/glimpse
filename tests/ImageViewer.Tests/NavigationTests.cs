// ファイラとしての移動（戻る / 進むの履歴・サブフォルダの一覧・アドレスバーの入力）の動作確認
using ImageViewer.Core.Navigation;
using ImageViewer.Core.Ordering;

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

        var hr = new NavigationHistory();
        foreach (var p in new[] { @"C:\写真\旅行", @"C:\写真", @"C:\写真2", @"C:\写真\家族" }) hr.Navigate(p);
        hr.GoBack(); // 進む に C:\写真\家族
        hr.Retarget(@"C:\写真", @"C:\画像");
        check(hr.Current == @"C:\写真2" && hr.GoBack() == @"C:\画像" && hr.GoBack() == @"C:\画像\旅行", "フォルダー名の変更で戻るの履歴を付け替える");
        hr.GoForward();
        check(hr.GoForward() == @"C:\写真2" && hr.GoForward() == @"C:\画像\家族", "進むの履歴も付け替える（似た名前の「写真2」は変えない）");

        // ---- フォルダの読み込み: 続けて別のフォルダを開いたら、最後に頼んだ読み込みの結果だけを返す ----
        using var started = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var loader = new FolderLoader((folder, _, mode, _) =>
        {
            if (folder.StartsWith("slow"))
            {
                started.Release();
                release.Wait(); // 打ち切りを見ないまま進む遅い読み込み
                if (folder == "slow-broken") throw new IOException("読めません");
            }
            return new FolderContents(new() { new DirectoryInfo(Path.Combine(dir, folder)) }, new(), mode, false);
        });
        check(!loader.Started, "読み込み: まだ何も頼んでいない");
        var slow = loader.LoadAsync("slow", false, SortMode.Name);
        started.Wait();
        var broken = loader.LoadAsync("slow-broken", false, SortMode.Name);
        started.Wait();
        var latest = loader.LoadAsync("B", false, SortMode.Size).GetAwaiter().GetResult();
        release.Set(); // B を開いた後で、前の読み込みが終わる
        check(loader.Started && latest is { Mode: SortMode.Size } && latest.Folders[0].Name == "B", "読み込み: 最後に頼んだフォルダの結果が返る");
        check(slow.GetAwaiter().GetResult() == null, "読み込み: 後から別のフォルダを開いていたら、遅れて読み終えた古い結果は返さない");
        bool staleThrew = false;
        try { staleThrew = broken.GetAwaiter().GetResult() != null; } catch (IOException) { staleThrew = true; }
        check(!staleThrew, "読み込み: 古い読み込みの失敗も知らせない（エラーを出さない）");
        bool latestThrew = false;
        try { loader.LoadAsync("slow-broken", false, SortMode.Name).GetAwaiter().GetResult(); } catch (IOException) { latestThrew = true; }
        check(latestThrew, "読み込み: 最後に頼んだ読み込みの失敗は例外で知らせる");

        // ---- サブフォルダの一覧 ----
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "img10", "img2", "Album" }) Directory.CreateDirectory(Path.Combine(dir, name));
        var hidden = Directory.CreateDirectory(Path.Combine(dir, ".cache"));
        hidden.Attributes |= FileAttributes.Hidden;
        File.WriteAllText(Path.Combine(dir, "file.jpg"), "x");
        var subs = FolderListing.ListSubfolders(dir).Select(d => d.Name).ToArray();
        check(subs.SequenceEqual(new[] { "Album", "img2", "img10" }), $"サブフォルダは名前順・隠しフォルダとファイルは除く ({string.Join(",", subs)})");
        check(FolderListing.ListSubfolders(Path.Combine(dir, "nothing")).Count == 0, "存在しないフォルダは空（例外にしない）");

        // ---- 画像以外のファイル・隠しファイルの表示（設定） ----
        File.WriteAllText(Path.Combine(dir, "memo.txt"), "x");
        File.WriteAllText(Path.Combine(dir, "secret.txt"), "x");
        File.SetAttributes(Path.Combine(dir, "secret.txt"), FileAttributes.Hidden);
        File.WriteAllText(Path.Combine(dir, "desktop.ini"), "x");
        File.SetAttributes(Path.Combine(dir, "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
        using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(dir, "book.zip"), System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntry("001.jpg");
            zip.CreateEntry("readme.txt");
        }
        string[] Files() => FolderListing.ListFiles(dir).Select(f => f.Name).ToArray();
        string[] InZip() => ImageViewer.Core.Archives.ZipStore.List(Path.Combine(dir, "book.zip"), "").Images.Select(f => f.Name).ToArray();
        try
        {
            check(Files().SequenceEqual(new[] { "file.jpg" }) && InZip().SequenceEqual(new[] { "001.jpg" }), "一覧: いつもは画像だけ（ZIP の中も）");
            FolderListing.ShowOtherFiles = true;
            var all = FolderListing.ListFiles(dir);
            check(all.Select(f => f.Name).SequenceEqual(new[] { "file.jpg", "memo.txt" }) && all[0].IsImage && !all[1].IsImage,
                $"一覧: 画像以外のファイルも出す設定（隠しファイル・ZIP は出さない。画像かどうかを見分ける） ({string.Join(",", Files())})");
            check(InZip().OrderBy(n => n).SequenceEqual(new[] { "001.jpg", "readme.txt" }), "一覧: ZIP の中も画像以外のファイルを出す");
            check(ImageViewer.App.Filer.FolderWatcher.Relevant("memo.txt") && !ImageViewer.App.Filer.FolderWatcher.Relevant("a.jpg.0123abcd.tmp"),
                "見張り: 画像以外のファイルも出す設定なら、その変化でも読み直す（一時ファイルは除く）");
            FolderListing.ShowHidden = true;
            check(Files().SequenceEqual(new[] { "file.jpg", "memo.txt", "secret.txt" }), $"一覧: 隠しファイルも出す設定（システムファイルは出さない） ({string.Join(",", Files())})");
            check(FolderListing.ListSubfolders(dir).Any(d => d.Name == ".cache"), "一覧: 隠しフォルダも出す設定");
            FolderListing.ShowOtherFiles = false;
            check(Files().SequenceEqual(new[] { "file.jpg" }), "一覧: 隠しファイルの設定だけでは、画像以外のファイルは出さない");
        }
        finally
        {
            FolderListing.ShowOtherFiles = FolderListing.ShowHidden = false;
            ImageViewer.Core.Archives.ZipStore.CloseAll();
        }

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

        // ---- 設定（ホームフォルダ） ----
        string settingsPath = Path.Combine(dir, "conf", "settings.json");
        var store = new ImageViewer.Core.Settings.SettingsStore(settingsPath);
        check(store.Load().HomeFolder == null, "設定ファイルが無ければ初期値（ホーム未設定）");
        store.Save(new ImageViewer.Core.Settings.AppSettings { HomeFolder = @"D:\写真" });
        check(store.Load().HomeFolder == @"D:\写真" && !File.Exists(settingsPath + ".tmp"), "保存して読める（一時ファイルは残らない）");
        store.Save(new ImageViewer.Core.Settings.AppSettings { HomeFolder = @"E:\" });
        check(store.Load().HomeFolder == @"E:\", "上書き保存");
        check(store.Load().OpenLastFolder == null && store.Load().LastFolder == null, "前回のフォルダを開く設定は既定でオフ");
        store.Save(new ImageViewer.Core.Settings.AppSettings { OpenLastFolder = true, LastFolder = @"D:\写真\2024" });
        check(store.Load() is { OpenLastFolder: true, LastFolder: @"D:\写真\2024" }, "前回のフォルダとその設定を保存して読める");
        File.WriteAllText(settingsPath, "{ 壊れた");
        check(store.Load().HomeFolder == null, "壊れた設定ファイルは初期値で読む（起動できなくならない）");

        // ---- フォルダー名の変更に合わせたパスの付け替え ----
        check(FolderListing.Retarget(@"C:\写真", @"c:\写真\", @"C:\画像") == @"C:\画像"
              && FolderListing.Retarget(@"C:\写真\2024\旅行", @"C:\写真", @"C:\画像\") == @"C:\画像\2024\旅行",
            "付け替え: 自分自身と中（大文字小文字・末尾の区切りは無視）");
        check(FolderListing.Retarget(@"C:\写真2", @"C:\写真", @"C:\画像") == null && FolderListing.Retarget(@"D:\x", @"C:\写真", @"C:\画像") == null,
            "付け替え: 名前の前方一致だけのもの・関係ないものは null");

        // ---- 移動 / コピー先の判定 ----
        check(FolderListing.IsDirectlyIn(@"C:\写真\a.jpg", @"c:\写真\") && !FolderListing.IsDirectlyIn(@"C:\写真\sub\a.jpg", @"C:\写真"),
            "直下にあるか（大文字小文字・末尾の区切りは無視、孫は含まない）");
        check(FolderListing.IsDirectlyIn(@"C:\a.jpg", @"C:\"), "ドライブのルート直下");
        check(FolderListing.IsSameOrInside(@"C:\写真\sub", @"C:\写真") && FolderListing.IsSameOrInside(@"C:\写真", @"C:\写真\")
              && !FolderListing.IsSameOrInside(@"C:\写真2", @"C:\写真"), "自分自身・自分の中か（名前の前方一致だけでは中と見なさない）");
        check(FolderListing.SameVolume(@"C:\a\b.jpg", @"c:\x") && !FolderListing.SameVolume(@"C:\a.jpg", @"D:\x"), "同じドライブか");
        check(FolderListing.LooksLikePath(@"C:\x") && FolderListing.LooksLikePath(@"\\server\share") && !FolderListing.LooksLikePath("旅行"),
            "パスらしい入力か");

        // ---- 最近の移動先 ----
        var recent = new ImageViewer.Core.Settings.AppSettings();
        for (int i = 0; i < 12; i++) recent = recent.WithRecentDestination($@"C:\f{i}");
        recent = recent.WithRecentDestination(@"c:\F5");
        check(recent.RecentDestinations!.Count == ImageViewer.Core.Settings.AppSettings.MaxRecentDestinations
              && recent.RecentDestinations[0] == @"c:\F5" && recent.RecentDestinations.Count(f => f.Equals(@"C:\f5", StringComparison.OrdinalIgnoreCase)) == 1
              && recent.RecentDestinations[1] == @"C:\f11",
            "最近の移動先: 新しい順・同じものは 1 つ・上限 10 件");
    }
}
