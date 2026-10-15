// ファイルのコピー / 切り取りのクリップボード（エクスプローラーと同じ形式）の動作確認
// 実際のクリップボードを使うので、テストの間はクリップボードの中身が置き換わる
using System.Collections.Specialized;
using ImageViewer.App.Commands;

static class ClipboardTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Scenario(check, dir);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        check(error == null, $"クリップボード: 最後まで動かせる{(error == null ? "" : $"（{error}）")}");
    }

    static void Scenario(Action<bool, string> check, string dir)
    {
        string a = Path.Combine(dir, "clip_a.png"), b = Path.Combine(dir, "clip_b.png");
        File.WriteAllBytes(a, new byte[] { 1 });
        File.WriteAllBytes(b, new byte[] { 2 });

        FileClipboard.Set(new[] { a, b }, cut: true);
        var (paths, cut) = FileClipboard.Get();
        check(paths.SequenceEqual(new[] { a, b }) && cut && FileClipboard.HasFiles, "クリップボード: 切り取りはファイルと「移動」を載せる");

        FileClipboard.Set(new[] { a }, cut: false);
        (paths, cut) = FileClipboard.Get();
        check(paths.SequenceEqual(new[] { a }) && !cut, "クリップボード: コピーはファイルと「コピー」を載せる");

        // エクスプローラーのコピーは 5（コピー | リンク）、切り取りは 2
        foreach (var (effect, expectCut) in new[] { (5, false), (2, true) })
        {
            var list = new StringCollection { b };
            var data = new DataObject();
            data.SetFileDropList(list);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(effect)));
            Clipboard.SetDataObject(data, copy: true);
            (paths, cut) = FileClipboard.Get();
            check(paths.SequenceEqual(new[] { b }) && cut == expectCut, $"クリップボード: エクスプローラーの形式（{effect}）を{(expectCut ? "切り取り" : "コピー")}と読む");
        }

        var plain = new DataObject();
        plain.SetFileDropList(new StringCollection { a });
        Clipboard.SetDataObject(plain, copy: true);
        check(FileClipboard.Get() is { Cut: false } got && got.Paths.SequenceEqual(new[] { a }), "クリップボード: コピーか移動かが無ければコピーとして扱う");
        Clipboard.Clear();
    }
}
