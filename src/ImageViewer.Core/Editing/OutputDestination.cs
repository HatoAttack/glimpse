// 一括処理の出力先（フォルダの決め方・同名のファイルの扱い）と、保存先の計画。リサイズ・まとめて補正で共通
namespace ImageViewer.Core.Editing;

/// <summary>出力先の決め方</summary>
public enum OutputFolderMode
{
    /// <summary>元の画像のフォルダの中のサブフォルダ（既定 resized）</summary>
    Subfolder,
    /// <summary>元の画像と同じフォルダ</summary>
    Same,
    /// <summary>指定したフォルダ</summary>
    Custom,
}

/// <param name="Overwrite">出力先に同名のファイルがあるとき上書きする（false ならその画像は飛ばす）</param>
public sealed record OutputDestination(OutputFolderMode Mode, string SubfolderName, string? CustomFolder, bool Overwrite)
{
    /// <summary>元の画像のフォルダから出力先フォルダを決める</summary>
    public string FolderFor(string sourceFolder) => Mode switch
    {
        OutputFolderMode.Same => sourceFolder,
        OutputFolderMode.Custom => CustomFolder ?? sourceFolder,
        _ => Path.Combine(sourceFolder, SubfolderName),
    };

    /// <summary>出力先の指定の誤り（無ければ null）</summary>
    public string? Validate() => Mode switch
    {
        OutputFolderMode.Subfolder when SubfolderError() is string e => e,
        OutputFolderMode.Custom when CustomFolder == null => "出力先のフォルダを指定してください",
        OutputFolderMode.Custom when !Path.IsPathFullyQualified(CustomFolder!) => "出力先のフォルダは C:\\… の形で指定してください",
        _ => null,
    };

    private string? SubfolderError() =>
        Mode == OutputFolderMode.Subfolder && Rename.RenamePlanner.ValidateName(SubfolderName) is string e ? $"中のフォルダの名前: {e}" : null;

    /// <summary>出力先の説明（「resized フォルダへ」など）</summary>
    public string Describe() => Mode switch
    {
        OutputFolderMode.Same => "同じフォルダへ",
        OutputFolderMode.Custom => $"{CustomFolder} へ",
        _ => $"{SubfolderName} フォルダへ",
    };

    /// <summary>出力先の名前を決めて検査する（ファイルには触らない）</summary>
    /// <param name="nameFor">元のファイル名 → 出力するファイル名</param>
    /// <param name="noteFor">元のパス → その画像について知らせること（無ければ null）</param>
    public List<ConvertPlanItem> Plan(IReadOnlyList<string> sources, Func<string, string> nameFor, Func<string, string?>? noteFor = null)
    {
        var names = sources.Select(s => nameFor(Path.GetFileName(s))).ToList();
        var targets = sources.Select((s, i) => Path.Combine(FolderFor(Path.GetDirectoryName(s)!), names[i])).ToList();
        var counts = targets.GroupBy(t => Path.GetFullPath(t), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        string? folderError = SubfolderError();

        var plan = new List<ConvertPlanItem>(sources.Count);
        for (int i = 0; i < sources.Count; i++)
        {
            string src = sources[i], dst = targets[i];
            // 組み立てる前の名前を調べる（置換や末尾に \ や .. があっても出力先の外に書かないように）
            string? nameError = folderError ?? Rename.RenamePlanner.ValidateName(names[i]);
            if (nameError != null)
                plan.Add(new(src, dst, ConvertStatus.Error, nameError));
            else if (counts[Path.GetFullPath(dst)] > 1)
                plan.Add(new(src, dst, ConvertStatus.Error, "出力先の名前が重複しています"));
            else if (File.Exists(dst) && !Overwrite)
                plan.Add(new(src, dst, ConvertStatus.Skip, "同名のファイルがあるので飛ばします"));
            else
            {
                var notes = new List<string>();
                if (SourceGuard.IsSameFile(src, dst)) notes.Add("元の画像を置き換えます");
                else if (File.Exists(dst)) notes.Add("上書きします");
                if (noteFor?.Invoke(src) is string note) notes.Add(note);
                plan.Add(new(src, dst, ConvertStatus.Ok, notes.Count > 0 ? string.Join("・", notes) : null));
            }
        }
        return plan;
    }
}

public enum ConvertStatus { Ok, Skip, Error }

/// <summary>1 枚分の処理の予定。Skip は同名ファイルがあるので飛ばす、Error は実行できない理由つき</summary>
public sealed record ConvertPlanItem(string Source, string Target, ConvertStatus Status, string? Note)
{
    public string SourceName => Path.GetFileName(Source);
    public string TargetName => Path.GetFileName(Target);
    /// <summary>元の画像を結果で置き換える</summary>
    public bool ReplacesSource => SourceGuard.IsSameFile(Source, Target);
}

public sealed record ConvertProgress(int Done, int Total, string Name);

public sealed record ConvertResult(int Converted, int Skipped, IReadOnlyList<string> Errors, bool Canceled);
