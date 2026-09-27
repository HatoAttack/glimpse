// フォルダの見張り（外での追加・削除・書き換えを、落ち着いてから 1 回だけ知らせる）の動作確認
using System.Windows.Forms;
using ImageViewer.App.Filer;

static class WatcherTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        Directory.CreateDirectory(dir);
        check(FolderWatcher.Relevant("a.jpg") && FolderWatcher.Relevant("B.PNG"), "見張り: 画像の変化で読み直す");
        check(!FolderWatcher.Relevant("a.jpg.0123abcd.tmp") && !FolderWatcher.Relevant("memo.txt"),
            "見張り: 保存中の一時ファイルや画像でないファイルでは読み直さない");

        // イベントは UI のスレッドで受けるので、STA のスレッドでメッセージを回して確かめる
        var events = new List<string>();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
                _ = form.Handle;
                using var watcher = new FolderWatcher(form);
                watcher.Changed += (_, folder) => events.Add(folder);
                watcher.Watch(dir);

                void Pump(int ms)
                {
                    var until = DateTime.UtcNow.AddMilliseconds(ms);
                    while (DateTime.UtcNow < until)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                }

                // 続けて 5 枚書いても、知らせるのは落ち着いてから 1 回
                for (int i = 0; i < 5; i++)
                {
                    File.WriteAllBytes(Path.Combine(dir, $"w{i}.png"), new byte[] { 1, 2, 3 });
                    Pump(30);
                }
                Pump(1200);
                check(events.Count == 1 && events[0] == dir, $"見張り: 続けて追加しても知らせるのは 1 回（{events.Count} 回）");

                events.Clear();
                File.WriteAllText(Path.Combine(dir, "memo.txt"), "x");
                Pump(900);
                check(events.Count == 0, "見張り: 画像でないファイルの追加では知らせない");

                File.Delete(Path.Combine(dir, "w0.png"));
                Pump(900);
                check(events.Count == 1, "見張り: 画像の削除を知らせる");

                // 名前に . があるフォルダも、拡張子で絞らずに知らせる
                events.Clear();
                Directory.CreateDirectory(Path.Combine(dir, "trip.2026"));
                Pump(900);
                check(events.Count == 1, "見張り: 名前に . があるフォルダの追加も知らせる");
                events.Clear();
                Directory.Delete(Path.Combine(dir, "trip.2026"));
                Pump(900);
                check(events.Count == 1, "見張り: 名前に . があるフォルダの削除も知らせる");

                events.Clear();
                watcher.Watch(null);
                File.Delete(Path.Combine(dir, "w1.png"));
                Pump(900);
                check(events.Count == 0 && watcher.Folder == null, "見張り: やめた後は知らせない");

                watcher.Watch(Path.Combine(dir, "no-such-folder"));
                check(watcher.Folder == null, "見張り: 無いフォルダは見張らない（落ちない）");
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        check(error == null, $"見張り: 例外なし {error?.Message}");
    }
}
