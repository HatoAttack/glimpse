// フッター（高さ固定）。左に件数・お知らせとチェックの数、右に選択中の画像の情報・更新のお知らせ・サムネイルの大きさ・
// ライト / ダークの切り替え。
// 画像を選んでいる間は、左側（件数・情報）が操作ボタン（ActionBar）に入れ替わる。
// 文字は自分で描く（配色に合わせるため）。高さは状態によって変えない（一覧がずれないように）
namespace ImageViewer.App.Chrome;

public sealed class FooterBar : Control
{
    private string _status = "", _selectionInfo = "", _updateText = "";
    private int _checkCount;
    private Rectangle _updateBounds;
    private readonly ToolTip _toolTip = new();

    public ThinSlider Slider { get; } = new();

    /// <summary>右端のライト / ダークの切り替え</summary>
    public IconButton ThemeButton { get; } = new();

    /// <summary>画像を選んでいる間に出す操作ボタン</summary>
    public ActionBar Actions { get; } = new() { Visible = false };

    private bool _actionMode;

    /// <summary>操作ボタンを出す（画像を選んでいる間）。高さは変えない</summary>
    public bool ActionMode
    {
        get => _actionMode;
        set
        {
            if (_actionMode == value) return;
            _actionMode = value;
            Actions.Visible = value;
            PerformLayout();
            Invalidate();
        }
    }

    /// <summary>更新のお知らせがクリックされた</summary>
    public event EventHandler? UpdateClicked;

    public FooterBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Dock = DockStyle.Bottom;
        Height = LogicalToDeviceUnits(32);
        Controls.Add(Slider);
        Controls.Add(ThemeButton);
        Controls.Add(Actions);
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        bool dark = Theming.Theme.Current.IsDark;
        BackColor = Theming.Theme.Current.Surface;
        // アイコンは今の配色、説明は押したときにどうなるか
        ThemeButton.Icon = dark ? Icons.Moon : Icons.Sun;
        ThemeButton.AccessibleName = dark ? "ライトに切り替え" : "ダークに切り替え";
        _toolTip.SetToolTip(ThemeButton, dark ? "ライトに切り替え" : "ダークに切り替え");
        Invalidate(true);
    }

    /// <summary>件数、またはお知らせ（次に選択や中身が変わるまで表示）</summary>
    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; Invalidate(); } }
    }

    public int CheckCount
    {
        get => _checkCount;
        set { if (_checkCount != value) { _checkCount = value; Invalidate(); } }
    }

    public string SelectionInfo
    {
        get => _selectionInfo;
        set
        {
            if (_selectionInfo == value) return;
            _selectionInfo = value;
            Actions.Info = value;
            Invalidate();
        }
    }

    /// <summary>新しいバージョンのお知らせ（空なら出さない）</summary>
    public string UpdateText
    {
        get => _updateText;
        set { if (_updateText != value) { _updateText = value; Invalidate(); } }
    }

    public void SetSliderToolTip(string text) => _toolTip.SetToolTip(Slider, text);

    private int Pad => LogicalToDeviceUnits(12);
    private int SliderWidth => LogicalToDeviceUnits(120);
    private int IconSize => LogicalToDeviceUnits(14);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int button = LogicalToDeviceUnits(26);
        int right = Width - LogicalToDeviceUnits(4);
        ThemeButton.SetBounds(right - button, (Height - button) / 2, button, button);
        right = ThemeButton.Left - LogicalToDeviceUnits(12);
        Slider.SetBounds(right - SliderWidth, 0, SliderWidth, Height);
        // 操作ボタンは左端から、スライダーのアイコンの手前の区切り線まで
        int actionsRight = Slider.Left - LogicalToDeviceUnits(8) - IconSize - LogicalToDeviceUnits(14);
        Actions.SetBounds(LogicalToDeviceUnits(4), 0, Math.Max(0, actionsRight - LogicalToDeviceUnits(4)), Height);
        if (Actions.Visible) Actions.PerformLayout();
    }

    private const TextFormatFlags Flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theming.Theme.Current;
        var g = e.Graphics;
        using (var line = new Pen(p.Border)) g.DrawLine(line, 0, 0, Width, 0);

        int gap = LogicalToDeviceUnits(16);
        // 右から: スライダー → アイコン → 選択中の情報 → 更新のお知らせ
        int right = Slider.Left - LogicalToDeviceUnits(8);
        Icons.Grid(g, new RectangleF(right - IconSize, (Height - IconSize) / 2f, IconSize, IconSize), p.TextSecondary);
        right -= IconSize + gap;
        if (_actionMode)
        {
            // 左側は ActionBar が描く。削除とスライダーの間は区切り線で離す
            int x0 = Actions.Right + LogicalToDeviceUnits(7), h = LogicalToDeviceUnits(18);
            using var sep = new Pen(p.Border);
            g.DrawLine(sep, x0, (Height - h) / 2, x0, (Height + h) / 2);
            _updateBounds = Rectangle.Empty;
            return;
        }

        int maxInfo = Math.Max(0, Width / 3);
        if (_selectionInfo.Length > 0)
        {
            int w = Math.Min(maxInfo, TextRenderer.MeasureText(g, _selectionInfo, Font, Size.Empty, Flags).Width);
            TextRenderer.DrawText(g, _selectionInfo, Font, new Rectangle(right - w, 0, w, Height), p.TextSecondary, Flags | TextFormatFlags.EndEllipsis);
            right -= w + gap;
        }

        _updateBounds = Rectangle.Empty;
        if (_updateText.Length > 0)
        {
            int w = Math.Min(maxInfo, TextRenderer.MeasureText(g, _updateText, Font, Size.Empty, Flags).Width);
            _updateBounds = new Rectangle(right - w, 0, w, Height);
            TextRenderer.DrawText(g, _updateText, Font, _updateBounds, p.SelectionBorder, Flags | TextFormatFlags.EndEllipsis);
            right -= w + gap;
        }

        // 左: 件数・お知らせ、チェックの数
        int x = Pad;
        string check = _checkCount > 0 ? $"チェック {_checkCount}" : "";
        int checkWidth = check.Length > 0 ? LogicalToDeviceUnits(14) + TextRenderer.MeasureText(g, check, Font, Size.Empty, Flags).Width : 0;
        int statusMax = Math.Max(0, right - x - (checkWidth > 0 ? checkWidth + gap : 0));
        int statusWidth = Math.Min(statusMax, TextRenderer.MeasureText(g, _status, Font, Size.Empty, Flags).Width);
        TextRenderer.DrawText(g, _status, Font, new Rectangle(x, 0, statusWidth, Height), p.TextSecondary, Flags | TextFormatFlags.EndEllipsis);
        x += statusWidth + gap;
        if (checkWidth > 0 && x + checkWidth <= right)
        {
            int dot = LogicalToDeviceUnits(8);
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(p.Check)) g.FillEllipse(b, x, (Height - dot) / 2f, dot, dot);
            g.SmoothingMode = old;
            TextRenderer.DrawText(g, check, Font, new Rectangle(x + LogicalToDeviceUnits(14), 0, checkWidth, Height), p.CheckText, Flags);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over = _updateBounds.Contains(e.Location);
        Cursor = over ? Cursors.Hand : Cursors.Default;
        string tip = over ? "クリックすると変更内容を表示して更新できます" : "";
        if (_toolTip.GetToolTip(this) != tip) _toolTip.SetToolTip(this, tip);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && _updateBounds.Contains(e.Location)) UpdateClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
