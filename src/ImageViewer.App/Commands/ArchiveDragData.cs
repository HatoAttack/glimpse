// ZIP の中の画像をほかのアプリへドラッグするときのデータ
// ファイルの一覧は、落とす先が本当に求めたときに初めて一時フォルダへ書き出す（一覧の中での並べ替えだけなら書き出さない）
using ImageViewer.Core.Archives;

namespace ImageViewer.App.Commands;

public sealed class ArchiveDragData : DataObject
{
    private readonly IReadOnlyList<string> _paths;
    private string[]? _files;

    public ArchiveDragData(IReadOnlyList<string> paths)
    {
        _paths = paths;
        // 形式の一覧に「ファイル」を出しておく（中身は求められたときに作る）
        base.SetData(DataFormats.FileDrop, Array.Empty<string>());
    }

    public override object? GetData(string format, bool autoConvert)
    {
        if (format != DataFormats.FileDrop) return base.GetData(format, autoConvert);
        if (_files == null)
        {
            try
            {
                _files = ArchiveExport.ToFiles(_paths).ToArray();
            }
            catch (Exception ex) when (ZipStore.IsReadFailure(ex))
            {
                _files = Array.Empty<string>();
            }
        }
        return _files;
    }
}
