// アプリのアイコン（exe に埋め込んだ Glimpse.ico）。メイン画面とダイアログのタイトルバー・タスクバーに使う
namespace ImageViewer.App;

internal static class AppIcon
{
    private static Icon? _current;

    public static Icon Current => _current ??= new Icon(typeof(AppIcon).Assembly.GetManifestResourceStream("Glimpse.ico")!);
}
