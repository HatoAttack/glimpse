// 保存の画質: JPEG / WEBP で保存するときの画質（リサイズ・切り抜き・連結で共通）
using ImageViewer.App.Theming;
using ImageViewer.Core.Editing;

namespace ImageViewer.App.Dialogs;

public sealed class SaveQualityDialog : ThemedForm
{
    private readonly NumericUpDown _jpeg = QualityBox();
    private readonly NumericUpDown _webp = QualityBox();

    public int JpegQuality => (int)_jpeg.Value;
    public int WebpQuality => (int)_webp.Value;

    public SaveQualityDialog(int jpegQuality, int webpQuality)
    {
        Text = "保存の画質";
        Font = new Font("Yu Gothic UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _jpeg.Value = Math.Clamp(jpegQuality, ImageSaver.MinQuality, ImageSaver.MaxQuality);
        _webp.Value = Math.Clamp(webpQuality, ImageSaver.MinQuality, ImageSaver.MaxQuality);

        var reset = new Button { Text = $"既定に戻す（{ImageSaver.DefaultQuality}）", AutoSize = true };
        reset.Click += (_, _) => _jpeg.Value = _webp.Value = ImageSaver.DefaultQuality;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = ok;
        CancelButton = cancel;

        var fields = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
        fields.Controls.Add(FieldLabel("JPEG"), 0, 0);
        fields.Controls.Add(_jpeg, 1, 0);
        fields.Controls.Add(FieldLabel("WEBP"), 0, 1);
        fields.Controls.Add(_webp, 1, 1);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        var bottom = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.Controls.Add(reset, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(12) };
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(360, 0),
            Text = "JPEG / WEBP で保存するときの画質（1〜100）。大きいほどきれいで、ファイルも大きくなります。" +
                   "リサイズ・切り抜き・連結の保存に使います。",
        });
        layout.Controls.Add(fields);
        layout.Controls.Add(bottom);
        Controls.Add(layout);
    }

    private static NumericUpDown QualityBox() =>
        new() { Minimum = ImageSaver.MinQuality, Maximum = ImageSaver.MaxQuality, Width = 72, TextAlign = HorizontalAlignment.Right };

    private static Label FieldLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 3) };
}
