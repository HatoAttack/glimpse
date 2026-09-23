// 境界線をドラッグしてもフォーカスを持ち続けない SplitContainer。
// 標準の SplitContainer はドラッグするとフォーカスを取り、境界線に点線の枠が残って、
// 矢印キーでも境界線が動いてしまう。フォーカスを受け取ったら、直前にフォーカスがあったもの（無ければ右側の中身）へ返す
namespace ImageViewer.App.Filer;

public sealed class NoFocusSplitContainer : SplitContainer
{
    private Control? _restoreTo;

    public NoFocusSplitContainer()
    {
        TabStop = false;
    }

    /// <summary>点線の枠は描かない</summary>
    protected override bool ShowFocusCues => false;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        // 境界線を掴む前にフォーカスがあったもの（グリッド・ツリー等）を覚えておく
        _restoreTo = InnermostActive(FindForm());
        base.OnMouseDown(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        BeginInvoke(RestoreFocus);
    }

    private void RestoreFocus()
    {
        if (!Focused) return;
        var target = _restoreTo is { IsDisposed: false, CanFocus: true } && _restoreTo != this
            ? _restoreTo
            : FirstFocusable(Panel2) ?? FirstFocusable(Panel1);
        target?.Focus();
    }

    private static Control? InnermostActive(ContainerControl? container)
    {
        Control? c = container?.ActiveControl;
        while (c is ContainerControl cc && cc.ActiveControl != null && cc.ActiveControl != c) c = cc.ActiveControl;
        return c;
    }

    private static Control? FirstFocusable(Control parent) =>
        parent.Controls.Cast<Control>().FirstOrDefault(c => c.CanFocus && c.TabStop);
}
