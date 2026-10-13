// ライセンスの表示（☰ → ヘルプ →「ライセンス」）。exe だけを持っている人にも見えるように、
// Glimpse の LICENSE と同梱しているソフトウェアのライセンス（THIRD-PARTY-NOTICES.txt）を exe に埋め込んで出す。
// .NET Runtime の Third-party notices は、.NET Runtime を同梱するビルドでだけ、同梱した版のものが埋め込まれる（csproj の EmbedRuntimeNotices）
using System.Diagnostics;
using ImageViewer.App.Theming;
using ImageViewer.Core.Updates;

namespace ImageViewer.App.Dialogs;

public sealed class LicenseDialog : ThemedForm
{
    private const string RepositoryUrl = "https://github.com/HatoAttack/glimpse";

    private const string Rule = "================================================================================";

    /// <summary>表示する全文（Glimpse のライセンス → 同梱しているソフトウェアのライセンス → .NET Runtime の Third-party notices）。改行は CRLF にそろえる</summary>
    public static string LicenseText
    {
        get
        {
            string runtime = ReadResource("RUNTIME-THIRD-PARTY-NOTICES.txt")
                             ?? "このビルドには .NET Runtime が含まれていません（PC に入っている .NET で動いています）。";
            string text = $"{ReadResource("LICENSE.txt")}\n\n\n{ReadResource("THIRD-PARTY-NOTICES.txt")}\n\n\n"
                          + $"{Rule}\n.NET Runtime - Third-party notices\n{Rule}\n\n{runtime}";
            return text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
    }

    public LicenseDialog(Version version)
    {
        Text = "ライセンス";
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 480);
        MinimumSize = new Size(380, 300);

        var heading = new Label
        {
            Text = $"Glimpse v{UpdateChecker.Normalize(version)} は MIT License で公開しています。\n一緒に含まれている他のソフトウェアのライセンスと著作権表示も下に載せています。",
            AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 6),
        };
        var text = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F), Text = LicenseText, BackColor = SystemColors.Window,
            TabStop = false, // 最初にフォーカスが入ると全体が選択された状態で開いてしまう
        };
        var pageLink = new LinkLabel { Text = RepositoryUrl, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 6, 0, 0) };
        pageLink.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        };

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0) };
        body.Controls.Add(text);
        body.Controls.Add(pageLink);
        body.Controls.Add(heading);
        var close = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.OK };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(8),
        };
        buttons.Controls.Add(close);
        Controls.Add(body);
        Controls.Add(buttons);
        AcceptButton = close;
        CancelButton = close;
    }

    /// <summary>埋め込んだ文書（ないときは null）</summary>
    private static string? ReadResource(string name)
    {
        using var stream = typeof(LicenseDialog).Assembly.GetManifestResourceStream(name);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimEnd();
    }
}
