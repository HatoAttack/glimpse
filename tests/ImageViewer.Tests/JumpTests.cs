// フォルダジャンプ（索引・一致の点数・開いた記録・検索）の動作確認
using ImageViewer.Core.Jump;

static class JumpTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        // ---- 一致の点数 ----
        check(FolderMatcher.ScoreName("旅行", "旅行") > FolderMatcher.ScoreName("旅行写真", "旅行"), "完全一致 > 先頭一致");
        check(FolderMatcher.ScoreName("旅行写真", "旅行") > FolderMatcher.ScoreName("2024_旅行", "旅行"), "先頭一致 > 単語の先頭");
        check(FolderMatcher.ScoreName("2024_旅行", "旅行") > FolderMatcher.ScoreName("国内旅行", "旅行"), "単語の先頭 > 途中");
        check(FolderMatcher.ScoreName("MyPhotos", "photos") > FolderMatcher.ScoreName("myphotos", "photos"), "大文字の切り替わりも単語の先頭");
        check(FolderMatcher.ScoreName("img_trip", "imgtrp") is >= 200 and < 300, "あいまい一致（文字が順番どおり）");
        check(FolderMatcher.ScoreName("image-sizechange", "imgszch") is >= 200 and < 300
              && FolderMatcher.ScoreName("Anime_Streaming_Calendar", "img") == FolderMatcher.NoMatch,
            "あいまい一致は離れすぎたものを除く（img → Anime_Streaming_Calendar は不一致）");
        check(FolderMatcher.ScoreName("img_trip", "pmi") == FolderMatcher.NoMatch && FolderMatcher.ScoreName("abc", "x") == FolderMatcher.NoMatch,
            "順番が違う・含まれないものは不一致");
        check(FolderMatcher.PathContainsOthers(@"C:\写真\2024\旅行", new[] { "2024", "旅行" })
              && !FolderMatcher.PathContainsOthers(@"C:\写真\2023\旅行", new[] { "2024", "旅行" }), "最後以外の語はパスで絞り込む");

        // ---- 索引の作成 ----
        Directory.CreateDirectory(dir);
        string root = Path.Combine(dir, "home");
        foreach (var d in new[] { @"写真\2024\旅行", @"写真\2023\旅行", @"写真\2024\家族", @"仕事\資料", @"dev\node_modules\pkg", @"dev\.venv\旅行", @"img_trip" })
            Directory.CreateDirectory(Path.Combine(root, d));
        var hidden = Directory.CreateDirectory(Path.Combine(root, ".secret"));
        hidden.Attributes |= FileAttributes.Hidden;
        Directory.CreateDirectory(Path.Combine(root, ".secret", "旅行"));
        // ジャンクション（ループの原因になるので、たどらないこと）
        // シンボリックリンクは管理者権限が要るので、権限不要のジャンクション（mklink /J）で作る
        var link = Path.Combine(root, "loop");
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{root}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
            p!.WaitForExit();
        check(Directory.Exists(link), "（準備）ジャンクションを作れた");

        var index = FolderIndex.Build(new[] { root });
        var all = Enumerable.Range(0, index.Count).Select(index.FullPath).ToList();
        check(all.Contains(Path.Combine(root, @"写真\2024\旅行")) && all.Contains(Path.Combine(root, "img_trip")), $"索引を作る（{index.Count} 件）");
        check(!all.Any(p => p.Contains("node_modules")) && !all.Any(p => p.Contains(".secret")) && !all.Any(p => p.Contains("loop"))
              && !all.Any(p => p.Contains(".venv")),
            "node_modules・隠しフォルダ・「.」で始まるフォルダ・リンクは除く");
        var small = FolderIndex.Build(new[] { root }, limit: 4);
        check(small.Count == 4 && small.Truncated && Enumerable.Range(0, 4).All(i => small.DepthAt(i) <= 1), "上限で打ち切る（浅い階層が優先）");

        // ---- 保存・読み込み ----
        string indexPath = Path.Combine(dir, "data", "folder-index.bin");
        index.Save(indexPath);
        var loaded = FolderIndex.Load(indexPath);
        check(loaded != null && loaded.Count == index.Count
              && Enumerable.Range(0, index.Count).All(i => loaded.FullPath(i) == index.FullPath(i)), "保存して同じ内容で読める");
        var bytes = File.ReadAllBytes(indexPath);
        File.WriteAllBytes(indexPath, bytes[..(bytes.Length / 2)]);
        check(FolderIndex.Load(indexPath) == null, "途中で切れた索引は読まない（作り直させる）");
        File.WriteAllText(indexPath, "garbage");
        check(FolderIndex.Load(indexPath) == null && FolderIndex.Load(Path.Combine(dir, "none.bin")) == null, "壊れた・無い索引は null");

        // ---- 開いた記録 ----
        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var visits = new VisitHistory(limit: 3);
        visits.Record(@"C:\a", t0);
        visits.Record(@"C:\a", t0);
        visits.Record(@"C:\b", t0.AddDays(-40));
        check(visits.FrecencyOf(@"C:\A", t0) > visits.FrecencyOf(@"C:\b", t0) && visits.FrecencyOf(@"C:\zzz", t0) == 0,
            "よく開く・最近開いたものほど高い（大文字小文字は同一視）");
        visits.Record(@"C:\c", t0);
        visits.Record(@"C:\d", t0);
        check(visits.Count == 3 && visits.FrecencyOf(@"C:\b", t0) == 0, "上限を超えたら点数の低いものから捨てる");
        string visitsPath = Path.Combine(dir, "data", "visits.json");
        visits.Save(visitsPath);
        check(VisitHistory.Load(visitsPath).Count == 3, "記録を保存して読める");
        File.WriteAllText(visitsPath, "[ 壊れた");
        check(VisitHistory.Load(visitsPath).Count == 0, "壊れた記録は空から始める");

        // ---- 検索 ----
        var service = new FolderJumpService(Path.Combine(dir, "svc"));
        var start = service.StartAsync(new[] { root });
        start.Wait();
        SpinWait.SpinUntil(() => !service.IsBuilding && service.Index != null, 10000);
        check(service.Index != null, "サービスが索引を作る（保存が無いとき）");

        // 最初のビルド完了時に次の指定を入れ、最後の roots が使われることを確認する
        string nextRoot = Path.Combine(dir, "next");
        string latestRoot = Path.Combine(dir, "latest");
        Directory.CreateDirectory(nextRoot);
        Directory.CreateDirectory(latestRoot);
        var rebuild = new FolderJumpService(Path.Combine(dir, "rebuild"));
        using var firstBuilt = new ManualResetEventSlim();
        using var continueBuild = new ManualResetEventSlim();
        int changes = 0;
        rebuild.IndexChanged += () =>
        {
            if (Interlocked.Increment(ref changes) != 1) return;
            firstBuilt.Set();
            continueBuild.Wait();
        };
        rebuild.Rebuild(new[] { root });
        bool firstCompleted = firstBuilt.Wait(10000);
        if (firstCompleted)
        {
            rebuild.Rebuild(new[] { nextRoot });
            rebuild.Rebuild(new[] { latestRoot });
        }
        continueBuild.Set();
        bool latestCompleted = SpinWait.SpinUntil(() => !rebuild.IsBuilding, 10000);
        check(firstCompleted && latestCompleted && rebuild.Index?.Roots.SequenceEqual(new[] { Path.GetFullPath(latestRoot) }) == true
              && Volatile.Read(ref changes) == 2, "ビルド中の再指定は最新 roots で再構築する");

        var r = service.Search("旅行");
        check(r.Count == 2 && r.All(x => x.Name == "旅行"), $"名前で探す（{string.Join(" / ", r.Select(x => x.Path))}）");
        r = service.Search("2024 旅行");
        check(r.Count == 1 && r[0].Path.EndsWith(@"2024\旅行"), "スペース区切りでパスを絞り込む");
        r = service.Search("imgtrp");
        check(r.Count == 1 && r[0].Name == "img_trip", "あいまい一致");
        check(service.Search("   ").Count == 0 && service.Search("存在しない名前").Count == 0, "空・一致なしは 0 件");

        string trip2023 = Path.Combine(root, @"写真\2023\旅行");
        for (int i = 0; i < 5; i++) service.RecordVisit(trip2023);
        r = service.Search("旅行");
        check(r[0].Path == trip2023, "よく開くフォルダが上に来る");
        service.RecordVisit(@"\\server\share\旅行アルバム");
        check(service.Search("旅行アルバム").Any(x => x.Path.StartsWith(@"\\server")), "索引に無くても開いた記録にあれば候補に出る（ネットワーク等）");

        r = service.Search("資料", extra: new[] { @"D:\other\資料", @"D:\other\関係ない" });
        check(r.Any(x => x.Path == @"D:\other\資料") && r.All(x => !x.Path.StartsWith(root)), "外部の結果（Everything）を渡したときはそれを使う");

        Directory.Delete(link);
    }
}
