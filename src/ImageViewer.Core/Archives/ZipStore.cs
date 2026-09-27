// ZIP の中の一覧と読み込み（展開せず、読むものだけをメモリに取り出す）
// - 開いた ZIP の目次（中の一覧）は少しの間覚えておく。サムネイルは 1 枚ずつ読みに来るので、毎回目次を読み直さないように
// - ファイルは読んでいる間だけ開き、しばらく読まなければ閉じる（開いたままだと、ほかのアプリがその ZIP に書き込めない）。
//   開いている間も、ほかのアプリが ZIP を消したり名前を変えたりはできるよう、共有して開く
// - 同じ ZIP から同時には読めない（中で 1 つのファイルを共有している）ので、ZIP ごとに順番に読む
// - ZIP が書き換えられた（更新日時か大きさが変わった）ら目次を読み直す
using System.IO.Compression;
using ImageViewer.Core.Imaging;

namespace ImageViewer.Core.Archives;

/// <summary>ZIP の中のあるフォルダの中身（サブフォルダと画像。どちらもフルパス）</summary>
public sealed record ArchiveListing(IReadOnlyList<string> Folders, IReadOnlyList<ImageFile> Images);

public static class ZipStore
{
    /// <summary>目次を覚えておく ZIP の数</summary>
    private const int MaxCached = 2;

    /// <summary>1 つのファイルとして取り出せる大きさの上限</summary>
    private const long MaxEntryBytes = 1L << 30;

    /// <summary>最後に読んでからファイルを閉じるまでの時間</summary>
    public static TimeSpan IdleClose { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>目次の 1 項目（中のパスは \ 区切り）</summary>
    private sealed record Entry(string FullName, long Length, DateTime LastWriteTimeUtc);

    private sealed class Opened : IDisposable
    {
        public Opened(string archive, DateTime stamp, long length)
        {
            Archive = archive;
            Stamp = stamp;
            Length = length;
            _idle = new Timer(_ => CloseIfIdle());
        }

        public string Archive { get; }
        public DateTime Stamp { get; }
        public long Length { get; }

        /// <summary>中のファイル（中のパス → 項目）</summary>
        public Dictionary<string, Entry> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>中のフォルダ（"" は ZIP の一番上）。フォルダの項目が無くても、ファイルのパスの途中にあれば入る</summary>
        public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase) { "" };

        public bool Disposed { get; private set; }

        // ---- 開いているファイル（lock (this) で守る） ----
        private readonly Timer _idle;
        private FileStream? _stream;
        private ZipArchive? _zip;
        private Dictionary<string, ZipArchiveEntry>? _entries;
        private DateTime _lastUsed;

        /// <summary>ファイルを開いて目次を作る（最初の 1 回）</summary>
        public void Index()
        {
            lock (this)
            {
                foreach (var (name, entry) in Open())
                {
                    bool isFolder = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                    // パスの途中のフォルダは、フォルダの項目が無くても作る
                    for (string parent = ParentOf(name); parent.Length > 0 && Folders.Add(parent); parent = ParentOf(parent)) { }
                    if (isFolder) Folders.Add(name);
                    else Files.TryAdd(name, new Entry(name, entry.Length, entry.LastWriteTime.UtcDateTime));
                }
                Touch();
            }
        }

        /// <summary>中のファイルの中身</summary>
        public byte[] Read(string inner, string path)
        {
            lock (this)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                if (!Files.TryGetValue(inner, out var info)) throw new FileNotFoundException("ZIP の中にファイルが見つかりません", path);
                if (info.Length > MaxEntryBytes) throw new NotSupportedException("ZIP の中のファイルが大きすぎます");
                if (_entries == null) Open();
                // 閉じている間に書き換えられて、その名前が無くなった
                if (!_entries!.TryGetValue(inner, out var entry)) throw new FileNotFoundException("ZIP の中にファイルが見つかりません", path);
                try
                {
                    using var stream = entry.Open();
                    var buffer = new MemoryStream((int)Math.Min(entry.Length, MaxEntryBytes));
                    stream.CopyTo(buffer);
                    return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
                }
                finally
                {
                    Touch();
                }
            }
        }

        private Dictionary<string, ZipArchiveEntry> Open()
        {
            var stream = new FileStream(Archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
            try
            {
                var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, ZipNameEncoding.Instance);
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                    if (Normalize(entry.FullName) is { } name) entries.TryAdd(name, entry);
                (_stream, _zip, _entries) = (stream, zip, entries);
                return entries;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private void Touch()
        {
            _lastUsed = DateTime.UtcNow;
            _idle.Change(IdleClose, Timeout.InfiniteTimeSpan);
        }

        private void CloseIfIdle()
        {
            lock (this)
            {
                if (_entries == null) return;
                var wait = _lastUsed + IdleClose - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    _idle.Change(wait, Timeout.InfiniteTimeSpan);
                    return;
                }
                CloseFile();
            }
        }

        private void CloseFile()
        {
            _zip?.Dispose();
            _stream?.Dispose();
            (_stream, _zip, _entries) = (null, null, null);
        }

        public void Dispose()
        {
            lock (this)
            {
                Disposed = true;
                _idle.Dispose();
                CloseFile();
            }
        }
    }

    private static readonly object CacheLock = new();
    private static readonly List<Opened> Cache = new(); // 先頭が最近使ったもの

    /// <summary>ZIP の中のフォルダ（inner が "" なら一番上）の中身。サブフォルダ・画像とも名前順ではない（並べるのは呼ぶ側）</summary>
    public static ArchiveListing List(string archive, string inner, CancellationToken ct = default)
    {
        var zip = Get(archive);
        if (!zip.Folders.Contains(inner)) throw new DirectoryNotFoundException($"ZIP の中にフォルダーが見つかりません: {inner}");
        var folders = new List<string>();
        foreach (var f in zip.Folders)
        {
            ct.ThrowIfCancellationRequested();
            if (f.Length > 0 && string.Equals(ParentOf(f), inner, StringComparison.OrdinalIgnoreCase))
                folders.Add(ArchivePath.Combine(archive, f));
        }
        var images = new List<ImageFile>();
        foreach (var entry in zip.Files.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(ParentOf(entry.FullName), inner, StringComparison.OrdinalIgnoreCase) || !ImageFormats.IsSupported(entry.FullName)) continue;
            images.Add(new ImageFile(ArchivePath.Combine(archive, entry.FullName), entry.Length, entry.LastWriteTimeUtc));
        }
        return new ArchiveListing(folders, images);
    }

    /// <summary>ZIP そのもの、または ZIP の中のフォルダか</summary>
    public static bool FolderExists(string path)
    {
        if (!ArchivePath.TrySplit(path, out string archive, out string inner)) return false;
        try
        {
            return Get(archive).Folders.Contains(inner);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return false;
        }
    }

    /// <summary>ZIP の中のファイルの中身（path は ZIP をフォルダと見なしたパス）</summary>
    public static byte[] Read(string path)
    {
        if (!ArchivePath.TrySplit(path, out string archive, out string inner) || inner.Length == 0)
            throw new FileNotFoundException("ZIP の中のファイルではありません", path);
        try
        {
            return Get(archive).Read(inner, path);
        }
        catch (ObjectDisposedException)
        {
            // 読もうとした間に、ZIP が書き換えられて目次を読み直した → 新しい目次で読む
            return Get(archive).Read(inner, path);
        }
    }

    /// <summary>ZIP の中のファイルを destination に書き出す（コピー・ドラッグでほかのアプリへ渡す用）</summary>
    public static void Extract(string path, string destination) => File.WriteAllBytes(destination, Read(path));

    /// <summary>覚えている ZIP を閉じて忘れる（ZIP の外のフォルダへ移ったとき）</summary>
    public static void CloseAll()
    {
        List<Opened> closing;
        lock (CacheLock)
        {
            closing = Cache.ToList();
            Cache.Clear();
        }
        foreach (var z in closing) z.Dispose();
    }

    /// <summary>ZIP が読めなかったときの例外（壊れている・無い・使えない）</summary>
    public static bool IsReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException;

    private static string ParentOf(string inner)
    {
        int i = inner.LastIndexOf(Path.DirectorySeparatorChar);
        return i < 0 ? "" : inner[..i];
    }

    private static Opened Get(string archive)
    {
        var info = new FileInfo(archive);
        if (!info.Exists) throw new FileNotFoundException("ZIP が見つかりません", archive);
        DateTime stamp = info.LastWriteTimeUtc;
        long length = info.Length;
        var dispose = new List<Opened>();
        try
        {
            lock (CacheLock)
            {
                int i = Cache.FindIndex(z => string.Equals(z.Archive, archive, StringComparison.OrdinalIgnoreCase));
                if (i >= 0)
                {
                    var hit = Cache[i];
                    Cache.RemoveAt(i);
                    if (hit.Stamp == stamp && hit.Length == length)
                    {
                        Cache.Insert(0, hit);
                        return hit;
                    }
                    dispose.Add(hit); // 書き換えられた
                }
                var opened = new Opened(archive, stamp, length);
                try
                {
                    opened.Index();
                }
                catch
                {
                    opened.Dispose();
                    throw;
                }
                Cache.Insert(0, opened);
                if (Cache.Count > MaxCached)
                {
                    dispose.Add(Cache[^1]);
                    Cache.RemoveAt(Cache.Count - 1);
                }
                return opened;
            }
        }
        finally
        {
            // 読んでいる途中のものは、読み終わるのを待ってから閉じる（lock の外で）
            foreach (var z in dispose) z.Dispose();
        }
    }

    /// <summary>
    /// 中のパスを \ 区切りにする。macOS が付ける __MACOSX（元のファイルの付属情報で、画像ではない）・
    /// 「..」やドライブ名を含むおかしなパスは null（一覧に出さない）
    /// </summary>
    private static string? Normalize(string fullName)
    {
        var parts = fullName.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.Contains(':') || string.Equals(part, "__MACOSX", StringComparison.OrdinalIgnoreCase))
                return null;
        }
        if (parts[^1].StartsWith("._", StringComparison.Ordinal)) return null;
        return string.Join(Path.DirectorySeparatorChar, parts);
    }
}
