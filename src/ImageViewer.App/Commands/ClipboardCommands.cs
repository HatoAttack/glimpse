// コピー / 切り取り / 貼り付け。クリップボードはエクスプローラーと同じ形式（ファイルの一覧 + コピーか移動か）なので、
// エクスプローラーとの間でもそのまま貼り付けられる
using System.Collections.Specialized;
using System.Runtime.InteropServices;
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

public static class FileClipboard
{
    private const string DropEffectFormat = "Preferred DropEffect";
    private const int DropEffectCopy = 1, DropEffectMove = 2;

    private const int CLIPBRD_E_CANT_OPEN = unchecked((int)0x800401D0);

    /// <summary>
    /// ほかのアプリ（クリップボードの履歴など）が開いたままだと、.NET が 1 秒ほど取り直しても
    /// CLIPBRD_E_CANT_OPEN で失敗することがあるので、少し待ってから何度か取り直す
    /// </summary>
    public static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (ExternalException ex) when (ex.HResult == CLIPBRD_E_CANT_OPEN && attempt < 4)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>クリップボードを空にする（切り取りを貼り付けたあと）</summary>
    public static void Clear() => Retry(Clipboard.Clear);

    public static void Set(IReadOnlyList<string> paths, bool cut)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes(cut ? DropEffectMove : DropEffectCopy)));
        Retry(() => Clipboard.SetDataObject(data, copy: true));
    }

    private const uint CF_HDROP = 15;
    private static readonly uint DropEffectId = RegisterClipboardFormat(DropEffectFormat);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, char[]? file, uint length);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr mem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr mem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr mem);

    /// <summary>
    /// 選択が変わるたびに呼ばれるので、クリップボードを開かずに形式だけを見る
    /// （Clipboard.ContainsFileDropList より軽く、ほかのアプリが使用中でも失敗しない）
    /// </summary>
    public static bool HasFiles => IsClipboardFormatAvailable(CF_HDROP);

    /// <summary>
    /// クリップボードのファイルと、切り取りかどうか（エクスプローラーのコピーは 5 = コピー | リンク、切り取りは 2）。
    /// .NET 10 の Clipboard.GetFileDropList は、ときどき一覧の途中まで（空のことも）しか返さない（すぐ読み直すとそろう）ので、
    /// クリップボードを 1 度だけ開いて、ファイルの一覧とコピーか移動かを直接読む
    /// </summary>
    public static (IReadOnlyList<string> Paths, bool Cut) Get()
    {
        // ほかのアプリが開いたままのあいだは、2 秒ほど取り直す
        for (int attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
        {
            if (attempt >= 100) throw new ExternalException("ほかのアプリがクリップボードを使用中のため、開けませんでした", CLIPBRD_E_CANT_OPEN);
            Thread.Sleep(20);
        }
        try
        {
            var paths = new List<string>();
            var drop = GetClipboardData(CF_HDROP);
            if (drop != IntPtr.Zero)
            {
                uint count = DragQueryFile(drop, uint.MaxValue, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    var file = new char[DragQueryFile(drop, i, null, 0) + 1];
                    uint length = DragQueryFile(drop, i, file, (uint)file.Length);
                    paths.Add(new string(file, 0, (int)length));
                }
            }
            bool cut = false;
            var effectData = GetClipboardData(DropEffectId);
            if (effectData != IntPtr.Zero && GlobalSize(effectData) >= 4 && GlobalLock(effectData) is var p && p != IntPtr.Zero)
            {
                int effect = Marshal.ReadInt32(p);
                GlobalUnlock(effectData);
                cut = (effect & DropEffectMove) != 0 && (effect & DropEffectCopy) == 0;
            }
            return (paths, cut);
        }
        finally
        {
            CloseClipboard();
        }
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
        if (cut && done > 0) FileClipboard.Clear();
    }
}
