// 一覧に並ぶ画像 1 枚（パス・大きさ・更新日時）。普通のファイルのほか、ZIP の中の画像も表す
// （ZIP の中は「C:\…\book.zip\sub\001.jpg」のように ZIP をフォルダと見なしたパス。FileInfo は実在するファイルしか扱えない）
namespace ImageViewer.Core.Imaging;

public sealed record ImageFile(string FullName, long Length, DateTime LastWriteTimeUtc)
{
    public string Name { get; } = Path.GetFileName(FullName);

    public string Extension => Path.GetExtension(FullName);

    public string? DirectoryName => Path.GetDirectoryName(FullName);

    public DateTime LastWriteTime => LastWriteTimeUtc.ToLocalTime();

    /// <summary>列挙した FileInfo から（大きさ・更新日時は列挙時に読んであるので、ディスクは読まない）</summary>
    public static ImageFile From(FileInfo file) => new(file.FullName, SafeLength(file), file.LastWriteTimeUtc);

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
        catch (IOException) { return 0; }
    }
}
