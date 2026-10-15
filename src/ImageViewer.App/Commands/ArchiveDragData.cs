// ZIP の中の画像をほかのアプリへドラッグするときのデータ
// ファイルの一覧は、落とす先が本当に求めたときに初めて一時フォルダへ書き出す（一覧の中での並べ替えだけなら書き出さない）
// .NET 10 の WinForms は、DataObject を継承して GetData を上書きしても、落とす先（エクスプローラーなど）が OLE で求めたときには呼ばない。
// そのため IDataObject を自分で実装し、DataObject で包んで渡す（包まれた IDataObject の GetData は OLE からも呼ばれる）
using ImageViewer.Core.Archives;

namespace ImageViewer.App.Commands;

// WFO1001（ITypedDataObject を実装していない）: このデータを読むのは OLE（GetData に来る）とアプリの一覧（GetData / GetDataPresent）だけで、
// TryGetData<T> で型を指定して取り出す所は無いので実装しない
#pragma warning disable WFO1001
public sealed class ArchiveDragData : IDataObject
#pragma warning restore WFO1001
{
    private readonly IReadOnlyList<string> _paths;
    private readonly DataObject _others = new(); // ファイル以外の形式（一覧の中での並べ替えの印など）
    private string[]? _files;

    private ArchiveDragData(IReadOnlyList<string> paths) => _paths = paths;

    /// <summary>ドラッグに渡すデータ（DoDragDrop にそのまま渡せる）</summary>
    public static DataObject Create(IReadOnlyList<string> paths) => new(new ArchiveDragData(paths));

    /// <summary>ZIP の中の画像を一時フォルダへ書き出したパス（初めて求められたときに書き出す）。読めなければ空</summary>
    private string[] Files()
    {
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

    private static bool IsFileDrop(string format) => format == DataFormats.FileDrop;

#pragma warning disable WFDEV005 // 包んだ IDataObject への問い合わせは、.NET 10 でもこの GetData に来る
    public object? GetData(string format, bool autoConvert) => IsFileDrop(format) ? Files() : _others.GetData(format, autoConvert);
    public object? GetData(string format) => GetData(format, autoConvert: true);
    public object? GetData(Type format) => GetData(format.FullName!, autoConvert: true);
#pragma warning restore WFDEV005

    // 形式の一覧に「ファイル」を出しておく（中身は求められたときに作る）
    public bool GetDataPresent(string format, bool autoConvert) => IsFileDrop(format) || _others.GetDataPresent(format, autoConvert);
    public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);
    public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!, autoConvert: true);

    public string[] GetFormats(bool autoConvert) => _others.GetFormats(autoConvert).Prepend(DataFormats.FileDrop).Distinct().ToArray();
    public string[] GetFormats() => GetFormats(autoConvert: true);

    public void SetData(string format, bool autoConvert, object? data) => _others.SetData(format, autoConvert, data);
    public void SetData(string format, object? data) => _others.SetData(format, data);
    public void SetData(Type format, object? data) => _others.SetData(format, data);
    public void SetData(object? data) => _others.SetData(data);
}
