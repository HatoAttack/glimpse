// GIF / WEBP アニメの読み込み（1 枚表示での再生と、フレーム保存）
// - 再生用は全部のコマを画面の大きさに縮めて持つ。ImageSharp が返すコマは重ね合わせ済み（どのコマもそれだけで 1 枚の絵）
// - 全部のコマを一度に展開するので、大きすぎるもの・コマが多すぎるものは再生しない（先頭のコマの静止画のまま）
// - フレーム保存は、そのコマを原寸で読み直して PNG にする
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageViewer.Core.Imaging;

/// <summary>再生用のコマ（画面に合わせて縮めたもの）と、それぞれの表示時間</summary>
public sealed class AnimationFrames : IDisposable
{
    public AnimationFrames(Image<Rgba32> image, IReadOnlyList<int> delaysMs)
    {
        Image = image;
        DelaysMs = delaysMs;
    }

    /// <summary>全部のコマ（Frames[i] が i 番目のコマ）</summary>
    public Image<Rgba32> Image { get; }

    /// <summary>それぞれのコマを見せる時間（ミリ秒）</summary>
    public IReadOnlyList<int> DelaysMs { get; }

    public int Count => DelaysMs.Count;

    /// <summary>元の大きさより縮めて読んだ（表示できる場所が広がったら読み直す価値がある）</summary>
    public bool Downscaled { get; init; }

    public void Dispose() => Image.Dispose();
}

public static class AnimationLoader
{
    /// <summary>元の大きさで全部のコマを展開したときの上限。これを超えるものは再生しない</summary>
    public const long MaxDecodeBytes = 1024L * 1024 * 1024;

    /// <summary>再生用に持つコマの合計の上限。超えるときはさらに縮めて収める</summary>
    public const long MaxPlaybackBytes = 384L * 1024 * 1024;

    /// <summary>とても短い表示時間はブラウザと同じく 0.1 秒として扱う（0 や 0.01 秒で書かれた GIF が多いため）</summary>
    private const int MinDelayMs = 20, DefaultDelayMs = 100;

    /// <summary>アニメかもしれない形式か（GIF / WEBP）</summary>
    public static bool MayBeAnimated(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".gif" or ".webp";

    /// <summary>コマの数（ヘッダーだけ読む）。読めなければ 0</summary>
    public static int FrameCount(string path)
    {
        if (!MayBeAnimated(path)) return 0;
        try
        {
            return Image.Identify(path).FrameMetadataCollection.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or UnknownImageFormatException or InvalidImageContentException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 再生用に全部のコマを読む。長辺を maxEdge 以下に縮め、合計が MaxPlaybackBytes に収まるようにする。
    /// コマが 1 つだけ・大きすぎて展開できないときは null
    /// </summary>
    public static AnimationFrames? Load(string path, int maxEdge, CancellationToken ct = default)
    {
        if (!MayBeAnimated(path)) return null;
        var info = Image.Identify(path);
        int count = info.FrameMetadataCollection.Count;
        if (count < 2 || (long)info.Width * info.Height * 4 * count > MaxDecodeBytes) return null;
        ct.ThrowIfCancellationRequested();

        var image = Image.Load<Rgba32>(new DecoderOptions(), path);
        try
        {
            ct.ThrowIfCancellationRequested();
            var delays = image.Frames.Select(f => DelayMs(f.Metadata)).ToList();
            // 画面の大きさと、全部のコマの合計の上限の、小さいほうに合わせて縮める
            double scale = Math.Min(1.0, (double)maxEdge / Math.Max(image.Width, image.Height));
            double bytes = (double)image.Width * image.Height * 4 * count * scale * scale;
            if (bytes > MaxPlaybackBytes) scale *= Math.Sqrt(MaxPlaybackBytes / bytes);
            if (scale < 1.0)
            {
                var size = new Size(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
                image.Mutate(x => x.Resize(size));
            }
            return new AnimationFrames(image, delays) { Downscaled = scale < 1.0 };
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static int DelayMs(SixLabors.ImageSharp.Metadata.ImageFrameMetadata meta)
    {
        int ms = meta.TryGetGifMetadata(out var gif) ? gif.FrameDelay * 10  // 1/100 秒単位
               : meta.TryGetWebpFrameMetadata(out var webp) ? (int)Math.Min(webp.FrameDelay, int.MaxValue)
               : DefaultDelayMs;
        return ms < MinDelayMs ? DefaultDelayMs : ms;
    }

    /// <summary>index 番目（0 から）のコマを原寸で読む</summary>
    public static Image<Rgba32> LoadFrame(string path, int index)
    {
        // そのコマまでだけ展開する（重ね合わせのため、前のコマは読む必要がある）
        using var image = Image.Load<Rgba32>(new DecoderOptions { MaxFrames = (uint)index + 1 }, path);
        if (index >= image.Frames.Count) throw new ArgumentOutOfRangeException(nameof(index), "そのコマはありません");
        return image.Frames.CloneFrame(index);
    }

    /// <summary>
    /// フレーム保存の保存先: 元のフォルダ\元の名前_frame012.png（何コマ目かは 1 から、桁はコマの数に合わせて最低 3 桁）。
    /// 既にあれば (2)… を付ける
    /// </summary>
    public static string FramePath(string source, int index, int count)
    {
        int digits = Math.Max(3, count.ToString().Length);
        string name = $"{Path.GetFileNameWithoutExtension(source)}_frame{(index + 1).ToString().PadLeft(digits, '0')}.png";
        return Editing.ImageSaver.UniquePath(Path.Combine(Path.GetDirectoryName(source)!, name));
    }

    /// <summary>index 番目（0 から）のコマを原寸の PNG で保存し、保存先を返す</summary>
    public static string SaveFrame(string source, int index, int count)
    {
        using var frame = LoadFrame(source, index);
        for (int attempt = 0; ; attempt++)
        {
            string path = FramePath(source, index, count);
            try
            {
                Editing.ImageSaver.Save(frame, path, overwrite: false);
                return path;
            }
            catch (Editing.DestinationExistsException) when (attempt < 10)
            {
                // 書いている間に同じ名前のファイルができた → 次の空いている名前で
            }
        }
    }
}
