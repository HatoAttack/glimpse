// Everything（voidtools）への問い合わせ。設定で「Everything を使う」を選んだ人だけが使う任意の機能。
// Everything 本体・SDK の DLL は同梱しない。起動中の Everything に Windows のメッセージ（WM_COPYDATA）で
// 検索を頼み、結果を受け取るだけ（Everything の公開 IPC 仕様 v1）。起動していなければ null を返し、呼び出し側は自前の索引を使う
using System.Runtime.InteropServices;
using System.Text;

namespace ImageViewer.App.Jump;

public sealed class EverythingClient : NativeWindow, IDisposable
{
    private const int WM_COPYDATA = 0x004A;
    private const int COPYDATA_QUERYW = 2;
    private const int FolderFlag = 0x1;
    private readonly object _pendingLock = new();
    private int _nextReplyId = 0x49560000;
    private int _pendingReplyId;
    private TaskCompletionSource<List<string>?>? _pending;

    public EverythingClient()
    {
        // 画面に出ない、メッセージを受けるだけのウィンドウ
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
    }

    public static bool IsRunning => FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null) != IntPtr.Zero;

    /// <summary>フォルダを検索。Everything が起動していない・応答しない・失敗したときは null</summary>
    public async Task<List<string>?> QueryFoldersAsync(string query, int maxResults, TimeSpan timeout)
    {
        var everything = FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null);
        if (everything == IntPtr.Zero) return null;

        TaskCompletionSource<List<string>?> tcs;
        int replyId;
        lock (_pendingLock)
        {
            _pending?.TrySetResult(null); // 前の問い合わせの返事はもう待たない
            tcs = _pending = new TaskCompletionSource<List<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            replyId = _pendingReplyId = unchecked(++_nextReplyId);
        }

        try
        {
            // EVERYTHING_IPC_QUERYW: reply_hwnd, reply_copydata_message, search_flags, offset, max_results（各 32bit）＋ 検索文字列
            string search = "folder: " + query;
            byte[] text = Encoding.Unicode.GetBytes(search + "\0");
            var buffer = new byte[20 + text.Length];
            BitConverter.GetBytes((uint)Handle.ToInt64()).CopyTo(buffer, 0);
            BitConverter.GetBytes(replyId).CopyTo(buffer, 4);
            BitConverter.GetBytes(0).CopyTo(buffer, 8);
            BitConverter.GetBytes(0).CopyTo(buffer, 12);
            BitConverter.GetBytes(maxResults).CopyTo(buffer, 16);
            text.CopyTo(buffer, 20);

            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var cds = new COPYDATASTRUCT { dwData = COPYDATA_QUERYW, cbData = buffer.Length, lpData = pin.AddrOfPinnedObject() };
                if (SendMessage(everything, WM_COPYDATA, Handle, ref cds) == IntPtr.Zero) return null;
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                return null;
            }
            finally
            {
                pin.Free();
            }

            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            return done == tcs.Task ? tcs.Task.Result : null;
        }
        finally
        {
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pending, tcs)) _pending = null;
            }
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_COPYDATA)
        {
            var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(m.LParam);
            TaskCompletionSource<List<string>?>? pending;
            lock (_pendingLock)
                pending = cds.dwData == new IntPtr(_pendingReplyId) ? _pending : null;
            if (pending != null)
            {
                pending.TrySetResult(ParseList(cds.lpData, cds.cbData));
                m.Result = new IntPtr(1);
                return;
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>EVERYTHING_IPC_LISTW を読む（7 個の 32bit の後に、項目ごとに flags・名前の位置・フォルダの位置）</summary>
    private static List<string>? ParseList(IntPtr list, int size)
    {
        try
        {
            if (size < 28) return null;
            int count = Marshal.ReadInt32(list, 20); // numitems
            if (count < 0 || 28 + count * 12 > size) return null;
            var result = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                int item = 28 + i * 12;
                int flags = Marshal.ReadInt32(list, item);
                int nameOffset = Marshal.ReadInt32(list, item + 4);
                int pathOffset = Marshal.ReadInt32(list, item + 8);
                if ((flags & FolderFlag) == 0 || nameOffset <= 0 || pathOffset <= 0 || nameOffset >= size || pathOffset >= size) continue;
                string name = Marshal.PtrToStringUni(list + nameOffset) ?? "";
                string parent = Marshal.PtrToStringUni(list + pathOffset) ?? "";
                if (name.Length > 0) result.Add(parent.Length > 0 ? Path.Combine(parent, name) : name);
            }
            return result;
        }
        catch (Exception ex) when (ex is AccessViolationException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_pendingLock)
        {
            _pending?.TrySetResult(null);
            _pending = null;
        }
        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam);
}
