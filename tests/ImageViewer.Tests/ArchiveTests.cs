// ZIP の中を見る（パスの判定・一覧・日本語の名前・読み込み・書き出し・書き換えへの追従）の動作確認
using System.IO.Compression;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ImageViewer.App.Commands;
using ImageViewer.App.Filer;
using ImageViewer.Core.Archives;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Navigation;
using ImageViewer.Core.Thumbnails;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

static class ArchiveTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] Jpeg(int w, int h)
        {
            using var img = new Image<Rgba32>(w, h, new Rgba32(255, 0, 0));
            using var ms = new MemoryStream();
            img.SaveAsJpeg(ms);
            return ms.ToArray();
        }
        byte[] Png(int w, int h)
        {
            using var img = new Image<Rgba32>(w, h, new Rgba32(0, 0, 255));
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            return ms.ToArray();
        }
        byte[] Gif()
        {
            using var img = new Image<Rgba32>(20, 10, new Rgba32(255, 0, 0));
            using (var frame = new Image<Rgba32>(20, 10, new Rgba32(0, 0, 255))) img.Frames.AddFrame(frame.Frames.RootFrame);
            using var ms = new MemoryStream();
            img.SaveAsGif(ms);
            return ms.ToArray();
        }
        void Write(string zipPath, Encoding? encoding, params (string Name, byte[]? Data)[] entries)
        {
            using var fs = File.Create(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create, false, encoding);
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name);
                if (data == null) continue; // フォルダの項目（名前が / で終わる）
                using var s = entry.Open();
                s.Write(data);
            }
        }

        // ---- 日本語の Windows で作った ZIP（名前が Shift_JIS で、UTF-8 の印が無い） ----
        string zip = Path.Combine(dir, "book.zip");
        Write(zip, Encoding.GetEncoding(932),
            ("001.jpg", Jpeg(400, 200)),
            ("sub/002.png", Png(30, 60)),
            ("日本語/画像.jpg", Jpeg(10, 10)),
            ("empty/", null),
            ("__MACOSX/._001.jpg", new byte[] { 0, 1, 2 }),
            ("._hidden.jpg", new byte[] { 0, 1, 2 }),
            ("readme.txt", Encoding.UTF8.GetBytes("hi")),
            ("anim.gif", Gif()),
            ("inner.zip", new byte[] { 0 }));

        // ---- パスの判定 ----
        string Z(string inner) => ArchivePath.Combine(zip, inner.Replace('/', '\\'));
        check(ArchivePath.TrySplit(Z("sub/002.png"), out var a, out var i) && a == zip && i == @"sub\002.png", $"ZIP の中のパスを分ける（{a} | {i}）");
        check(ArchivePath.TrySplit(zip, out a, out i) && a == zip && i == "", "ZIP そのものは中のパスが空");
        check(ArchivePath.IsInside(Z("001.jpg")) && !ArchivePath.IsInside(zip) && !ArchivePath.IsInside(Path.Combine(dir, "a.jpg")),
            "ZIP の中の項目か（ZIP そのもの・普通のファイルは違う）");
        string fakeZip = Path.Combine(dir, "folder.zip");
        Directory.CreateDirectory(fakeZip);
        check(!ArchivePath.IsArchiveFolder(Path.Combine(fakeZip, "a.jpg")), ".zip という名前のフォルダは ZIP ではない");
        check(!ArchivePath.IsArchiveFolder(Path.Combine(dir, "missing.zip", "a.jpg")), "無い ZIP は ZIP ではない");

        // ---- 一覧 ----
        var root = ZipStore.List(zip, "");
        var rootFolders = root.Folders.Select(Path.GetFileName).OrderBy(n => n).ToArray();
        check(rootFolders.SequenceEqual(new[] { "empty", "sub", "日本語" }.OrderBy(n => n)),
            $"一番上のフォルダ（__MACOSX は出さない。Shift_JIS の名前も読める）: {string.Join(",", rootFolders)}");
        var rootImages = root.Images.Select(f => f.Name).OrderBy(n => n).ToArray();
        check(rootImages.SequenceEqual(new[] { "001.jpg", "anim.gif" }),
            $"一番上の画像（txt・ZIP の中の ZIP・._ で始まるものは出さない）: {string.Join(",", rootImages)}");
        var first = root.Images.First(f => f.Name == "001.jpg");
        check(first.FullName == Z("001.jpg") && first.Length > 0 && first.LastWriteTimeUtc.Year >= 2020, "画像のパス・大きさ・更新日時");
        check(ZipStore.List(zip, "sub").Images.Select(f => f.Name).SequenceEqual(new[] { "002.png" }), "中のフォルダの画像");
        check(ZipStore.List(zip, "日本語").Images.Select(f => f.Name).SequenceEqual(new[] { "画像.jpg" }), "日本語のフォルダの画像");
        check(ZipStore.List(zip, "empty").Images.Count == 0, "空のフォルダ（フォルダの項目だけ）");
        bool notFound = false;
        try { ZipStore.List(zip, "nope"); } catch (DirectoryNotFoundException) { notFound = true; }
        check(notFound, "無いフォルダは例外");

        // ---- フォルダとして開けるか・アドレスバー ----
        check(FolderListing.CanOpen(zip) && FolderListing.CanOpen(Z("日本語")) && !FolderListing.CanOpen(Z("nope")) && !FolderListing.CanOpen(Z("001.jpg")),
            "開けるのは ZIP・ZIP の中のフォルダだけ");
        check(FolderListing.Normalize($"  \"{Z("sub")}\" ") == Z("sub"), "アドレスバーに ZIP の中のパスを入れられる");
        check(FolderListing.Parent(Z("sub")) == zip && FolderListing.Parent(zip) == dir, "上のフォルダ: ZIP の中 → ZIP → ZIP のあるフォルダ");
        File.WriteAllBytes(Path.Combine(dir, "comic.CBZ"), File.ReadAllBytes(zip));
        File.WriteAllBytes(Path.Combine(dir, "photo.jpg"), Jpeg(10, 10));
        var archives = FolderListing.ListArchives(dir).Select(d => d.Name).ToArray();
        check(archives.SequenceEqual(new[] { "book.zip", "comic.CBZ" }), $"フォルダの中の ZIP（CBZ も。フォルダ folder.zip は入らない）: {string.Join(",", archives)}");
        check(FolderListing.ListArchives(dir).All(FolderListing.IsArchiveTile)
              && !FolderListing.ListSubfolders(dir).Any(FolderListing.IsArchiveTile), "ZIP のタイルか（.zip という名前のフォルダは普通のフォルダのタイル）");
        check(FolderListing.IsFolderOrArchive(zip) && FolderListing.IsFolderOrArchive(dir) && !FolderListing.IsFolderOrArchive(Path.Combine(dir, "photo.jpg")),
            "ドロップで開けるのはフォルダと ZIP");
        check(FolderWatcher.Relevant("new.zip") && FolderWatcher.Relevant("x.cbz"), "見張り: ZIP の追加・削除でも読み直す");

        // ---- 読み込み ----
        using (var img = ImageLoader.Load(Z("001.jpg"), LoadOptions.Thumbnail(100)))
            check(img.Width == 100 && img.Height == 50, $"ZIP の中の JPEG を縮小して読む（{img.Width}x{img.Height}）");
        check(ImageLoader.Identify(Z("sub/002.png")) is { Width: 30, Height: 60, Format: "PNG" }, "ZIP の中の PNG のヘッダー");
        check(ImageLoader.Identify(Z("日本語/画像.jpg")) is { Width: 10 }, "日本語の名前の画像");
        check(ImageLoader.Identify(Z("missing.jpg")) == null, "ZIP の中に無い画像は null");
        using (var bmp = ThumbnailGenerator.Generate(Z("001.jpg"), 64))
            check(bmp.Width == 64 && bmp.Height == 32, "ZIP の中の画像のサムネイル");
        check(AnimationLoader.FrameCount(Z("anim.gif")) == 2, "ZIP の中のアニメ GIF のコマ数");
        using (var anim = AnimationLoader.Load(Z("anim.gif"), 100))
            check(anim?.Count == 2, "ZIP の中のアニメ GIF を再生用に読む");
        using (var frame = AnimationLoader.LoadFrame(Z("anim.gif"), 1))
            check(frame[0, 0].B > 200, "ZIP の中のアニメ GIF の 2 コマ目");

        // ---- UTF-8 の名前（印あり） ----
        string utf8 = Path.Combine(dir, "utf8.zip");
        Write(utf8, null, ("写真/夏.png", Png(5, 5)), ("한국어.png", Png(5, 5)));
        check(ZipStore.List(utf8, "").Images.Select(f => f.Name).SequenceEqual(new[] { "한국어.png" })
              && ZipStore.List(utf8, "写真").Images.Select(f => f.Name).SequenceEqual(new[] { "夏.png" }), "UTF-8 の名前（Shift_JIS にない文字も）");

        // ---- 書き出し（コピー・ドラッグ用） ----
        ArchiveExport.Root = Path.Combine(dir, "export");
        var exported = ArchiveExport.ToFiles(new[] { Z("001.jpg"), Z("sub/002.png"), Path.Combine(dir, "photo.jpg") });
        check(exported.Count == 3 && Path.GetFileName(exported[0]) == "001.jpg"
              && File.ReadAllBytes(exported[0]).SequenceEqual(ZipStore.Read(Z("001.jpg")))
              && exported[2] == Path.Combine(dir, "photo.jpg"),
            "ZIP の中の画像は一時フォルダへ書き出し、普通のファイルはそのまま");
        check(ImageLoader.Identify(exported[1]) is { Width: 30, Height: 60 }, "書き出した画像は元のまま");
        Write(Path.Combine(dir, "dup.zip"), null, ("a/x.png", Png(3, 3)), ("b/x.png", Png(4, 4)));
        var dup = ArchiveExport.ToFiles(new[] { ArchivePath.Combine(Path.Combine(dir, "dup.zip"), @"a\x.png"), ArchivePath.Combine(Path.Combine(dir, "dup.zip"), @"b\x.png") });
        check(dup.Select(Path.GetFileName).SequenceEqual(new[] { "x.png", "x (2).png" }), "同じ名前が重なったら (2) を付ける");
        check(ArchiveExport.SafeFileName("photo?.jpg") == "photo_.jpg" && ArchiveExport.SafeFileName("a:b*.png") == "a_b_.png"
              && ArchiveExport.SafeFileName("CON.jpg") == "_CON.jpg" && ArchiveExport.SafeFileName("x.png. ") == "x.png"
              && ArchiveExport.SafeFileName("普通.jpg") == "普通.jpg", "書き出す名前を Windows で使える名前にする（? : * ・予約名・末尾の .）");
        check(ArchiveExport.SafeFileName("CON.preview.jpg") == "_CON.preview.jpg" && ArchiveExport.SafeFileName("com¹.png") == "_com¹.png"
              && ArchiveExport.SafeFileName("LPT0.x.png") == "_LPT0.x.png" && ArchiveExport.SafeFileName("CONSOLE.jpg") == "CONSOLE.jpg",
            "予約名は最初の . より前で見る（CON.preview.jpg・COM¹・LPT0。CONSOLE は違う）");
        string odd = Path.Combine(dir, "odd.zip");
        Write(odd, null, ("what?.png", Png(3, 3)), ("NUL.png", Png(3, 3)));
        var oddOut = ArchiveExport.ToFiles(new[] { ArchivePath.Combine(odd, "what?.png"), ArchivePath.Combine(odd, "NUL.png") });
        check(oddOut.Select(Path.GetFileName).SequenceEqual(new[] { "what_.png", "_NUL.png" }) && oddOut.All(File.Exists),
            "Windows で使えない名前の画像も書き出せる");
        var plain = new[] { Path.Combine(dir, "photo.jpg") };
        check(ReferenceEquals(ArchiveExport.ToFiles(plain), plain), "ZIP の中が無ければ書き出さない");

        // ---- ドラッグ: 相手が求めたときだけ書き出す ----
        string lazyRoot = ArchiveExport.Root = Path.Combine(dir, "export-lazy");
        var data = new ArchiveDragData(new[] { Z("001.jpg") });
        check(!Directory.Exists(lazyRoot) && data.GetDataPresent(DataFormats.FileDrop), "ドラッグを始めただけでは書き出さない（形式は出ている）");
        // エクスプローラーと同じく OLE で取り出す
        var format = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        ((System.Runtime.InteropServices.ComTypes.IDataObject)data).GetData(ref format, out var medium);
        var dropped = new StringBuilder(260);
        int count = DragQueryFile(medium.unionmember, 0xFFFFFFFF, null, 0);
        DragQueryFile(medium.unionmember, 0, dropped, dropped.Capacity);
        ReleaseStgMedium(ref medium);
        check(count == 1 && dropped.ToString().StartsWith(lazyRoot) && File.Exists(dropped.ToString()),
            $"落とす先が求めたら書き出したパスを渡す（{dropped}）");

        // ---- 開いている間も ZIP の名前を変えられる・書き換えに追従する ----
        string moved = Path.Combine(dir, "book2.zip");
        ZipStore.List(zip, ""); // 開いておく
        bool movable = true;
        try { File.Move(zip, moved); File.Move(moved, zip); } catch (IOException) { movable = false; }
        check(movable, "開いている ZIP でもほかのアプリが名前を変えられる");
        bool writable = true;
        ZipStore.IdleClose = TimeSpan.FromMilliseconds(200);
        ImageLoader.Identify(Z("001.jpg")); // 読んで、ファイルを開いた状態にする
        Thread.Sleep(700);
        try { Write(zip, null, ("new.png", Png(7, 7))); } catch (IOException) { writable = false; }
        check(writable, "しばらく読まなければファイルを閉じる（ほかのアプリが ZIP に書き込める）");
        File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddMinutes(1));
        check(ZipStore.List(zip, "").Images.Select(f => f.Name).SequenceEqual(new[] { "new.png" }) && ImageLoader.Identify(Z("new.png")) is { Width: 7 },
            "ZIP が書き換えられたら開き直す");

        // ---- 名前・大きさ・日時が同じでも、中身が変われば別物 ----
        string same = Path.Combine(dir, "same.zip");
        var stamp = new DateTimeOffset(2024, 1, 2, 3, 4, 6, TimeSpan.Zero);
        void WriteStamped(byte fill)
        {
            using var fs = File.Create(same);
            using var z = new ZipArchive(fs, ZipArchiveMode.Create);
            var e = z.CreateEntry("v.jpg", CompressionLevel.NoCompression);
            e.LastWriteTime = stamp;
            using var s = e.Open();
            s.Write(Enumerable.Repeat(fill, 100).ToArray());
        }
        WriteStamped(1);
        var before = ZipStore.List(same, "").Images.Single();
        Thread.Sleep(700); // 読み終わったファイルが閉じるのを待つ
        WriteStamped(2);
        File.SetLastWriteTimeUtc(same, DateTime.UtcNow.AddMinutes(2));
        var after = ZipStore.List(same, "").Images.Single();
        check(before.Length == after.Length && before.LastWriteTimeUtc == after.LastWriteTimeUtc && before.Version != after.Version
              && ThumbnailKey.From(before) != ThumbnailKey.From(after), "中のファイルの日時が同じでも、中身が変われば別物（サムネイルを作り直す）");
        // ZIP そのものの大きさ・更新日時も変えずに置き換えられた: 覚えている目次のままだが、読み直し（Forget）で追いつく
        var zipTime = File.GetLastWriteTimeUtc(same);
        Thread.Sleep(700);
        WriteStamped(3);
        File.SetLastWriteTimeUtc(same, zipTime);
        bool stale = ZipStore.List(same, "").Images.Single().Version == after.Version;
        ZipStore.Forget(same);
        var refreshed = ZipStore.List(same, "").Images.Single();
        check(stale && refreshed.Version != after.Version, "大きさも日時も同じまま置き換えられた ZIP も、読み直せば新しい中身");

        // ---- 壊れた ZIP ----
        string broken = Path.Combine(dir, "broken.zip");
        File.WriteAllBytes(broken, new byte[] { 1, 2, 3, 4, 5 });
        bool invalid = false;
        try { ZipStore.List(broken, ""); } catch (InvalidDataException) { invalid = true; }
        check(invalid && !FolderListing.CanOpen(broken) && ImageLoader.Identify(ArchivePath.Combine(broken, "a.jpg")) == null,
            "壊れた ZIP は開けない（例外は InvalidDataException、読み込みは null）");

        // ---- 片付け ----
        string oldDir = Path.Combine(lazyRoot, "old");
        Directory.CreateDirectory(oldDir);
        Directory.SetCreationTimeUtc(oldDir, DateTime.UtcNow.AddDays(-3));
        ArchiveExport.CleanUp(TimeSpan.FromDays(1));
        check(!Directory.Exists(oldDir) && Directory.Exists(Path.GetDirectoryName(dropped.ToString())), "古い書き出しだけを片付ける");
        ZipStore.CloseAll();
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int DragQueryFile(IntPtr hDrop, uint index, StringBuilder? file, int size);

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
