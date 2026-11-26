// 並び順（名前順の比較・手動の並びの適用と移動・保存・挿入位置）の動作確認
using ImageViewer.App.Grid;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Ordering;

static class OrderingTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        // ---- 名前順（エクスプローラーと同じ） ----
        var names = new[] { "img10.jpg", "img2.jpg", "IMG1.jpg", "a260000.jpg" }.ToList();
        names.Sort(FileSorting.NaturalNameComparer);
        check(names.SequenceEqual(new[] { "a260000.jpg", "IMG1.jpg", "img2.jpg", "img10.jpg" }), $"数字は数値として比較 ({string.Join(",", names)})");

        // ---- 名前・更新日時・サイズ順と、その向き ----
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var listed = new[]
        {
            new ImageFile(@"C:\x\img10.jpg", 300, t0.AddDays(1)),
            new ImageFile(@"C:\x\img2.jpg", 100, t0.AddDays(3)),
            new ImageFile(@"C:\x\b.jpg", 100, t0.AddDays(1)),
            new ImageFile(@"C:\x\a.jpg", 200, t0.AddDays(2)),
        };
        string Order(SortMode mode, bool descending) => string.Join(",", FileSorting.Sort(listed, mode, descending).Select(f => f.Name));
        check(Order(SortMode.Name, false) == "a.jpg,b.jpg,img2.jpg,img10.jpg", $"名前の昇順 ({Order(SortMode.Name, false)})");
        check(Order(SortMode.Name, true) == "img10.jpg,img2.jpg,b.jpg,a.jpg", $"名前の降順（数字は数値として） ({Order(SortMode.Name, true)})");
        check(Order(SortMode.Modified, false) == "b.jpg,img10.jpg,a.jpg,img2.jpg", $"更新日時の昇順（同じ日時は名前順） ({Order(SortMode.Modified, false)})");
        check(Order(SortMode.Modified, true) == "img2.jpg,a.jpg,img10.jpg,b.jpg", $"更新日時の降順（同じ日時も逆の名前順） ({Order(SortMode.Modified, true)})");
        check(Order(SortMode.Size, false) == "b.jpg,img2.jpg,a.jpg,img10.jpg", $"サイズの昇順 ({Order(SortMode.Size, false)})");
        check(Order(SortMode.Size, true) == "img10.jpg,a.jpg,img2.jpg,b.jpg", $"サイズの降順 ({Order(SortMode.Size, true)})");
        check(Order(SortMode.Manual, true) == Order(SortMode.Name, false), "手動（保存が無いとき）は向きを見ずに名前の昇順");

        check(SortPreference.From(null, null) == new SortPreference(SortMode.Name, false), "並び順の設定が無ければ名前の昇順");
        check(SortPreference.From(SortMode.Modified, true) == new SortPreference(SortMode.Modified, true), "並び順の設定を読む");
        check(SortPreference.From(SortMode.Manual, true).Mode == SortMode.Name && SortPreference.From((SortMode)99, null).Mode == SortMode.Name,
            "並び順の設定が手動・知らない値なら名前順");

        // ---- 手動の並びの適用 ----
        Directory.CreateDirectory(dir);
        ImageFile F(string name)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) File.WriteAllBytes(path, new byte[] { 0 });
            return ImageFile.From(new FileInfo(path));
        }
        var files = new[] { F("a.jpg"), F("b.jpg"), F("c.jpg"), F("d.jpg") };
        var applied = ManualOrder.Apply(files, new[] { "C.JPG", "x.jpg", "a.jpg", "c.jpg" });
        check(applied.Select(f => f.Name).SequenceEqual(new[] { "c.jpg", "a.jpg", "b.jpg", "d.jpg" }),
            $"保存した順 → 無いものは捨て → 新しいものは末尾 ({string.Join(",", applied.Select(f => f.Name))})");

        // ---- ドラッグでの移動 ----
        check(ManualOrder.Move(5, new[] { 3 }, 1).SequenceEqual(new[] { 0, 3, 1, 2, 4 }), "後ろのものを前へ");
        check(ManualOrder.Move(5, new[] { 0 }, 3).SequenceEqual(new[] { 1, 2, 0, 3, 4 }), "前のものを後ろへ（挿入位置は元の並び基準）");
        check(ManualOrder.Move(6, new[] { 1, 4 }, 6).SequenceEqual(new[] { 0, 2, 3, 5, 1, 4 }), "複数を末尾へ（順番は保つ）");
        check(ManualOrder.Move(6, new[] { 1, 4 }, 3).SequenceEqual(new[] { 0, 2, 1, 4, 3, 5 }), "複数を間へ（挿入位置をまたぐ）");
        check(ManualOrder.Move(4, new[] { 2 }, 2).SequenceEqual(new[] { 0, 1, 2, 3 }), "自分の直前に落としても変わらない");
        check(ManualOrder.Move(4, new[] { 2 }, 3).SequenceEqual(new[] { 0, 1, 2, 3 }), "自分の直後に落としても変わらない");

        // ---- 保存 ----
        string root = Path.Combine(dir, "store");
        string photos = Path.Combine(dir, "photos");
        Directory.CreateDirectory(photos);
        var store = new FolderOrderStore(root, maxEntries: 3);
        check(store.Load(photos) == null, "保存が無ければ null");
        store.Save(photos, new[] { "b.jpg", "a.jpg" });
        check(store.Load(photos)?.SequenceEqual(new[] { "b.jpg", "a.jpg" }) == true, "保存して読める");
        bool usesId = FolderOrderStore.FolderId(photos) != null;
        check(usesId && Directory.GetFiles(root).Single().Contains("id-"), "NTFS ではフォルダ ID で保存");

        string renamed = Path.Combine(dir, "photos_renamed");
        Directory.Move(photos, renamed);
        check(store.Load(renamed)?.SequenceEqual(new[] { "b.jpg", "a.jpg" }) == true, "フォルダ名を変えても並び順が残る");
        store.Delete(renamed);
        check(store.Load(renamed) == null && Directory.GetFiles(root).Length == 0, "削除");

        // 上限 3 件: 4 件目を保存すると一番古いものが消える
        var folders = Enumerable.Range(0, 4).Select(i => Directory.CreateDirectory(Path.Combine(dir, $"f{i}")).FullName).ToList();
        for (int i = 0; i < 4; i++)
        {
            store.Save(folders[i], new[] { $"{i}.jpg" });
            File.SetLastWriteTimeUtc(Directory.GetFiles(root).OrderBy(File.GetLastWriteTimeUtc).Last(), DateTime.UtcNow.AddMinutes(i - 10));
        }
        check(Directory.GetFiles(root).Length == 3 && store.Load(folders[0]) == null && store.Load(folders[3]) != null,
            "上限を超えたら古いものから消す");

        File.WriteAllText(Directory.GetFiles(root)[0], "{壊れた");
        check(folders.Skip(1).Count(f => store.Load(f) != null) == 2, "壊れた保存は無視（ほかは読める）");

        // ---- 挿入位置 ----
        // 3 列（セル 100、余白 10、左の余り 2px）: 列 0 の左端 x=12、境目 k の x = 2 + 10 + 110k - 5
        var l = new GridLayout(ItemCount: 8, ClientWidth: 345, CellWidth: 100, CellHeight: 120, Gap: 10);
        check(l.InsertionAt(20, 50, 2).Index == 0, "セルの左寄りなら前に入る");
        check(l.InsertionAt(100, 50, 2).Index == 1, "セルの右寄りなら後ろに入る");
        check(l.InsertionAt(1000, 50, 2).Index == 3, "行の右端より右は行末");
        var (idx, marker) = l.InsertionAt(120, 200, 2);
        check(idx == 4 && marker == new System.Drawing.Rectangle(116, 140, 2, 120), $"2 行目の境目と縦線の位置 ({idx}, {marker})");
        check(l.InsertionAt(340, 300, 2).Index == 8, "最終行（2 枚）の右は末尾");
        check(l.InsertionAt(10, 5000, 2).Index == 8, "最終行より下は末尾");
        check(new GridLayout(0, 345, 100, 120, 10).InsertionAt(10, 10, 2).Index == 0, "0 件なら 0");
    }
}
