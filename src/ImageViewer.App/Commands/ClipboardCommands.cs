// コピー / 切り取り / 貼り付け。クリップボードはエクスプローラーと同じ形式（ファイルの一覧 + コピーか移動か）なので、
// エクスプローラーとの間でもそのまま貼り付けられる
using System.Collections.Specialized;
using ImageViewer.Core.Commands;

namespace ImageViewer.App.Commands;

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

public sealed class CopyFilesCommand : ImageCommandBase
{
    public override string Id => "edit.copy";
    public override string Name => "コピー";
    public override string Category => "編集";
    public override string? DefaultShortcut => "Ctrl+C";

    public override Task ExecuteAsync(CommandContext context)
    {
        FileClipboard.Set(context.Paths, cut: false);
        context.Host.Notify($"{context.Paths.Count} 枚をコピーしました（貼り付けは Ctrl+V。エクスプローラーにも貼り付けられます）");
        return Task.CompletedTask;
    }
}

public sealed class CutFilesCommand : ImageCommandBase
{
    public override string Id => "edit.cut";
    public override string Name => "切り取り";
    public override string Category => "編集";
    public override string? DefaultShortcut => "Ctrl+X";

    public override Task ExecuteAsync(CommandContext context)
    {
        FileClipboard.Set(context.Paths, cut: true);
        context.Host.Notify($"{context.Paths.Count} 枚を切り取りました（移動先のフォルダで Ctrl+V）");
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
        // 無いフォルダを貼り付け先にすると、その名前のファイルとして移動・コピーされてしまう
        if (!Directory.Exists(folder))
        {
            context.Host.Notify($"貼り付け先のフォルダが見つかりません: {folder}");
            return;
        }
        var (sources, cut) = FileClipboard.Get();
        sources = sources.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        bool SameFolder(string p) =>
            string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)) is string d ? Path.TrimEndingDirectorySeparator(d) : null,
                target, StringComparison.OrdinalIgnoreCase);
        // 同じフォルダへの移動は何もしない（エクスプローラーと同じ）
        if (cut) sources = sources.Where(p => !SameFolder(p)).ToList();
        if (sources.Count == 0)
        {
            context.Host.Notify(cut ? "切り取ったファイルは既にこのフォルダにあります" : "貼り付けるファイルが見つかりません");
            return;
        }

        var before = Entries(folder);
        IntPtr handle = owner.Handle;
        bool renameOnCollision = sources.Any(SameFolder); // 同じフォルダへのコピーは「- コピー」を付けて複製
        await ShellFileOps.RunInBackground(() =>
        {
            if (cut) ShellFileOps.Move(sources, folder, handle);
            else ShellFileOps.Copy(sources, folder, renameOnCollision, handle);
        });

        // 増えたもの＋同じ名前で上書きしたものを選択する
        var added = Entries(folder).Where(p => !before.Contains(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var src in sources)
        {
            string dest = Path.Combine(folder, Path.GetFileName(src));
            if (!added.Contains(dest) && !SameFolder(src) && (File.Exists(dest) || Directory.Exists(dest))) added.Add(dest);
        }
        // 切り取りは貼り付けたら終わり（エクスプローラーと同じく、もう一度は貼り付けない）
        if (cut && sources.Any(p => !File.Exists(p) && !Directory.Exists(p))) Clipboard.Clear();
        await context.Host.FilesAddedAsync(folder, added.ToList());
        context.Host.Notify(added.Count == 0 ? "貼り付けませんでした"
            : $"{added.Count} 件を{(cut ? "移動" : "コピー")}しました");
    }

    private static HashSet<string> Entries(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
