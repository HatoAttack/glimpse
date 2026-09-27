// 画像ファイルの中身を読む口。普通のファイルはそのまま、ZIP の中の画像はメモリに取り出して読む
using ImageViewer.Core.Archives;

namespace ImageViewer.Core.Imaging;

public static class ImageSource
{
    /// <summary>読み取り用に開く（先頭へ戻せる Stream）</summary>
    public static Stream OpenRead(string path) =>
        ArchivePath.IsInside(path) ? new MemoryStream(ZipStore.Read(path), writable: false) : File.OpenRead(path);
}
