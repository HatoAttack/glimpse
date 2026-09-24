// 選択中の画像に対する操作（リサイズ・切り抜き・連結など）の共通インターフェース
namespace ImageViewer.Core.Commands;

/// <summary>コマンドがビューア本体に依頼できること（UI 技術に依存しない）</summary>
public interface ICommandHost
{
    /// <summary>表示中のフォルダ（未選択なら null）。貼り付け先</summary>
    string? CurrentFolder { get; }

    /// <summary>ステータスバー等への通知</summary>
    void Notify(string message);

    /// <summary>ファイルの追加・変更後に一覧を読み直す</summary>
    void RequestRefresh();

    /// <summary>
    /// コマンドがファイル名を変えた。本体はチェック・選択・手動の並び順を新しい名前に付け替え、
    /// 「元に戻す」に登録する
    /// </summary>
    void FilesRenamed(IReadOnlyList<Rename.RenameOp> ops);

    /// <summary>コマンドがファイルを削除した。本体は一覧から外し、消した位置の次の画像を選択する</summary>
    void FilesDeleted(IReadOnlyList<string> paths);

    /// <summary>コマンドが表示中のフォルダにファイル・フォルダを追加した。本体は読み直して、追加したものを選択する</summary>
    void FilesAdded(string folder, IReadOnlyList<string> paths);
}

/// <summary>コマンド実行時に渡される情報</summary>
public sealed record CommandContext(IReadOnlyList<string> Paths, ICommandHost Host);

public interface IImageCommand
{
    /// <summary>一意な ID（設定ファイルでショートカットを上書きするときのキー）</summary>
    string Id { get; }

    /// <summary>メニューに表示する名前</summary>
    string Name { get; }

    /// <summary>メニューのグループ名（同じグループはまとめて並ぶ）</summary>
    string Category { get; }

    /// <summary>既定のショートカット（"Ctrl+Shift+C" 形式、無ければ null）</summary>
    string? DefaultShortcut { get; }

    /// <summary>この選択状態で実行できるか</summary>
    bool CanExecute(IReadOnlyList<string> paths);

    Task ExecuteAsync(CommandContext context);
}

/// <summary>選択枚数の条件だけで実行可否が決まるコマンドの基底クラス</summary>
public abstract class ImageCommandBase : IImageCommand
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual string Category => "画像";
    public virtual string? DefaultShortcut => null;

    /// <summary>必要な最小選択枚数</summary>
    protected virtual int MinSelection => 1;

    /// <summary>許容する最大選択枚数</summary>
    protected virtual int MaxSelection => int.MaxValue;

    public virtual bool CanExecute(IReadOnlyList<string> paths) =>
        paths.Count >= MinSelection && paths.Count <= MaxSelection;

    public abstract Task ExecuteAsync(CommandContext context);
}
