// SHFileOperation によるファイル操作（追加のソフト不要。エクスプローラーと同じ確認・進行状況・上書きの確認が出る）
using System.Runtime.InteropServices;

namespace ImageViewer.App.Commands;

internal static class ShellFileOps
{
    private const uint FO_MOVE = 0x0001;
    private const uint FO_COPY = 0x0002;
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_RENAMEONCOLLISION = 0x0008; // 同じ名前があれば「- コピー」を付ける
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;          // 削除はごみ箱へ・エクスプローラーの「元に戻す」に載る
    private const ushort FOF_WANTNUKEWARNING = 0x4000;    // ごみ箱に入らない（ネットワークドライブ・大きすぎる）ときは完全に消す前に確認

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

    /// <summary>ごみ箱へ送る。確認はこちらで出しているので Windows の確認は出さない</summary>
    public static void Recycle(IReadOnlyList<string> paths, IntPtr owner) =>
        Run(FO_DELETE, paths, null, FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING, owner);

    /// <summary>フォルダへコピー。同じ名前があるときは、同じフォルダ内なら「- コピー」を付け、別のフォルダなら Windows の上書きの確認を出す</summary>
    public static void Copy(IReadOnlyList<string> paths, string folder, bool renameOnCollision, IntPtr owner) =>
        Run(FO_COPY, paths, folder, (ushort)(FOF_ALLOWUNDO | (renameOnCollision ? FOF_RENAMEONCOLLISION : 0)), owner);

    /// <summary>フォルダへ移動。同じ名前があるときは Windows の上書きの確認を出す</summary>
    public static void Move(IReadOnlyList<string> paths, string folder, IntPtr owner) =>
        Run(FO_MOVE, paths, folder, FOF_ALLOWUNDO, owner);

    /// <summary>失敗・キャンセルは例外にしない（呼び出し側で実際の結果をファイルの有無で確かめる）</summary>
    private static void Run(uint func, IReadOnlyList<string> paths, string? to, ushort flags, IntPtr owner)
    {
        if (paths.Count == 0) return;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = func,
            // パスは \0 区切り、最後は \0\0（文字列のマーシャリングで末尾に 1 つ付くので 1 つ足す）
            pFrom = string.Join('\0', paths.Select(Path.GetFullPath)) + '\0',
            pTo = to == null ? null : Path.GetFullPath(to) + '\0',
            fFlags = flags,
        };
        SHFileOperation(ref op);
    }

    /// <summary>
    /// 別スレッド（STA）で実行する。大きなコピーの間も一覧の画面が固まらない
    /// （進行状況・確認のダイアログは Windows が出す）
    /// </summary>
    public static Task RunInBackground(Action action)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }) { IsBackground = true, Name = "ファイル操作" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
