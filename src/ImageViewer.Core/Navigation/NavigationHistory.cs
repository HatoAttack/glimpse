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
