// 手動の並び順の保存先（%LOCALAPPDATA%\ima-ge-viewer\orders\*.json）
// - 画像フォルダには何も書かない（読み取り専用・ネットワーク上のフォルダでも使える）
// - フォルダは NTFS のフォルダ ID で探すので、同じドライブ内でフォルダ名を変えたり移動したりしても引き継げる。
//   ID が取れないドライブ（FAT の USB メモリ等）ではパスで探す
// - 1 フォルダ数 KB（1 万枚でも数百 KB）。上限件数を超えたら古いものから消す
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ImageViewer.Core.Ordering;

public sealed class FolderOrderStore
{
    private readonly string _root;
    private readonly int _maxEntries;

    private sealed record Entry(string Folder, List<string> Names);

    public FolderOrderStore(string root, int maxEntries = 1000)
    {
        _root = root;
        _maxEntries = maxEntries;
    }

    public static FolderOrderStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ima-ge-viewer", "orders"));

    /// <summary>保存した並び（ファイル名の列）。無ければ null</summary>
    public IReadOnlyList<string>? Load(string folder)
    {
        foreach (var path in CandidatePaths(folder))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
                if (entry == null) continue;
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // 最近使ったものとして残す
                return entry.Names;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // 壊れた・読めない保存は無かったことにする
            }
        }
        return null;
    }

    public void Save(string folder, IReadOnlyList<string> names)
    {
        Directory.CreateDirectory(_root);
        var paths = CandidatePaths(folder).ToList();
        Settings.AtomicFile.WriteAllText(paths[0], JsonSerializer.Serialize(new Entry(folder, names.ToList())));
        // ID で保存できたら、以前パスで保存したものは消す
        foreach (var old in paths.Skip(1)) TryDelete(old);
        Prune();
    }

    public void Delete(string folder)
    {
        foreach (var path in CandidatePaths(folder)) TryDelete(path);
    }

    /// <summary>上限件数を超えた分を、最後に使った日時の古い順に消す</summary>
    private void Prune()
    {
        var files = new DirectoryInfo(_root).GetFiles("*.json");
        // 書き込み途中で落ちたときの一時ファイルが残っていれば消す
        foreach (var tmp in new DirectoryInfo(_root).GetFiles("*.json.tmp")) TryDelete(tmp.FullName);
        if (files.Length <= _maxEntries) return;
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - _maxEntries))
            TryDelete(f.FullName);
    }

    /// <summary>探す順: フォルダ ID → パス</summary>
    private IEnumerable<string> CandidatePaths(string folder)
    {
        if (FolderId(folder) is string id) yield return Path.Combine(_root, $"id-{id}.json");
        string full = Path.GetFullPath(folder).TrimEnd('\\').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
        yield return Path.Combine(_root, $"path-{hash}.json");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- NTFS のフォルダ ID（ボリュームのシリアル番号 + ファイル番号） ----

    /// <summary>フォルダ ID。取れなければ null（FAT 系・一部のネットワークドライブ等）</summary>
    public static string? FolderId(string folder)
    {
        using var handle = CreateFile(folder, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
        ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        // FAT ではファイル番号が安定しない（0 のこともある）ので使わない
        if (index == 0 || !IsNtfsLike(folder)) return null;
        return $"{info.VolumeSerialNumber:X8}-{index:X16}";
    }

    private static bool IsNtfsLike(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (root == null || root.StartsWith(@"\\")) return false; // ネットワークは ID が保証されない
            string fs = new DriveInfo(root).DriveFormat;
            return fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    private const uint OPEN_EXISTING = 3, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);
}
