// 名前の変更（計画・検査・2 段階の実行・失敗時の巻き戻し・元に戻す）の動作確認
using ImageViewer.App.Grid;
using ImageViewer.Core.Rename;

static class RenameTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        Directory.CreateDirectory(dir);
        string P(string name) => Path.Combine(dir, name);
        void Make(params string[] names)
        {
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            foreach (var n in names) File.WriteAllText(P(n), n); // 中身 = 元の名前（どれがどこへ行ったか追える）
        }
        string Content(string name) => File.ReadAllText(P(name));

        // ---- 名前の決め方 ----
        var seq = new RenameOptions { Pattern = "trip_###", Start = 8, Step = 2 };
        check(RenamePlanner.NewName("IMG.JPG", 0, seq) == "trip_008.JPG" && RenamePlanner.NewName("x.png", 2, seq) == "trip_012.png",
            "# を連番に（開始・# の数が桁数・増分、拡張子は残す）");
        var snap = new RenameOptions { Pattern = "snap##_#" };
        check(RenamePlanner.NewName("a.jpg", 0, snap) == "snap01_#.jpg" && RenamePlanner.NewName("a.jpg", 99, snap) == "snap100_#.jpg",
            "既定は 1 から / 最初の # の並びだけが番号 / 桁が足りなければ増える");
        check(RenamePlanner.NewName("IMG.JPG", 0, seq with { LowercaseExtension = true }) == "trip_008.jpg", "拡張子だけ小文字");
        var keep = new RenameOptions { UseSequence = false, ReplaceSearch = "DSC", ReplaceWith = "photo", Suffix = "_s", Lowercase = true };
        check(RenamePlanner.NewName("DSC0001.JPG", 0, keep) == "photo0001_s.jpg", "元の名前を元に置換＋末尾＋すべて小文字");

        check(RenamePlanner.GuessPattern("a260019.jpg") == "a######" && RenamePlanner.GuessPattern("sunset.png") == "sunset_###",
            "名前欄の初期値: 末尾の数字を # に / 数字が無ければ 名前_###");

        // ---- 検査 ----
        Make("a.jpg", "b.jpg", "other.jpg");
        var plan = RenamePlanner.Plan(new[] { P("a.jpg"), P("b.jpg") },
            new RenameOptions { UseSequence = false, ReplaceSearch = "a", ReplaceWith = "other" });
        check(plan[0].Status == RenameStatus.Error && plan[0].Error!.Contains("すでに") && plan[1].Status == RenameStatus.Unchanged,
            "対象外の既存ファイルと同名はエラー / 変わらないものは変更なし");
        plan = RenamePlanner.Plan(new[] { P("a.jpg"), P("b.jpg") }, new RenameOptions { UseSequence = false, Suffix = "", ReplaceSearch = "b", ReplaceWith = "A" });
        check(plan.All(p => p.Status == RenameStatus.Error && p.Error!.Contains("重複")), "変更後の名前どうしの重複（大文字小文字無視）はエラー");
        check(RenamePlanner.ValidateName("a?.jpg") != null && RenamePlanner.ValidateName("CON.jpg") != null
              && RenamePlanner.ValidateName("x .jpg") == null && RenamePlanner.ValidateName("x.") != null && RenamePlanner.ValidateName(".jpg") != null,
            "使えない文字・予約名・末尾の . ・空の名前");
        plan = RenamePlanner.Plan(new[] { P("a.jpg") }, new RenameOptions { Pattern = @"..\moved_#" });
        check(plan[0].Status == RenameStatus.Error, "\\ を含む名前はエラー（別のフォルダへ移さない）");
        plan = RenamePlanner.Plan(new[] { P("a.jpg") }, new RenameOptions { Pattern = "snap" });
        check(plan[0].Status == RenameStatus.Error && plan[0].Error!.Contains('#'), "# が無ければエラー");

        // ---- 実行: 入れ替え ----
        Make("a.jpg", "b.jpg");
        RenameExecutor.Execute(new[] { new RenameOp(P("a.jpg"), P("b.jpg")), new RenameOp(P("b.jpg"), P("a.jpg")) });
        check(Content("a.jpg") == "b.jpg" && Content("b.jpg") == "a.jpg" && Directory.GetFiles(dir).Length == 2, "名前の入れ替え（a↔b）");

        // ---- 実行: 並べ替えてから同じ名前で振り直す（a260000〜a260004 を 4,0,3,1,2 の順に） ----
        var names = Enumerable.Range(0, 5).Select(i => $"a26000{i}.jpg").ToArray();
        Make(names);
        var order = new[] { 4, 0, 3, 1, 2 }.Select(i => P(names[i])).ToList();
        plan = RenamePlanner.Plan(order, new RenameOptions { Pattern = RenamePlanner.GuessPattern(order[0]), Start = 260000 });
        check(plan.All(p => p.Status != RenameStatus.Error), "振り直しの計画にエラーが無い（対象どうしの名前の重なりは可）");
        var done = RenameExecutor.Execute(plan.Where(p => p.Status == RenameStatus.Ok).Select(p => new RenameOp(p.Source, p.Target)));
        check(Enumerable.Range(0, 5).Select(i => Content(names[i])).SequenceEqual(order.Select(Path.GetFileName)!)
              && Directory.GetFiles(dir).Length == 5,
            $"並べた順に a260000〜 を振り直し（{string.Join(",", Enumerable.Range(0, 5).Select(i => Content(names[i])))}）");

        // ---- 元に戻す ----
        RenameExecutor.Execute(done.Reverse().Select(o => new RenameOp(o.To, o.From)));
        check(names.All(n => Content(n) == n), "元に戻す");

        // ---- 失敗時の巻き戻し: 途中のファイルを開いたままにして移動できなくする ----
        Make("a.jpg", "b.jpg", "c.jpg");
        bool threw;
        using (File.Open(P("c.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                RenameExecutor.Execute(new[]
                {
                    new RenameOp(P("a.jpg"), P("b.jpg")), new RenameOp(P("b.jpg"), P("a.jpg")), new RenameOp(P("c.jpg"), P("d.jpg")),
                });
                threw = false;
            }
            catch (IOException ex)
            {
                threw = ex.Message.Contains("すべて元に戻しました");
            }
        }
        check(threw && Content("a.jpg") == "a.jpg" && Content("b.jpg") == "b.jpg" && Content("c.jpg") == "c.jpg"
              && Directory.GetFiles(dir).Length == 3, "途中で失敗したらすべて元の名前に戻す（一時ファイルも残さない）");

        // ---- チェックの付け替え ----
        var marks = new MarkSet();
        marks.Set(new[] { P("a.jpg"), P("c.jpg") }, true);
        marks.Remap(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [P("a.jpg")] = P("b.jpg"), [P("b.jpg")] = P("a.jpg") });
        check(marks.IsMarked(P("b.jpg")) && marks.IsMarked(P("c.jpg")) && !marks.IsMarked(P("a.jpg")), "チェックは名前の変更に付いていく（入れ替えも）");
    }
}
