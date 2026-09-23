// リサイズ・形式変換（image-sizechange のリサイズタブから移植）。
// 読み込みは ImageLoader 経由なので HEIC / AVIF / RAW なども入力にできる
using ImageViewer.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace ImageViewer.Core.Editing;

public enum ResizeAlgorithm { Bilinear, Bicubic, Lanczos }

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

public sealed record ConvertOptions
{
    /// <summary>LongEdge に指定するとリサイズしない</summary>
    public const int KeepSize = 0;

    public static readonly int[] SizePresets = { 1600, 1200, 600, 560 };

    /// <summary>長辺の大きさ（KeepSize ならリサイズしない）</summary>
    public int LongEdge { get; init; } = 1600;
    public ResizeAlgorithm Algorithm { get; init; } = ResizeAlgorithm.Lanczos;
    /// <summary>長辺が指定より小さい画像は拡大しない</summary>
    public bool NoUpscale { get; init; } = true;
    /// <summary>EXIF・XMP・IPTC・PNG のテキストを消す（ICC プロファイルは残す）</summary>
    public bool StripMetadata { get; init; } = true;
    public OutputFormat Format { get; init; } = OutputFormat.Keep;

    // ファイル名（適用順: 置換 → 末尾に付ける → 小文字化）
    public string ReplaceSearch { get; init; } = "";
    public string ReplaceWith { get; init; } = "";
    public string Suffix { get; init; } = "";
    public bool Lowercase { get; init; }

    public OutputFolderMode OutputMode { get; init; } = OutputFolderMode.Subfolder;
    public string SubfolderName { get; init; } = "resized";
    public string? CustomFolder { get; init; }

    /// <summary>出力先に同名のファイルがあるとき上書きする（false ならその画像は飛ばす）</summary>
    public bool Overwrite { get; init; }
}

public enum ConvertStatus { Ok, Skip, Error }

/// <summary>1 枚分の変換予定。Skip は同名ファイルがあるので飛ばす、Error は実行できない理由つき</summary>
public sealed record ConvertPlanItem(string Source, string Target, ConvertStatus Status, string? Note)
{
    public string SourceName => Path.GetFileName(Source);
    public string TargetName => Path.GetFileName(Target);
    /// <summary>元の画像を変換結果で置き換える</summary>
    public bool ReplacesSource => string.Equals(Path.GetFullPath(Source), Path.GetFullPath(Target), StringComparison.OrdinalIgnoreCase);
}

public sealed record ConvertProgress(int Done, int Total, string Name);

public sealed record ConvertResult(int Converted, int Skipped, IReadOnlyList<string> Errors, bool Canceled);

public static class Converter
{
    public static IResampler ResamplerOf(ResizeAlgorithm algorithm) => algorithm switch
    {
        ResizeAlgorithm.Bilinear => KnownResamplers.Triangle,
        ResizeAlgorithm.Bicubic => KnownResamplers.Bicubic,
        _ => KnownResamplers.Lanczos3,
    };

    /// <summary>出力ファイル名（置換 → 末尾に付ける → 小文字化。拡張子は出力形式に合わせる）</summary>
    public static string BuildDestName(string fileName, ConvertOptions options)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = ImageSaver.ExtensionFor(options.Format, Path.GetExtension(fileName));
        if (!string.IsNullOrEmpty(options.ReplaceSearch))
            stem = stem.Replace(options.ReplaceSearch, options.ReplaceWith);
        stem += options.Suffix;
        if (options.Lowercase)
        {
            stem = stem.ToLowerInvariant();
            ext = ext.ToLowerInvariant();
        }
        return stem + ext;
    }

    /// <summary>元の画像のフォルダから出力先フォルダを決める</summary>
    public static string OutputFolderFor(string sourceFolder, ConvertOptions options) => options.OutputMode switch
    {
        OutputFolderMode.Same => sourceFolder,
        OutputFolderMode.Custom => options.CustomFolder ?? sourceFolder,
        _ => Path.Combine(sourceFolder, options.SubfolderName),
    };

    /// <summary>出力先の名前を決めて検査する（ファイルには触らない）</summary>
    public static List<ConvertPlanItem> Plan(IReadOnlyList<string> sources, ConvertOptions options)
    {
        var targets = sources
            .Select(s => Path.Combine(OutputFolderFor(Path.GetDirectoryName(s)!, options), BuildDestName(Path.GetFileName(s), options)))
            .ToList();
        var counts = targets.GroupBy(t => Path.GetFullPath(t), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var plan = new List<ConvertPlanItem>(sources.Count);
        for (int i = 0; i < sources.Count; i++)
        {
            string src = sources[i], dst = targets[i];
            string? nameError = Rename.RenamePlanner.ValidateName(Path.GetFileName(dst));
            if (nameError != null)
                plan.Add(new(src, dst, ConvertStatus.Error, nameError));
            else if (counts[Path.GetFullPath(dst)] > 1)
                plan.Add(new(src, dst, ConvertStatus.Error, "出力先の名前が重複しています"));
            else if (File.Exists(dst) && !options.Overwrite)
                plan.Add(new(src, dst, ConvertStatus.Skip, "同名のファイルがあるので飛ばします"));
            else
            {
                var item = new ConvertPlanItem(src, dst, ConvertStatus.Ok, null);
                plan.Add(item.ReplacesSource ? item with { Note = "元の画像を置き換えます" }
                    : File.Exists(dst) ? item with { Note = "上書きします" } : item);
            }
        }
        return plan;
    }

    /// <summary>計画の Ok のものを 1 枚ずつ変換する（重い処理なので呼び出し側で別スレッドへ）</summary>
    public static ConvertResult Run(IReadOnlyList<ConvertPlanItem> plan, ConvertOptions options,
        IProgress<ConvertProgress>? progress = null, CancellationToken ct = default)
    {
        var todo = plan.Where(p => p.Status == ConvertStatus.Ok).ToList();
        int converted = 0, skipped = plan.Count(p => p.Status == ConvertStatus.Skip);
        var errors = new List<string>();
        for (int i = 0; i < todo.Count; i++)
        {
            if (ct.IsCancellationRequested) return new(converted, skipped, errors, true);
            var item = todo[i];
            progress?.Report(new(i, todo.Count, item.SourceName));
            try
            {
                ConvertOne(item.Source, item.Target, options);
                converted++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add($"{item.SourceName}: {ex.Message}");
            }
        }
        progress?.Report(new(todo.Count, todo.Count, ""));
        return new(converted, skipped, errors, false);
    }

    /// <summary>1 枚を変換して保存する（上書きの判断は済んでいる前提）</summary>
    public static void ConvertOne(string src, string dst, ConvertOptions options)
    {
        // 回転補正済み・先頭フレームだけ（アニメーションは静止画になる）
        using var image = ImageLoader.Load(src);

        int longNow = Math.Max(image.Width, image.Height);
        bool resize = options.LongEdge != ConvertOptions.KeepSize && longNow != options.LongEdge
                      && !(options.NoUpscale && longNow < options.LongEdge);
        if (resize)
        {
            double scale = (double)options.LongEdge / longNow;
            int w = Math.Max(1, (int)Math.Round(image.Width * scale));
            int h = Math.Max(1, (int)Math.Round(image.Height * scale));
            image.Mutate(x => x.Resize(w, h, ResamplerOf(options.Algorithm)));
        }

        if (options.StripMetadata)
        {
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.GetPngMetadata().TextData.Clear();
        }
        ImageSaver.Save(image, dst);
    }

    /// <summary>設定の説明（1 行）</summary>
    public static string Describe(ConvertOptions options)
    {
        string size = options.LongEdge == ConvertOptions.KeepSize ? "サイズそのまま" : $"長辺 {options.LongEdge}px";
        string format = options.Format switch
        {
            OutputFormat.Jpeg => "JPG", OutputFormat.Png => "PNG", OutputFormat.Webp => "WEBP", _ => "形式そのまま",
        };
        return $"{size} ・ {format} ・ {options.Algorithm}";
    }
}
