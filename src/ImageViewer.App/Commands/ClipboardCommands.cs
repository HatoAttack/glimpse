// コピー / 切り取り / 貼り付け。クリップボードはエクスプローラーと同じ形式（ファイルの一覧 + コピーか移動か）なので、
// エクスプローラーとの間でもそのまま貼り付けられる
using System.Collections.Specialized;
using ImageViewer.Core.Archives;
using ImageViewer.Core.Commands;
using ImageViewer.Core.Imaging;

namespace ImageViewer.App.Commands;

/// <summary>お知らせ・確認に出す数え方</summary>
internal static class FileWording
{
    /// <summary>画像だけなら「3 枚」、画像以外のファイル・フォルダ（ZIP のタイルも）が混ざっていれば「3 件」</summary>
    public static string Count(IReadOnlyList<string> paths) =>
        paths.All(p => ImageFormats.IsSupported(p) && !Directory.Exists(p)) ? $"{paths.Count} 枚" : $"{paths.Count} 件";

    /// <summary>「3 枚の画像」/「3 件のファイル」/ フォルダが混ざっていれば「3 件の項目」</summary>
    public static string CountWithNoun(IReadOnlyList<string> paths) =>
        paths.Any(Directory.Exists) ? $"{paths.Count} 件の項目"
        : paths.All(ImageFormats.IsSupported) ? $"{paths.Count} 枚の画像" : $"{paths.Count} 件のファイル";
}

internal static class FileClipboard
{
    private const string DropEffectFormat = "Preferred DropEffect";
    private const int DropEffectCopy = 1, DropEffectMove = 2;

    public static void Set(IReadOnlyList<string> paths, bool cut)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes(cut ? DropEffectMove : DropEffectCopy)));
        Clipboard.SetDataObject(data, copy: true);
    }

    private const uint CF_HDROP = 15;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    /// <summary>
    /// 選択が変わるたびに呼ばれるので、クリップボードを開かずに形式だけを見る
    /// （Clipboard.ContainsFileDropList より軽く、ほかのアプリが使用中でも失敗しない）
    /// </summary>
    public static bool HasFiles => IsClipboardFormatAvailable(CF_HDROP);

    /// <summary>クリップボードのファイルと、切り取りかどうか（エクスプローラーのコピーは 5 = コピー | リンク、切り取りは 2）</summary>
    public static (IReadOnlyList<string> Paths, bool Cut) Get()
    {
        var paths = Clipboard.GetFileDropList().Cast<string>().ToList();
        bool cut = false;
        if (Clipboard.GetData(DropEffectFormat) is MemoryStream ms && ms.Length >= 4)
        {
            var bytes = new byte[4];
            ms.ReadExactly(bytes);
            int effect = BitConverter.ToInt32(bytes);
            cut = (effect & DropEffectMove) != 0 && (effect & DropEffectCopy) == 0;
        }
        return (paths, cut);
    }
}

/// <summary>
/// 選択中のフォルダもフォルダごとコピーする（エクスプローラーや FTP ソフトへ貼り付けられる）。
/// ZIP の中の画像は、一時フォルダへ書き出したものをクリップボードに載せる
/// </summary>
public sealed class CopyFilesCommand : ImageCommandBase, IWorksInArchive, IWorksOnFolders, IWorksOnAnyFile
{
    public override string Id => "edit.copy";
    public override string Name => "コピー";
    public override string Category => "編集";
    public override string? DefaultShortcut => "Ctrl+C";

    public override async Task ExecuteAsync(CommandContext context)
    {
        var paths = await Task.Run(() => ArchiveExport.ToFiles(context.Paths));
        FileClipboard.Set(paths, cut: false);
        // フォルダ（ZIP のタイルも）・画像以外のファイルを含むときは枚数ではなく件数で
        context.Host.Notify($"{FileWording.Count(context.Paths)}をコピーしました（貼り付けは Ctrl+V。エクスプローラーにも貼り付けられます）");
    }
}

/// <summary>選択中のフォルダ（ZIP のタイルも）もフォルダごと切り取る</summary>
public sealed class CutFilesCommand : ImageCommandBase, IWorksOnAnyFile, IWorksOnFolders
{
    public override string Id => "edit.cut";
    public override string Name => "切り取り";
    public override string Category => "編集";
    public override string? DefaultShortcut => "Ctrl+X";

    public override Task ExecuteAsync(CommandContext context)
    {
        FileClipboard.Set(context.Paths, cut: true);
        context.Host.Notify($"{FileWording.Count(context.Paths)}を切り取りました（移動先のフォルダで Ctrl+V）");
        return Task.CompletedTask;
    }
}

/// <summary>クリップボードのファイル・フォルダを表示中のフォルダへ。選択は使わない</summary>
public sealed class PasteFilesCommand(Form owner) : IImageCommand
{
    public string Id => "edit.paste";
    public string Name => "貼り付け";
    public string Category => "編集";
    public string? DefaultShortcut => "Ctrl+V";

    public bool CanExecute(IReadOnlyList<string> paths) => FileClipboard.HasFiles;

    public async Task ExecuteAsync(CommandContext context)
    {
        if (context.Host.CurrentFolder is not string folder) return;
        var (sources, cut) = FileClipboard.Get();
        sources = sources.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (sources.Count == 0)
        {
            context.Host.Notify("貼り付けるファイルが見つかりません");
            return;
        }
        // 移動 / コピー・画面への反映・結果の表示はドラッグ＆ドロップと共通（増えたもの・置き換わったものを選択）
        int done = await FileTransfer.RunAsync(context.Host, sources, folder, cut, owner.Handle);
        // 切り取りは貼り付けたら終わり（エクスプローラーと同じく、もう一度は貼り付けない）
        if (cut && done > 0) Clipboard.Clear();
    }
}
