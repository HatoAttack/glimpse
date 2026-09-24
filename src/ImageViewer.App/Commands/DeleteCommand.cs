// 削除（ごみ箱へ移動）。選択中の画像をまとめて 1 回のシェル操作で送る（ごみ箱から元に戻せる）
using System.Runtime.InteropServices;
using ImageViewer.Core.Commands;

namespace ImageViewer.App.Commands;

public sealed class DeleteCommand(Form owner) : ImageCommandBase
{
    public override string Id => "file.delete";
    public override string Name => "削除（ごみ箱へ）";
    public override string Category => "ファイル";
    public override string? DefaultShortcut => "Del";

    public override Task ExecuteAsync(CommandContext context)
    {
        var paths = context.Paths;
        string question = paths.Count == 1
            ? $"「{Path.GetFileName(paths[0])}」をごみ箱に移動しますか？"
            : $"選択中の {paths.Count} 枚の画像をごみ箱に移動しますか？";
        if (MessageBox.Show(owner, question, "削除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return Task.CompletedTask;

        RecycleBin.Send(paths, owner.Handle);
        // 途中で失敗・キャンセルされた分は残っているので、実際に無くなったものだけを反映する
        var deleted = paths.Where(p => !File.Exists(p)).ToList();
        if (deleted.Count > 0) context.Host.FilesDeleted(deleted);
        context.Host.Notify(deleted.Count == paths.Count
            ? $"{deleted.Count} 枚をごみ箱に移動しました"
            : $"{deleted.Count} / {paths.Count} 枚をごみ箱に移動しました（残りは削除できませんでした）");
        return Task.CompletedTask;
    }
}

/// <summary>SHFileOperation でごみ箱へ送る（追加のソフト不要。エクスプローラーと同じ動き）</summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_NOCONFIRMATION = 0x0010; // 確認はこちらで出している
    private const ushort FOF_ALLOWUNDO = 0x0040;      // ごみ箱へ
    private const ushort FOF_WANTNUKEWARNING = 0x4000; // ごみ箱に入らない（ネットワークドライブ・大きすぎる）ときは完全に消す前に確認

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>失敗・キャンセルは例外にしない（呼び出し側で残ったファイルを確かめる）</summary>
    public static void Send(IReadOnlyList<string> paths, IntPtr owner)
    {
        if (paths.Count == 0) return;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            // パスは \0 区切り、最後は \0\0（文字列のマーシャリングで末尾に 1 つ付くので 1 つ足す）
            pFrom = string.Join('\0', paths.Select(Path.GetFullPath)) + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING,
        };
        SHFileOperation(ref op);
    }
}
