// フォルダジャンプ用の索引（フォルダ名と親の番号だけを持つ軽い形）
// - 対象はユーザーフォルダ＋設定で追加したフォルダ。隠し・システム・ジャンクション・node_modules 等は除く
// - 件数に上限（既定 30 万）。超えたら浅い階層を優先して打ち切る（幅優先で走査するため）
// - 保存は 1 ファイル（一時ファイルに書いてから差し替え）。読めない・壊れているときは null を返して作り直させる
using System.Text;
using ImageViewer.Core.Settings;

namespace ImageViewer.Core.Jump;

public sealed class FolderIndex
{
    public const int DefaultLimit = 300_000;

    private readonly string[] _names;
    private readonly int[] _parents;   // -1 = ルート（名前にフルパスが入っている）
    private readonly byte[] _depths;

    public int Count => _names.Length;
    public IReadOnlyList<string> Roots { get; }
    public DateTime BuiltUtc { get; }

    /// <summary>上限に達して打ち切った</summary>
    public bool Truncated { get; }

    private FolderIndex(string[] names, int[] parents, byte[] depths, IReadOnlyList<string> roots, DateTime builtUtc, bool truncated)
    {
        _names = names;
        _parents = parents;
        _depths = depths;
        Roots = roots;
        BuiltUtc = builtUtc;
        Truncated = truncated;
    }

    /// <summary>フォルダ名（ルートはフルパスで持っているので末尾の名前を返す。ドライブのルートならそのまま）</summary>
    public string NameAt(int i)
    {
        if (_parents[i] >= 0) return _names[i];
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(_names[i]));
        return name.Length > 0 ? name : _names[i];
    }

    public int DepthAt(int i) => _depths[i];

    /// <summary>親をたどってフルパスを組み立てる（候補に残ったものだけに使う）</summary>
    public string FullPath(int i)
    {
        var parts = new List<string>(16);
        for (int p = i; ; p = _parents[p])
        {
            parts.Add(_names[p]);
            if (_parents[p] < 0) break;
        }
        parts.Reverse();
        return Path.Combine(parts.ToArray());
    }

    /// <summary>
    /// フォルダー名を変えた。そのフォルダの名前を 1 件書き換えるだけで、中のフォルダも親をたどって新しいパスになる。
    /// 索引に無ければ false（次の作り直しで入る）
    /// </summary>
    public bool Rename(string oldPath, string newPath)
    {
        string oldFull = Path.TrimEndingDirectorySeparator(oldPath), newFull = Path.TrimEndingDirectorySeparator(newPath);
        string oldName = Path.GetFileName(oldFull);
        for (int i = 0; i < _names.Length; i++)
        {
            bool root = _parents[i] < 0;
            // 名前が同じものだけ組み立てて比べる（全件のパスは作らない）
            if (!root && !string.Equals(_names[i], oldName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(Path.TrimEndingDirectorySeparator(FullPath(i)), oldFull, StringComparison.OrdinalIgnoreCase)) continue;
            _names[i] = root ? newFull : Path.GetFileName(newFull);
            return true;
        }
        return false;
    }

    // ---- 作成 ----

    /// <summary>除外するフォルダ名（大きいのに画像を探すことは少ない、開発・システム用のもの）</summary>
    public static readonly IReadOnlySet<string> ExcludedNames = new HashSet<string>(
        new[] { "node_modules", "__pycache__", "venv", "site-packages", "$RECYCLE.BIN", "System Volume Information" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 索引に入れないフォルダ。「.」で始まるもの（.git・.venv・.cache 等、Unix 流の隠しフォルダ）は
    /// Windows の隠し属性が付いていなくても除く
    /// </summary>
    public static bool IsExcluded(string name) => name.StartsWith('.') || ExcludedNames.Contains(name);

    public static FolderIndex Build(IReadOnlyList<string> roots, int limit = DefaultLimit, CancellationToken ct = default)
    {
        var names = new List<string>();
        var parents = new List<int>();
        var depths = new List<byte>();
        var queue = new Queue<int>();
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };
        bool truncated = false;

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            names.Add(Path.GetFullPath(root));
            parents.Add(-1);
            depths.Add(0);
            queue.Enqueue(names.Count - 1);
        }

        // 幅優先: 上限で打ち切っても浅い（＝よく使う）階層は必ず入る
        while (queue.Count > 0 && !truncated)
        {
            ct.ThrowIfCancellationRequested();
            int current = queue.Dequeue();
            string path = FullPathOf(names, parents, current);
            IEnumerable<DirectoryInfo> children;
            try
            {
                children = new DirectoryInfo(path).EnumerateDirectories("*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }
            try
            {
                foreach (var dir in children)
                {
                    if (IsExcluded(dir.Name)) continue;
                    // ジャンクション・シンボリックリンクはたどらない（ループや重複の原因）。
                    // OneDrive のクラウド上のフォルダも再解析ポイントだがリンク先を持たないので対象に含める
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 && dir.LinkTarget != null) continue;
                    if (names.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }
                    names.Add(dir.Name);
                    parents.Add(current);
                    depths.Add((byte)Math.Min(255, depths[current] + 1));
                    queue.Enqueue(names.Count - 1);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // 列挙の途中で読めなくなったフォルダは、そこまでで打ち切る
            }
        }
        return new FolderIndex(names.ToArray(), parents.ToArray(), depths.ToArray(),
            roots.Select(Path.GetFullPath).ToList(), DateTime.UtcNow, truncated);
    }

    private static string FullPathOf(List<string> names, List<int> parents, int i)
    {
        var parts = new List<string>();
        for (int p = i; ; p = parents[p])
        {
            parts.Add(names[p]);
            if (parents[p] < 0) break;
        }
        parts.Reverse();
        return Path.Combine(parts.ToArray());
    }

    // ---- 保存・読み込み ----

    private const int Magic = 0x58465649; // "IVFX"
    private const int Version = 1;

    public void Save(string path)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(BuiltUtc.Ticks);
            w.Write(Truncated);
            w.Write(Roots.Count);
            foreach (var r in Roots) w.Write(r);
            w.Write(Count);
            for (int i = 0; i < Count; i++)
            {
                w.Write(_parents[i]);
                w.Write(_depths[i]);
                w.Write(_names[i]);
            }
            w.Write(Magic); // 終わりの印（途中で切れていないかの確認）
        }
        AtomicFile.WriteAllBytes(path, ms.ToArray());
    }

    /// <summary>読めない・壊れている・形式が違うときは null</summary>
    public static FolderIndex? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            if (r.ReadInt32() != Magic || r.ReadInt32() != Version) return null;
            var built = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            bool truncated = r.ReadBoolean();
            int rootCount = r.ReadInt32();
            if (rootCount is < 0 or > 1000) return null;
            var roots = new List<string>(rootCount);
            for (int i = 0; i < rootCount; i++) roots.Add(r.ReadString());
            int count = r.ReadInt32();
            if (count is < 0 or > 10_000_000) return null;
            var names = new string[count];
            var parents = new int[count];
            var depths = new byte[count];
            for (int i = 0; i < count; i++)
            {
                parents[i] = r.ReadInt32();
                depths[i] = r.ReadByte();
                names[i] = r.ReadString();
                if (parents[i] >= i || parents[i] < -1) return null; // 親は必ず前にある（壊れていれば不正な値になる）
            }
            if (r.ReadInt32() != Magic) return null;
            return new FolderIndex(names, parents, depths, roots, built, truncated);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException
                                       or FormatException or ArgumentException or DecoderFallbackException)
        {
            return null;
        }
    }
}
