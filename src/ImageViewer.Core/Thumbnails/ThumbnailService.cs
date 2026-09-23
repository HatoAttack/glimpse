// サムネイルの生成待ち行列とメモリキャッシュ
// - 待ち行列は Schedule のたびに丸ごと差し替える（スクロールで画面外に出た要求は自動的に捨てられる）
// - 生成はワーカースレッド（シェルの COM を使うので STA）で行い、結果は UI スレッドに Post する
// - キャッシュは UI スレッドだけが触る。描画中の Bitmap を別スレッドが Dispose する事故を構造的に防ぐ
using System.Drawing;

namespace ImageViewer.Core.Thumbnails;

/// <summary>同じパスでも更新日時・サイズが変われば別物として扱う（編集後に古いサムネイルが出ない）</summary>
public sealed record ThumbnailKey(string Path, long LastWriteTicks, long Length)
{
    public static ThumbnailKey From(FileInfo file) => new(file.FullName, file.LastWriteTimeUtc.Ticks, file.Length);
}

public enum ThumbnailState { None, Pending, Ready, Failed }

public sealed class ThumbnailService : IDisposable
{
    private readonly Func<string, int, Bitmap> _generator;
    private readonly SynchronizationContext _uiContext;
    private readonly long _memoryBudget;

    // ---- ワーカーと共有する状態（_lock で保護） ----
    private readonly object _lock = new();
    private readonly Queue<ThumbnailKey> _pending = new();
    private readonly HashSet<ThumbnailKey> _pendingSet = new();
    private readonly HashSet<ThumbnailKey> _inFlight = new();
    private bool _disposed;
    private int _generation; // 大きさを変えるたびに増やす。古い大きさで作られた結果は捨てる

    // ---- UI スレッド専用 ----
    private readonly Dictionary<ThumbnailKey, LinkedListNode<Entry>> _cache = new();
    private readonly LinkedList<Entry> _lru = new(); // 先頭が最近使ったもの
    private readonly HashSet<ThumbnailKey> _failed = new();

    private sealed record Entry(ThumbnailKey Key, Bitmap Bitmap, long Bytes);

    /// <summary>生成するサムネイルの長辺（px）</summary>
    public int Size { get; private set; }

    public long CachedBytes { get; private set; }
    public int CachedCount => _cache.Count;

    /// <summary>サムネイルが出来た・失敗した（UI スレッドで発生）</summary>
    public event Action<ThumbnailKey>? ThumbnailReady;

    /// <param name="uiContext">結果を渡す先（通常は UI スレッドの SynchronizationContext.Current）</param>
    /// <param name="memoryBudget">キャッシュの上限バイト数（超えたら古いものから破棄）</param>
    /// <param name="generator">テスト用の差し替え口。既定はシェル → ImageLoader</param>
    public ThumbnailService(int size, long memoryBudget, SynchronizationContext uiContext,
        Func<string, int, Bitmap>? generator = null, int? workers = null)
    {
        Size = size;
        _memoryBudget = memoryBudget;
        _uiContext = uiContext;
        _generator = generator ?? ThumbnailGenerator.Generate;

        int count = workers ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        for (int i = 0; i < count; i++)
        {
            var t = new Thread(WorkerLoop) { IsBackground = true, Name = $"Thumbnail{i}", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    // ---- UI スレッドから呼ぶ ----

    /// <summary>キャッシュを見る（見つかったら最近使ったものとして記録）</summary>
    public ThumbnailState TryGet(ThumbnailKey key, out Bitmap? bitmap)
    {
        bitmap = null;
        if (_cache.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            bitmap = node.Value.Bitmap;
            return ThumbnailState.Ready;
        }
        if (_failed.Contains(key)) return ThumbnailState.Failed;
        lock (_lock)
            return _pendingSet.Contains(key) || _inFlight.Contains(key) ? ThumbnailState.Pending : ThumbnailState.None;
    }

    /// <summary>
    /// 生成してほしいものを優先順に渡す（表示中 → 先読み）。前回の待ち行列は捨てて差し替える。
    /// キャッシュ済み・失敗済み・生成中のものは除外される
    /// </summary>
    public void Schedule(IEnumerable<ThumbnailKey> keysInPriorityOrder)
    {
        var wanted = keysInPriorityOrder.Where(k => !_cache.ContainsKey(k) && !_failed.Contains(k)).ToList();
        lock (_lock)
        {
            _pending.Clear();
            _pendingSet.Clear();
            foreach (var k in wanted)
                if (!_inFlight.Contains(k) && _pendingSet.Add(k)) _pending.Enqueue(k);
            Monitor.PulseAll(_lock);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pending.Clear();
            _pendingSet.Clear();
            Monitor.PulseAll(_lock);
        }
        foreach (var e in _lru) e.Bitmap.Dispose();
        _lru.Clear();
        _cache.Clear();
        CachedBytes = 0;
    }

    /// <summary>
    /// 生成する大きさを変える（UI スレッドから）。キャッシュと待ち行列を捨てて、以後は新しい大きさで作る。
    /// 作成中だったものは、出来上がっても古い大きさなので捨てる
    /// </summary>
    public void SetSize(int size)
    {
        if (size == Size) return;
        lock (_lock)
        {
            Size = size;
            _generation++;
            _pending.Clear();
            _pendingSet.Clear();
            _inFlight.Clear();
        }
        foreach (var e in _lru) e.Bitmap.Dispose();
        _lru.Clear();
        _cache.Clear();
        _failed.Clear();
        CachedBytes = 0;
    }

    // ---- ワーカースレッド ----

    private void WorkerLoop()
    {
        while (true)
        {
            ThumbnailKey key;
            int generation, size;
            lock (_lock)
            {
                while (!_disposed && _pending.Count == 0) Monitor.Wait(_lock);
                if (_disposed) return;
                key = _pending.Dequeue();
                _pendingSet.Remove(key);
                _inFlight.Add(key);
                generation = _generation;
                size = Size;
            }

            Bitmap? bitmap = null;
            try
            {
                bitmap = _generator(key.Path, size);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"サムネイル生成失敗: {key.Path}: {ex.Message}");
            }
            _uiContext.Post(_ => Complete(key, bitmap, generation), null);
        }
    }

    // ---- 結果の受け取り（UI スレッド） ----

    private void Complete(ThumbnailKey key, Bitmap? bitmap, int generation)
    {
        bool stale;
        lock (_lock)
        {
            stale = _disposed || generation != _generation;
            if (!stale) _inFlight.Remove(key);
        }
        if (stale)
        {
            bitmap?.Dispose(); // 終了済み、または古い大きさで作られたもの
            return;
        }

        if (bitmap == null)
        {
            _failed.Add(key);
        }
        else if (_cache.ContainsKey(key))
        {
            bitmap.Dispose(); // 念のため（同じキーが二重に生成された場合）
        }
        else
        {
            var entry = new Entry(key, bitmap, (long)bitmap.Width * bitmap.Height * 4);
            _cache[key] = _lru.AddFirst(entry);
            CachedBytes += entry.Bytes;
            Evict();
        }
        ThumbnailReady?.Invoke(key);
    }

    /// <summary>上限を超えた分を古い順に破棄（直前に追加した 1 枚は残す）</summary>
    private void Evict()
    {
        while (CachedBytes > _memoryBudget && _lru.Count > 1)
        {
            var last = _lru.Last!.Value;
            _lru.RemoveLast();
            _cache.Remove(last.Key);
            CachedBytes -= last.Bytes;
            last.Bitmap.Dispose();
        }
    }
}
