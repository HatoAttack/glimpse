// 並び順（名前・更新日時・サイズ・手動）
using System.Runtime.InteropServices;

namespace ImageViewer.Core.Ordering;

public enum SortMode { Name, Modified, Size, Manual }

public static class FileSorting
{
    /// <summary>エクスプローラーと同じ名前比較（数字部分は数値として比べる: img2 < img10）</summary>
    public static readonly IComparer<string> NaturalNameComparer = Comparer<string>.Create(StrCmpLogicalW);

    /// <summary>
    /// 指定の順に並べた新しいリスト。Manual はここでは名前順（手動の並びは ManualOrder.Apply で当てる）。
    /// 同じ値のものは名前順にして、表示が毎回揺れないようにする
    /// </summary>
    public static List<FileInfo> Sort(IEnumerable<FileInfo> files, SortMode mode)
    {
        var byName = files.OrderBy(f => f.Name, NaturalNameComparer);
        return mode switch
        {
            SortMode.Modified => files.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, NaturalNameComparer).ToList(),
            SortMode.Size => files.OrderBy(f => f.Length).ThenBy(f => f.Name, NaturalNameComparer).ToList(),
            _ => byName.ToList(),
        };
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);
}
