// 開いたフォルダの記録（回数と最後に開いた日時）。よく開く・最近開いたフォルダをジャンプの候補の上位に出す
// 件数に上限（既定 500）。超えたら点数の低いものから捨てる
using System.Text.Json;
using ImageViewer.Core.Settings;

namespace ImageViewer.Core.Jump;

public sealed class VisitHistory
{
    public sealed record Visit(string Path, int Count, DateTime LastUtc);

    private readonly Dictionary<string, Visit> _visits = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly int _limit;

    public VisitHistory(int limit = 500) => _limit = limit;

    public int Count
    {
        get { lock (_lock) return _visits.Count; }
    }

    public void Record(string path, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_lock)
        {
            _visits[path] = _visits.TryGetValue(path, out var v) ? v with { Count = v.Count + 1, LastUtc = now } : new Visit(path, 1, now);
            if (_visits.Count > _limit)
            {
                foreach (var drop in _visits.Values.OrderBy(x => Frecency(x, now)).Take(_visits.Count - _limit).ToList())
                    _visits.Remove(drop.Path);
            }
        }
    }

    /// <summary>よく開く × 最近開いた の点数（0 なら記録なし）</summary>
    public double FrecencyOf(string path, DateTime? nowUtc = null)
    {
        lock (_lock)
            return _visits.TryGetValue(path, out var v) ? Frecency(v, nowUtc ?? DateTime.UtcNow) : 0;
    }

    public IReadOnlyList<Visit> Snapshot()
    {
        lock (_lock) return _visits.Values.ToList();
    }

    private static double Frecency(Visit v, DateTime now)
    {
        var age = now - v.LastUtc;
        double recency = age < TimeSpan.FromDays(1) ? 4 : age < TimeSpan.FromDays(7) ? 2 : age < TimeSpan.FromDays(30) ? 1 : 0.5;
        return Math.Min(v.Count, 50) * recency;
    }

    // ---- 保存・読み込み（壊れていたら空から始める） ----

    public void Save(string path)
    {
        List<Visit> list;
        lock (_lock) list = _visits.Values.ToList();
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(list));
    }

    public static VisitHistory Load(string path, int limit = 500)
    {
        var history = new VisitHistory(limit);
        try
        {
            if (!File.Exists(path)) return history;
            var list = JsonSerializer.Deserialize<List<Visit>>(File.ReadAllText(path));
            if (list == null) return history;
            foreach (var v in list.Where(v => !string.IsNullOrEmpty(v.Path)).OrderByDescending(v => v.LastUtc).Take(limit))
                history._visits[v.Path] = v;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // 壊れた記録は無かったことにする
        }
        return history;
    }
}
