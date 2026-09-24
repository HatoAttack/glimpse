// アドレスバーの下に出す候補の一覧（2 行表示）。コマンド（名前＋メニューの場所・ショートカット）を先に、続けてフォルダ（名前＋パス）
// フォーカスはアドレスバーに残したまま使う（矢印キー・Enter はアドレスバー側で受けてこちらを動かす）
using ImageViewer.App.Chrome;
using ImageViewer.App.Theming;
using ImageViewer.Core.Jump;

namespace ImageViewer.App.Jump;

/// <summary>候補のコマンド（☰ メニューの項目）</summary>
/// <param name="Where">メニューの場所（"表示 › テーマ" など）</param>
public sealed record CommandCandidate(string Name, string Where, string Shortcut, bool Enabled, Action Run);

public sealed class JumpList : ListBox
{
    private IReadOnlyList<CommandCandidate> _commands = Array.Empty<CommandCandidate>();
    private IReadOnlyList<JumpResult> _results = Array.Empty<JumpResult>();

    /// <summary>フォルダの候補がクリックされた</summary>
    public event EventHandler<string>? Picked;

    /// <summary>コマンドの候補がクリックされた</summary>
    public event EventHandler<CommandCandidate>? CommandPicked;

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

    private int Count => _commands.Count + _results.Count;

    public void SetResults(IReadOnlyList<JumpResult> results, string? message = null) => SetResults(Array.Empty<CommandCandidate>(), results, message);

    public void SetResults(IReadOnlyList<CommandCandidate> commands, IReadOnlyList<JumpResult> results, string? message = null)
    {
        _commands = commands;
        _results = results;
        BeginUpdate();
        Items.Clear();
        foreach (var c in commands) Items.Add(c.Name);
        foreach (var r in results) Items.Add(r.Path);
        if (Count == 0 && message != null) Items.Add(message);
        EndUpdate();
        if (Count > 0) SelectedIndex = 0;
        Height = Math.Max(1, Math.Min(Items.Count, MaxVisibleItems)) * ItemHeight + 2;
    }

    /// <summary>選択を上下に動かす（端で止まる）</summary>
    public void MoveSelection(int delta)
    {
        if (Count == 0) return;
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Count - 1);
    }

    /// <summary>選んでいるフォルダ（コマンドを選んでいれば null）</summary>
    public string? SelectedPath =>
        SelectedIndex >= _commands.Count && SelectedIndex < Count ? _results[SelectedIndex - _commands.Count].Path : null;

    public CommandCandidate? SelectedCommand => SelectedIndex >= 0 && SelectedIndex < _commands.Count ? _commands[SelectedIndex] : null;

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
        var r = Rectangle.Inflate(e.Bounds, -6, -2);

        if (e.Index >= Count)
        {
            // 「見つかりません」等のお知らせ
            TextRenderer.DrawText(e.Graphics, Items[e.Index]?.ToString(), Font, r, p.TextMuted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        string title, sub;
        var fore = selected ? p.SelectionText : p.Text;
        var subColor = selected ? p.SelectionText : p.TextSecondary;
        if (e.Index < _commands.Count)
        {
            // コマンド: 左に ›、右にショートカット。今は実行できないものは薄く
            var c = _commands[e.Index];
            if (!c.Enabled) fore = subColor = p.Disabled;
            int icon = Font.Height;
            Icons.Forward(e.Graphics, new RectangleF(r.X, r.Y + (r.Height - icon) / 2f, icon, icon), c.Enabled ? p.TextMuted : p.Disabled);
            r = new Rectangle(r.X + icon + 6, r.Y, r.Width - icon - 6, r.Height);
            if (c.Shortcut.Length > 0)
            {
                int w = TextRenderer.MeasureText(e.Graphics, c.Shortcut, Font).Width;
                TextRenderer.DrawText(e.Graphics, c.Shortcut, Font, new Rectangle(r.Right - w, r.Y, w, r.Height), p.TextMuted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                r.Width -= w + 8;
            }
            title = c.Name;
            sub = c.Enabled ? $"コマンド · {c.Where}" : $"コマンド · {c.Where}（今は実行できません）";
        }
        else
        {
            var item = _results[e.Index - _commands.Count];
            title = item.Name;
            sub = item.Path;
        }
        using var bold = new Font(Font, FontStyle.Bold);
        var top = new Rectangle(r.X, r.Y, r.Width, r.Height / 2);
        var bottom = new Rectangle(r.X, r.Y + r.Height / 2, r.Width, r.Height / 2);
        TextRenderer.DrawText(e.Graphics, title, bold, top, fore, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, sub, Font, bottom, subColor, TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int i = IndexFromPoint(e.Location);
        if (i >= 0 && i < _commands.Count) CommandPicked?.Invoke(this, _commands[i]);
        else if (i >= _commands.Count && i < Count) Picked?.Invoke(this, _results[i - _commands.Count].Path);
    }
}
