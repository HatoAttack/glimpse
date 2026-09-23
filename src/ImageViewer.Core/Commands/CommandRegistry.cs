// コマンドの登録先。メニュー・右クリックメニュー・ショートカットはすべてここから組み立てる
namespace ImageViewer.Core.Commands;

public sealed class CommandRegistry
{
    private readonly List<IImageCommand> _commands = new();
    private readonly Dictionary<string, string?> _shortcutOverrides = new(StringComparer.Ordinal);

    /// <summary>登録順（＝メニュー内の並び順）</summary>
    public IReadOnlyList<IImageCommand> All => _commands;

    public void Register(IImageCommand command)
    {
        if (_commands.Any(c => c.Id == command.Id))
            throw new InvalidOperationException($"コマンド ID が重複しています: {command.Id}");
        _commands.Add(command);
    }

    public IImageCommand? Find(string id) => _commands.FirstOrDefault(c => c.Id == id);

    /// <summary>設定ファイル等からのショートカット上書き（null でショートカットなし）</summary>
    public void OverrideShortcut(string id, string? shortcut) => _shortcutOverrides[id] = shortcut;

    /// <summary>実際に使うショートカット（上書きがあればそちらを優先）</summary>
    public string? ShortcutOf(IImageCommand command) =>
        _shortcutOverrides.TryGetValue(command.Id, out var s) ? s : command.DefaultShortcut;

    /// <summary>メニュー構築用: グループ名ごとに登録順でまとめる</summary>
    public IEnumerable<IGrouping<string, IImageCommand>> ByCategory() =>
        _commands.GroupBy(c => c.Category);

    /// <summary>同じショートカットが複数のコマンドに割り当てられていないか（大文字小文字・空白は無視）</summary>
    public IReadOnlyList<(string Shortcut, IReadOnlyList<string> Ids)> FindShortcutConflicts() =>
        _commands
            .Select(c => (Key: Normalize(ShortcutOf(c)), c.Id))
            .Where(x => x.Key != null)
            .GroupBy(x => x.Key!)
            .Where(g => g.Count() > 1)
            .Select(g => (g.Key, (IReadOnlyList<string>)g.Select(x => x.Id).ToList()))
            .ToList();

    private static string? Normalize(string? shortcut) =>
        string.IsNullOrWhiteSpace(shortcut) ? null : shortcut.Replace(" ", "").ToLowerInvariant();
}
