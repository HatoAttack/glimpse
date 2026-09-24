// 詳細パネル（一覧の右側のインスペクタと、1 枚表示の右側で共通）。文字だけ（画像はサムネイル・1 枚表示で見えているので出さない）。
// 1 枚: ファイル名・大きさ・形式・ファイルサイズ・更新日時・撮影情報・場所 / 複数: 枚数と合計サイズ / フォルダ: 名前と更新日時
using ImageViewer.App.Theming;
using ImageViewer.Core.Imaging;

namespace ImageViewer.App.Chrome;

public sealed class DetailsPanel : Control
{
    private string _title = "";
    private string _message = "";
    private IReadOnlyList<(string Label, string Value)> _rows = Array.Empty<(string, string)>();
    private string? _location;
    private readonly ToolTip _toolTip = new();

    /// <summary>決まった配色で描く（1 枚表示は常にダーク）。null なら今のテーマ</summary>
    public Palette? FixedPalette { get; set; }

    private Palette P => FixedPalette ?? Theme.Current;

    public DetailsPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Width = LogicalToDeviceUnits(264);
        ShowNothing();
    }

    /// <summary>何も選んでいないとき</summary>
    public void ShowNothing(string message = "画像を選ぶと、ここに詳細を表示します")
    {
        _title = "";
        _rows = Array.Empty<(string, string)>();
        _location = null;
        _message = message;
        Invalidate();
    }

    /// <param name="header">大きさ・形式・撮影情報（読んでいる間・読めなければ null）</param>
    public void ShowImage(FileInfo file, ImageHeader? header, bool loading)
    {
        var rows = new List<(string, string)>();
        if (header != null)
        {
            double mp = header.Width * (double)header.Height / 1_000_000;
            rows.Add(("大きさ", $"{header.Width} × {header.Height}（{mp:0.#} MP）"));
            rows.Add(("形式", header.Format));
        }
        else if (loading)
        {
            rows.Add(("大きさ", "読み込み中…"));
        }
        rows.Add(("ファイル", FormatBytes(SafeLength(file))));
        rows.Add(("更新", SafeTime(file)));
        if (header?.Photo is { } photo)
        {
            if (photo.TakenAt is DateTime taken) rows.Add(("撮影", $"{taken:yyyy/MM/dd HH:mm}"));
            if (photo.Camera is string camera) rows.Add(("カメラ", camera));
            if (photo.Lens is string lens) rows.Add(("レンズ", lens));
            if (photo.SettingsText is string settings) rows.Add(("設定", settings));
        }
        _title = file.Name;
        _rows = rows;
        _location = file.DirectoryName;
        _message = "";
        _toolTip.SetToolTip(this, file.FullName);
        Invalidate();
    }

    public void ShowMultiple(IReadOnlyList<FileInfo> files)
    {
        _title = $"{files.Count} 枚の画像";
        _rows = new[] { ("合計", FormatBytes(files.Sum(SafeLength))) };
        _location = files.Select(f => f.DirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? files[0].DirectoryName : null;
        _message = "";
        _toolTip.SetToolTip(this, "");
        Invalidate();
    }

    public void ShowFolder(DirectoryInfo folder)
    {
        _title = folder.Name;
        _rows = new[] { ("種類", "フォルダー"), ("更新", SafeTime(folder)) };
        _location = folder.Parent?.FullName;
        _message = "";
        _toolTip.SetToolTip(this, folder.FullName);
        Invalidate();
    }

    public static long SafeLength(FileInfo f)
    {
        try { return f.Length; }
        catch (IOException) { return 0; }
    }

    private static string SafeTime(FileSystemInfo f)
    {
        try { return $"{f.LastWriteTime:yyyy/MM/dd HH:mm}"; }
        catch (IOException) { return ""; }
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    private const TextFormatFlags Flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = P;
        var g = e.Graphics;
        g.Clear(p.Surface);
        using (var line = new Pen(p.Border)) g.DrawLine(line, 0, 0, 0, Height);

        int padX = LogicalToDeviceUnits(16), x = padX, y = LogicalToDeviceUnits(14);
        int width = Math.Max(0, Width - padX * 2);
        using var small = new Font(Font.FontFamily, Font.Size * 0.9f);
        TextRenderer.DrawText(g, "詳細", small, new Point(x, y), p.TextMuted, Flags);
        y += small.Height + LogicalToDeviceUnits(10);

        if (_message.Length > 0)
        {
            TextRenderer.DrawText(g, _message, Font, new Rectangle(x, y, width, Height - y), p.TextMuted, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        using var bold = new Font(Font.FontFamily, Font.Size * 1.1f, FontStyle.Bold);
        TextRenderer.DrawText(g, _title, bold, new Rectangle(x, y, width, bold.Height), p.Text, Flags | TextFormatFlags.EndEllipsis);
        y += bold.Height + LogicalToDeviceUnits(10);

        int rowHeight = LogicalToDeviceUnits(22), labelWidth = LogicalToDeviceUnits(56);
        void Row(string label, string value, TextFormatFlags ellipsis)
        {
            TextRenderer.DrawText(g, label, Font, new Rectangle(x, y, labelWidth, rowHeight), p.TextMuted, Flags | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, value, Font, new Rectangle(x + labelWidth, y, Math.Max(0, width - labelWidth), rowHeight), p.Text,
                Flags | TextFormatFlags.VerticalCenter | ellipsis);
            y += rowHeight;
        }
        foreach (var (label, value) in _rows) Row(label, value, TextFormatFlags.EndEllipsis);

        if (_location == null) return;
        y += LogicalToDeviceUnits(8);
        using (var line = new Pen(p.Border)) g.DrawLine(line, x, y, x + width, y);
        y += LogicalToDeviceUnits(8);
        Row("場所", _location, TextFormatFlags.PathEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
