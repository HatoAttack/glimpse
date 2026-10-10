// 一覧に並ぶ画像 1 枚（パス・大きさ・更新日時）。普通のファイルのほか、ZIP の中の画像も表す。
// 「画像以外のファイルも表示」の設定のときは、画像以外のファイルもこれで表す（IsImage が false）
// （ZIP の中は「C:\…\book.zip\sub\001.jpg」のように ZIP をフォルダと見なしたパス。FileInfo は実在するファイルしか扱えない）
namespace ImageViewer.Core.Imaging;

public sealed record ImageFile(string FullName, long Length, DateTime LastWriteTimeUtc)
{
    public string Name { get; } = Path.GetFileName(FullName);

    /// <summary>
    /// 中身の目印（ZIP の中の画像は CRC-32、普通のファイルは 0）。ZIP のツールは中のファイルの日時を保つことが多いので、
    /// 名前・大きさ・日時が同じでも中身が変わったことを、これで見分ける
    /// </summary>
    public long Version { get; init; }

    /// <summary>対応している画像か（false は画像以外のファイル。1 枚表示・画像の編集の対象にしない）</summary>
    public bool IsImage { get; } = ImageFormats.IsSupported(FullName);

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
