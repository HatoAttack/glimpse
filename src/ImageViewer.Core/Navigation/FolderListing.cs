// フォルダの中のサブフォルダの一覧（グリッドのフォルダタイル・フォルダツリーで共通）
using ImageViewer.Core.Ordering;

namespace ImageViewer.Core.Navigation;

public static class FolderListing
{
    /// <summary>
    /// 直下のサブフォルダを名前順（エクスプローラーと同じ比較）で。隠し・システムフォルダは除く。
    /// アクセスできないフォルダは空として扱う（例外にしない）
    /// </summary>
    public static List<DirectoryInfo> ListSubfolders(string folder, CancellationToken ct = default)
    {
        var list = new List<DirectoryInfo>();
        try
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                IgnoreInaccessible = true,
            };
            foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories("*", options))
            {
                ct.ThrowIfCancellationRequested();
                list.Add(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return list;
        }
        list.Sort((a, b) => FileSorting.NaturalNameComparer.Compare(a.Name, b.Name));
        return list;
    }

    /// <summary>親フォルダ（ドライブのルートなら null）</summary>
    public static string? Parent(string folder) => Directory.GetParent(Path.TrimEndingDirectorySeparator(folder))?.FullName;

    /// <summary>パスらしい入力（C:\… \\server %VAR% など）。フォルダ名での検索ではなくパスとして扱う</summary>
    public static bool LooksLikePath(string text)
    {
        string t = text.Trim().Trim('"');
        return t.Length >= 2 && t[1] == ':' || t.StartsWith(@"\\") || t.StartsWith('%') || t.StartsWith('/') || t.StartsWith('\\');
    }

    /// <summary>path がフォルダ folder の直下にあるか（大文字小文字・末尾の区切りは無視）</summary>
    public static bool IsDirectlyIn(string path, string folder)
    {
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        return parent != null && string.Equals(Path.TrimEndingDirectorySeparator(parent),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>folder が path 自身かその中か（フォルダを自分の中へ移動・コピーしないための確認）</summary>
    public static bool IsSameOrInside(string folder, string path)
    {
        string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(f, p, StringComparison.OrdinalIgnoreCase)
               || f.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// フォルダー名の変更に合わせてパスを付け替える。path が oldPath 自身かその中なら newPath に置き換えたパス、
    /// 関係なければ null（大文字小文字・末尾の区切りは無視）
    /// </summary>
    public static string? Retarget(string path, string oldPath, string newPath)
    {
        string p = Path.TrimEndingDirectorySeparator(path), o = Path.TrimEndingDirectorySeparator(oldPath);
        if (string.Equals(p, o, StringComparison.OrdinalIgnoreCase)) return newPath;
        return p.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.TrimEndingDirectorySeparator(newPath) + p[o.Length..]
            : null;
    }

    /// <summary>同じドライブか（ドラッグ＆ドロップの既定を、エクスプローラーと同じく 同じドライブ = 移動 / 別 = コピー にする）</summary>
    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>入力されたパスを正規化（前後の空白・引用符を除き、環境変数を展開）。フォルダとして存在しなければ null</summary>
    public static string? Normalize(string input)
    {
        string s = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"').Trim());
        if (s.Length == 0) return null;
        // "C:" のようなドライブ名だけのときはルートにする（そのままだと「C ドライブの現在のフォルダ」になる）
        if (s.Length == 2 && s[1] == ':') s += Path.DirectorySeparatorChar;
        // 相対パスはアプリの作業フォルダ基準になって紛らわしいので受け付けない
        if (!Path.IsPathFullyQualified(s)) return null;
        try
        {
            string full = Path.GetFullPath(s);
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }
}
