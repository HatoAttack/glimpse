// アドレスバーの下に出す候補の一覧（フォルダ名＋パスの 2 行表示）
// フォーカスはアドレスバーに残したまま使う（矢印キー・Enter はアドレスバー側で受けてこちらを動かす）
using ImageViewer.App.Theming;
using ImageViewer.Core.Jump;

namespace ImageViewer.App.Jump;

public sealed class JumpList : ListBox
{
    private IReadOnlyList<JumpResult> _results = Array.Empty<JumpResult>();

    /// <summary>候補がクリックされた</summary>
    public event EventHandler<string>? Picked;

    public JumpList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        IntegralHeight = false;
        BorderStyle = BorderStyle.FixedSingle;
        TabStop = false;
        Visible = false;
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Current.Surface;
        ForeColor = Theme.Current.Text;
        Theme.ApplyNativeTheme(this);
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyNativeTheme(this);
    }

    public int MaxVisibleItems { get; set; } = 10;

    public IReadOnlyList<JumpResult> Results => _results;

    public void SetResults(IReadOnlyList<JumpResult> results, string? message = null)
    {
        _results = results;
        BeginUpdate();
        Items.Clear();
        foreach (var r in results) Items.Add(r.Path);
        if (results.Count == 0 && message != null) Items.Add(message);
        EndUpdate();
        if (results.Count > 0) SelectedIndex = 0;
        Height = Math.Max(1, Math.Min(Items.Count, MaxVisibleItems)) * ItemHeight + 2;
    }

    /// <summary>選択を上下に動かす（端で止まる）</summary>
    public void MoveSelection(int delta)
    {
        if (_results.Count == 0) return;
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, _results.Count - 1);
    }

    public string? SelectedPath => SelectedIndex >= 0 && SelectedIndex < _results.Count ? _results[SelectedIndex].Path : null;

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ItemHeight = Font.Height * 2 + 6;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var p = Theme.Current;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(selected ? p.SelectionFill : p.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
        var fore = selected ? p.SelectionText : p.Text;
        var sub = selected ? p.SelectionText : p.TextSecondary;
        var r = Rectangle.Inflate(e.Bounds, -6, -2);

        if (e.Index >= _results.Count)
        {
            // 「見つかりません」等のお知らせ
            TextRenderer.DrawText(e.Graphics, Items[e.Index]?.ToString(), Font, r, p.TextMuted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }
        var item = _results[e.Index];
        using var bold = new Font(Font, FontStyle.Bold);
        var top = new Rectangle(r.X, r.Y, r.Width, r.Height / 2);
        var bottom = new Rectangle(r.X, r.Y + r.Height / 2, r.Width, r.Height / 2);
        TextRenderer.DrawText(e.Graphics, item.Name, bold, top, fore, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, item.Path, Font, bottom, sub, TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = IndexFromPoint(e.Location);
        if (e.Button == MouseButtons.Left && i >= 0 && i < _results.Count) Picked?.Invoke(this, _results[i].Path);
    }
}
