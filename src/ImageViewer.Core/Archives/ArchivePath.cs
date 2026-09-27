// ZIP をフォルダと見なしたパス（C:\…\book.zip\sub\001.jpg）の判定と分解
// ZIP の中は見るだけ（書き込みはしない）。ZIP の中の ZIP は開かない
namespace ImageViewer.Core.Archives;

public static class ArchivePath
{
    /// <summary>フォルダのように開ける圧縮ファイルの拡張子（CBZ は中身が ZIP の漫画用）</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(new[] { ".zip", ".cbz" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>名前が ZIP か（ファイルがあるかは見ない）</summary>
    public static bool IsArchiveName(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// path が ZIP そのもの・ZIP の中なら、ZIP のパスと中のパス（区切りは \、ZIP そのものなら ""）。
    /// 途中の ZIP.zip という名前が実在するファイルのときだけ ZIP と見なす（そういう名前のフォルダもあるので）
    /// </summary>
    public static bool TrySplit(string path, out string archive, out string inner)
    {
        archive = inner = "";
        if (!MayContainArchive(path)) return false;
        string p = Path.TrimEndingDirectorySeparator(path);
        for (int start = 0; ; )
        {
            int sep = p.IndexOf(Path.DirectorySeparatorChar, start);
            int end = sep < 0 ? p.Length : sep;
            string prefix = p[..end];
            if (IsArchiveName(prefix) && File.Exists(prefix))
            {
                archive = prefix;
                inner = sep < 0 ? "" : p[(sep + 1)..];
                return true;
            }
            if (sep < 0) return false;
            start = sep + 1;
        }
    }

    /// <summary>ZIP そのもの、または ZIP の中（一覧を ZIP から作る場所）</summary>
    public static bool IsArchiveFolder(string path) => TrySplit(path, out _, out _);

    /// <summary>ZIP の中の項目（ZIP そのものは含まない）。書き込み・シェルの機能が使えないもの</summary>
    public static bool IsInside(string path) => TrySplit(path, out _, out string inner) && inner.Length > 0;

    /// <summary>ZIP の中の項目のパス</summary>
    public static string Combine(string archive, string inner) =>
        inner.Length == 0 ? archive : archive + Path.DirectorySeparatorChar + inner;

    /// <summary>File.Exists を呼ぶ前の軽い判定（パスのどこかに .zip / .cbz があるか）</summary>
    private static bool MayContainArchive(string path)
    {
        foreach (var ext in Extensions)
            if (path.Contains(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
