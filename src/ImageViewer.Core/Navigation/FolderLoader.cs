// フォルダ（ZIP の中も）の一覧の読み込み。続けて別のフォルダを開いたときは、最後に頼んだ読み込みの結果だけを返す
using ImageViewer.Core.Archives;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Ordering;

namespace ImageViewer.Core.Navigation;

/// <summary>読み込んだフォルダの中身（Files は Mode の並び順に並べてある。設定によっては画像以外のファイルも入る）</summary>
public sealed record FolderContents(List<DirectoryInfo> Folders, List<ImageFile> Files, SortMode Mode, bool InArchive);

public sealed class FolderLoader
{
    private readonly Func<string, bool, SortMode, CancellationToken, FolderContents> _read;
    private CancellationTokenSource? _cts;

    public FolderLoader(FolderOrderStore orderStore) : this((folder, reload, mode, ct) => Read(folder, reload, mode, orderStore, ct)) { }

    /// <param name="read">一覧を読む処理（フォルダ, 読み直しか, 今の並び順, 打ち切り）。テストで差し替える</param>
    public FolderLoader(Func<string, bool, SortMode, CancellationToken, FolderContents> read) => _read = read;

    /// <summary>1 度でも読み込みを頼んだか</summary>
    public bool Started => _cts != null;

    /// <summary>
    /// 一覧を別スレッドで読む（大きなフォルダでも UI を止めない）。前に頼んだ読み込みは打ち切る。
    /// 読んでいる間に次の読み込みが頼まれていたら、読めていても・失敗していても null（古い結果もエラーも画面に出さない）
    /// </summary>
    /// <param name="reload">今開いているフォルダの読み直し（並び順は currentMode のまま）</param>
    public async Task<FolderContents?> LoadAsync(string folder, bool reload, SortMode currentMode)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var contents = await Task.Run(() => _read(folder, reload, currentMode, cts.Token), cts.Token);
            // 打ち切りを見ない所（並び順の読み込みなど）まで進んでいた読み込みは、打ち切った後でも読み終わる
            return IsLatest(cts) ? contents : null;
        }
        catch (OperationCanceledException)
        {
            return null; // 後から別のフォルダが開かれた
        }
        catch (Exception) when (!IsLatest(cts))
        {
            return null;
        }
    }

    private bool IsLatest(CancellationTokenSource cts) => ReferenceEquals(_cts, cts);

    /// <summary>
    /// 別のフォルダを開いたときは、手動の並び順が保存されていれば手動、無ければ名前順で始める
    /// </summary>
    private static FolderContents Read(string folder, bool reload, SortMode currentMode, FolderOrderStore orderStore, CancellationToken ct)
    {
        bool archive = ArchivePath.TrySplit(folder, out string zip, out string inner);
        List<ImageFile> listed;
        List<DirectoryInfo> subfolders;
        if (archive)
        {
            // ZIP そのもの・ZIP の中のフォルダ: 中の一覧から作る（ZIP の中の ZIP は開かない）
            ArchiveListing listing;
            try
            {
                // 読み直し（F5）は目次から。大きさも日時も変えずに置き換えられた ZIP にも追いつく
                if (reload) ZipStore.Forget(zip);
                listing = ZipStore.List(zip, inner, ct);
            }
            catch (InvalidDataException ex)
            {
                throw new IOException($"ZIP を開けませんでした（壊れているか、ZIP ではありません）: {ex.Message}", ex);
            }
            listed = listing.Images.ToList();
            subfolders = listing.Folders.Select(f => new DirectoryInfo(f))
                .OrderBy(d => d.Name, FileSorting.NaturalNameComparer).ToList();
        }
        else
        {
            listed = FolderListing.ListFiles(folder, ct);
            // ZIP はフォルダのタイルとして、サブフォルダの後ろに並べる
            subfolders = FolderListing.ListSubfolders(folder, ct);
            subfolders.AddRange(FolderListing.ListArchives(folder, ct));
        }
        var saved = orderStore.Load(folder);
        var mode = reload ? currentMode : saved != null ? SortMode.Manual : SortMode.Name;
        return new(subfolders, Arrange(listed, mode, saved), mode, archive);
    }

    /// <summary>並び順を当てる。手動は保存した並び（無ければ名前順）</summary>
    public static List<ImageFile> Arrange(IReadOnlyList<ImageFile> files, SortMode mode, IReadOnlyList<string>? savedOrder)
    {
        var sorted = FileSorting.Sort(files, mode);
        return mode == SortMode.Manual && savedOrder != null ? ManualOrder.Apply(sorted, savedOrder) : sorted;
    }
}
