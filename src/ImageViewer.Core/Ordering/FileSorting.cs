// 並び順（名前・更新日時・サイズ・手動）と、その向き（昇順・降順）
using System.Runtime.InteropServices;
using ImageViewer.Core.Imaging;

namespace ImageViewer.Core.Ordering;

public enum SortMode { Name, Modified, Size, Manual }

/// <summary>
/// 自動で並べるときの並び順と向き（設定に覚えて、手動の並びを保存していないフォルダで使う）。
/// Mode は Name / Modified / Size のどれか（Manual にはしない）
/// </summary>
public readonly record struct SortPreference(SortMode Mode, bool Descending)
{
    /// <summary>設定から読んだ値を、使える値に直す（無い・Manual・知らない値は名前順）</summary>
    public static SortPreference From(SortMode? mode, bool? descending) =>
        new(mode is SortMode.Name or SortMode.Modified or SortMode.Size ? mode.Value : SortMode.Name, descending ?? false);
}

public static class FileSorting
{
    /// <summary>エクスプローラーと同じ名前比較（数字部分は数値として比べる: img2 < img10）</summary>
    public static readonly IComparer<string> NaturalNameComparer = Comparer<string>.Create(StrCmpLogicalW);

    /// <summary>
    /// 指定の順に並べた新しいリスト。Manual はここでは名前の昇順（手動の並びは ManualOrder.Apply で当てる。向きは見ない）。
    /// 同じ値のものは名前順にして、表示が毎回揺れないようにする（降順ならそこも逆にする）
    /// </summary>
    public static List<ImageFile> Sort(IEnumerable<ImageFile> files, SortMode mode, bool descending = false)
    {
        IComparer<ImageFile> byName = Comparer<ImageFile>.Create((a, b) => NaturalNameComparer.Compare(a.Name, b.Name));
        IComparer<ImageFile> comparer = mode switch
        {
            SortMode.Modified => Then((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc), byName),
            SortMode.Size => Then((a, b) => a.Length.CompareTo(b.Length), byName),
            _ => byName,
        };
        if (descending && mode != SortMode.Manual)
        {
            var ascending = comparer;
            comparer = Comparer<ImageFile>.Create((a, b) => ascending.Compare(b, a));
        }
        return files.OrderBy(f => f, comparer).ToList();
    }

    private static IComparer<ImageFile> Then(Comparison<ImageFile> first, IComparer<ImageFile> next) =>
        Comparer<ImageFile>.Create((a, b) => first(a, b) is int c and not 0 ? c : next.Compare(a, b));

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);
}
