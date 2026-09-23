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
