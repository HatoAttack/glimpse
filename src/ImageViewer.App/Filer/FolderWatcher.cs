// 開いているフォルダの変化（エクスプローラーやほかのアプリでの追加・削除・名前の変更・書き換え）を見張る。
// 変化が続いている間は待ち、落ち着いてから Changed を 1 回だけ出す（大きなファイルのコピー中などに何度も読み直さない）。
// 見張れないフォルダ（一部のネットワークドライブなど）では何もしない
using ImageViewer.Core.Imaging;

namespace ImageViewer.App.Filer;

public sealed class FolderWatcher : IDisposable
{
    /// <summary>最後の変化からこれだけ静かになったら知らせる</summary>
    private const int QuietMs = 400;

    /// <summary>変化が続いていても、最初の変化からこれだけ経ったら一度知らせる</summary>
    private const int MaxWaitMs = 2000;

    private readonly Control _owner;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = QuietMs };
    private FileSystemWatcher? _files, _folders;
    private DateTime _firstChange;

    /// <summary>見張っているフォルダ（見張れていなければ null）</summary>
    public string? Folder { get; private set; }

    /// <summary>変化が落ち着いた（UI のスレッドで出す）。引数は見張っているフォルダ</summary>
    public event EventHandler<string>? Changed;

    /// <param name="owner">イベントを UI のスレッドへ戻すためのコントロール（メインのウィンドウ）</param>
    public FolderWatcher(Control owner)
    {
        _owner = owner;
        _timer.Tick += (_, _) => Flush();
    }

    /// <summary>見張るフォルダを変える（null でやめる）。同じフォルダなら何もしない</summary>
    public void Watch(string? folder)
    {
        if (string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase) && (_files != null || folder == null)) return;
        Stop();
        if (folder == null) return;
        try
        {
            // ファイル: 画像の追加・削除・名前の変更・書き換え（名前で絞る）
            var files = _files = Create(folder, NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size);
            files.Created += (_, e) => OnEvent(e.Name);
            files.Deleted += (_, e) => OnEvent(e.Name);
            files.Changed += (_, e) => OnEvent(e.Name);
            files.Renamed += (_, e) => { if (Relevant(e.OldName) || Relevant(e.Name)) Mark(); };
            // フォルダ: 一覧に出る中のフォルダの追加・削除・名前の変更（名前に . があっても拡張子で絞らない）
            var folders = _folders = Create(folder, NotifyFilters.DirectoryName);
            folders.Created += (_, _) => Mark();
            folders.Deleted += (_, _) => Mark();
            folders.Renamed += (_, _) => Mark();
            files.EnableRaisingEvents = true;
            folders.EnableRaisingEvents = true;
            Folder = folder;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Stop(); // 見張れないフォルダ。F5 の読み直しだけになる
        }
    }

    private FileSystemWatcher Create(string folder, NotifyFilters filter)
    {
        var w = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = filter,
            InternalBufferSize = 64 * 1024,
            SynchronizingObject = _owner, // イベントは UI のスレッドで受ける
        };
        w.Error += (_, _) => Mark(); // 変化が多すぎて取りこぼした: 読み直せば追いつく
        return w;
    }

    private void OnEvent(string? name)
    {
        if (Relevant(name)) Mark();
    }

    /// <summary>
    /// 一覧に関わるファイルの名前か（画像と、フォルダのタイルとして出す ZIP）。保存のときの一時ファイル（.tmp）や、画像でないファイルでは読み直さない。
    /// フォルダの変化は別の見張りで受けるので、ここでは見ない
    /// </summary>
    public static bool Relevant(string? name) =>
        string.IsNullOrEmpty(name) || ImageFormats.IsSupported(name) || Core.Archives.ArchivePath.IsArchiveName(name);

    private void Mark()
    {
        if (_files == null) return;
        var now = DateTime.UtcNow;
        if (!_timer.Enabled) _firstChange = now;
        _timer.Stop();
        // 続いていても、最初の変化から MaxWaitMs を過ぎたらすぐに知らせる
        _timer.Interval = (now - _firstChange).TotalMilliseconds >= MaxWaitMs ? 1 : QuietMs;
        _timer.Start();
    }

    private void Flush()
    {
        _timer.Stop();
        if (Folder == null) return;
        // ドラッグ中（並べ替え・範囲選択）に一覧を入れ替えると操作が途切れるので、離すまで待つ
        if (Control.MouseButtons != MouseButtons.None)
        {
            _timer.Interval = QuietMs;
            _timer.Start();
            return;
        }
        Changed?.Invoke(this, Folder);
    }

    private void Stop()
    {
        _timer.Stop();
        _files?.Dispose();
        _folders?.Dispose();
        _files = _folders = null;
        Folder = null;
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }
}
