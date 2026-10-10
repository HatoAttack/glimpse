// 戻る / 進む の履歴（ブラウザと同じ動き）。件数に上限を設けて、長時間使っても増え続けないようにする
namespace ImageViewer.Core.Navigation;

public sealed class NavigationHistory
{
    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();
    private readonly int _limit;

    public NavigationHistory(int limit = 100) => _limit = limit;

    public string? Current { get; private set; }
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>新しい場所へ移動（同じ場所なら何もしない）。進む の履歴は消える</summary>
    public void Navigate(string path)
    {
        if (Current != null && string.Equals(Current, path, StringComparison.OrdinalIgnoreCase)) return;
        if (Current != null) Push(_back, Current);
        _forward.Clear();
        Current = path;
    }

    /// <summary>戻り先（無ければ null）。履歴は動かすので、移動に失敗したら Navigate で上書きする</summary>
    public string? GoBack()
    {
        if (!CanGoBack) return null;
        Push(_forward, Current!);
        Current = Pop(_back);
        return Current;
    }

    public string? GoForward()
    {
        if (!CanGoForward) return null;
        Push(_back, Current!);
        Current = Pop(_forward);
        return Current;
    }

    /// <summary>フォルダー名を変えた。そのフォルダ（とその中）の履歴を新しいパスに付け替える</summary>
    public void Retarget(string oldPath, string newPath)
    {
        if (Current != null) Current = FolderListing.Retarget(Current, oldPath, newPath) ?? Current;
        foreach (var stack in new[] { _back, _forward })
            for (int i = 0; i < stack.Count; i++)
                stack[i] = FolderListing.Retarget(stack[i], oldPath, newPath) ?? stack[i];
    }

    /// <summary>
    /// フォルダーを消した・別の場所へ移した。そのフォルダ（とその中）を戻る / 進む の履歴から外す（今の場所はそのまま）。
    /// ファイルのパスが混ざっていてもよい（履歴には無いので何も起きない）
    /// </summary>
    public void Remove(IEnumerable<string> paths)
    {
        var gone = new HashSet<string>(paths.Select(Path.TrimEndingDirectorySeparator), StringComparer.OrdinalIgnoreCase);
        if (gone.Count == 0) return;
        // 履歴の側から、自分か上のフォルダが消えたものに入っているかを見る（消えたものが多くても速い）
        bool Gone(string path)
        {
            for (string? p = Path.TrimEndingDirectorySeparator(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                if (gone.Contains(p)) return true;
            return false;
        }
        foreach (var stack in new[] { _back, _forward }) stack.RemoveAll(Gone);
    }

    private void Push(List<string> stack, string path)
    {
        stack.Add(path);
        if (stack.Count > _limit) stack.RemoveAt(0); // 古いものから捨てる
    }

    private static string Pop(List<string> stack)
    {
        var last = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return last;
    }
}
