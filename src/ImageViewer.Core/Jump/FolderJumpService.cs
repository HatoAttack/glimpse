// フォルダジャンプの本体: 索引の読み込み・作り直し（裏で低い優先度）と検索
// - 起動時は保存した索引をすぐ読み、古ければ（既定 30 分）裏で作り直して差し替える。常駐の監視はしない
// - 検索は索引と「開いたフォルダの記録」の両方から。記録にあるフォルダは点数を上乗せする
namespace ImageViewer.Core.Jump;

public sealed record JumpResult(string Path, string Name, int Score);

public sealed class FolderJumpService
{
    private readonly string _indexPath;
    private readonly string _visitsPath;
    private readonly TimeSpan _maxAge;
    private volatile FolderIndex? _index;
    private int _building; // 0/1（二重に作らない）
    private readonly object _buildLock = new();
    private string[]? _queuedRoots;
    private int _queuedLimit;
    private int _requests; // Rebuild を頼まれた回数（起動時の古い指定・古い索引で、先に頼まれた作り直しを上書きしない）

    public VisitHistory Visits { get; }

    /// <summary>索引が作り直された（別スレッドから呼ばれる）</summary>
    public event Action? IndexChanged;

    public FolderIndex? Index => _index;
    public bool IsBuilding => Volatile.Read(ref _building) == 1;

    public FolderJumpService(string dataDir, TimeSpan? maxAge = null)
    {
        _indexPath = Path.Combine(dataDir, "folder-index.bin");
        _visitsPath = Path.Combine(dataDir, "visits.json");
        _maxAge = maxAge ?? TimeSpan.FromMinutes(30);
        Visits = VisitHistory.Load(_visitsPath);
    }

    public static string DefaultDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ima-ge-viewer");

    /// <summary>
    /// 保存した索引を読み、無い・古い・対象フォルダが変わったときは裏で作り直す（起動時に 1 回）。
    /// それより前・読み込みの途中に Rebuild が頼まれていたら（設定の変更など）、読んだ索引も起動時の指定も使わない
    /// </summary>
    public Task StartAsync(IReadOnlyList<string> roots) => Task.Run(() =>
    {
        var loaded = FolderIndex.Load(_indexPath);
        if (loaded != null)
        {
            lock (_buildLock)
            {
                if (_requests != 0) return;
                _index = loaded;
            }
            IndexChanged?.Invoke();
        }
        bool sameRoots = loaded != null && loaded.Roots.SequenceEqual(roots.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        if (loaded == null || !sameRoots || DateTime.UtcNow - loaded.BuiltUtc > _maxAge)
            Rebuild(roots.ToArray(), FolderIndex.DefaultLimit, onlyIfRequests: 0);
    });

    /// <summary>裏で（低い優先度のスレッドで）作り直す。作成中の指定は最新のものを次に使う</summary>
    public void Rebuild(IReadOnlyList<string> roots, int limit = FolderIndex.DefaultLimit) => Rebuild(roots.ToArray(), limit, onlyIfRequests: null);

    /// <param name="onlyIfRequests">指定したとき、その後に別の Rebuild が頼まれていたら何もしない</param>
    private void Rebuild(string[] currentRoots, int limit, int? onlyIfRequests)
    {
        lock (_buildLock)
        {
            if (onlyIfRequests is int expected && _requests != expected) return;
            _requests++;
            if (_building == 1)
            {
                _queuedRoots = currentRoots;
                _queuedLimit = limit;
                return;
            }
            Volatile.Write(ref _building, 1);
        }
        var thread = new Thread(() =>
        {
            int currentLimit = limit;
            bool released = false;
            try
            {
                while (true)
                {
                    var index = FolderIndex.Build(currentRoots, currentLimit);
                    _index = index;
                    try { index.Save(_indexPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 次回また作る */ }
                    IndexChanged?.Invoke();

                    lock (_buildLock)
                    {
                        if (_queuedRoots == null)
                        {
                            Volatile.Write(ref _building, 0);
                            released = true;
                            break;
                        }
                        currentRoots = _queuedRoots;
                        currentLimit = _queuedLimit;
                        _queuedRoots = null;
                    }
                }
            }
            finally
            {
                if (!released)
                {
                    lock (_buildLock)
                    {
                        _queuedRoots = null;
                        Volatile.Write(ref _building, 0);
                    }
                }
            }
        })
        { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "FolderIndex" };
        thread.Start();
    }

    public void RecordVisit(string folder) => Visits.Record(folder);

    public void SaveVisits()
    {
        try { Visits.Save(_visitsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 候補を点数順に。extra（Everything の結果など）があれば索引の代わりにそれを使う。
    /// 記録にあるフォルダは、索引に無くても（ネットワーク上など）候補に入る
    /// </summary>
    public List<JumpResult> Search(string query, int limit = 30, IReadOnlyList<string>? extra = null, CancellationToken ct = default)
    {
        var tokens = FolderMatcher.Tokenize(query);
        if (tokens.Length == 0) return new List<JumpResult>();
        string last = tokens[^1];
        var now = DateTime.UtcNow;
        var best = new Dictionary<string, JumpResult>(StringComparer.OrdinalIgnoreCase);

        void Offer(string path, string name, int nameScore, int depth)
        {
            if (!FolderMatcher.PathContainsOthers(path, tokens)) return;
            double frecency = Visits.FrecencyOf(path, now);
            int score = nameScore - Math.Min(depth, 20) * 3 + (int)Math.Min(400, frecency * 25);
            if (!best.TryGetValue(path, out var existing) || existing.Score < score)
                best[path] = new JumpResult(path, name, score);
        }

        if (extra != null)
        {
            foreach (var path in extra)
            {
                string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                int s = FolderMatcher.ScoreName(name, last);
                if (s != FolderMatcher.NoMatch) Offer(path, name, s, path.Count(c => c == Path.DirectorySeparatorChar));
            }
        }
        else if (_index is FolderIndex index)
        {
            // 名前だけで先に絞る（フルパスは候補に残ったものだけ組み立てる）。
            // 1 文字の入力などで候補が多すぎるときは点数の高いものだけ残す
            var candidates = new List<(int Index, int Score)>();
            for (int i = 0; i < index.Count; i++)
            {
                if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                int s = FolderMatcher.ScoreName(index.NameAt(i), last);
                if (s != FolderMatcher.NoMatch) candidates.Add((i, s - Math.Min(index.DepthAt(i), 20) * 3));
            }
            int keep = tokens.Length > 1 ? 5000 : 500;
            foreach (var (i, _) in candidates.OrderByDescending(c => c.Score).Take(keep))
                Offer(index.FullPath(i), index.NameAt(i), FolderMatcher.ScoreName(index.NameAt(i), last), index.DepthAt(i));
        }

        foreach (var v in Visits.Snapshot())
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(v.Path));
            int s = FolderMatcher.ScoreName(name.Length > 0 ? name : v.Path, last);
            if (s != FolderMatcher.NoMatch) Offer(v.Path, name.Length > 0 ? name : v.Path, s, v.Path.Count(c => c == Path.DirectorySeparatorChar) - 1);
        }

        return best.Values.OrderByDescending(r => r.Score).ThenBy(r => r.Path.Length).Take(limit).ToList();
    }
}
