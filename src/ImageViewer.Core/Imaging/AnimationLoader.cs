// GIF / WEBP アニメの読み込み（1 枚表示での再生と、フレーム保存）
// - Skia で読む。コマは前のコマ（「背景に戻す」「前の状態に戻す」も含めて Skia が選ぶ）に重ねて、どのコマもそれだけで 1 枚の絵にする。
//   #81 の試作で、ブラウザ（Chrome）と同じコマになることを確かめた（ImageSharp は「背景に戻す」の GIF でコマが違っていた）
// - 再生用は全部のコマを画面の大きさに縮めて持つ
// - 全部のコマを一度に展開するので、大きすぎるもの・コマが多すぎるものは再生しない（先頭のコマの静止画のまま）
// - フレーム保存は、そのコマを原寸で読み直して PNG にする
using SkiaSharp;

namespace ImageViewer.Core.Imaging;

/// <summary>再生用のコマ（画面に合わせて縮めたもの）と、それぞれの表示時間</summary>
public sealed class AnimationFrames : IDisposable
{
    public AnimationFrames(IReadOnlyList<SKBitmap> frames, IReadOnlyList<int> delaysMs)
    {
        Frames = frames;
        DelaysMs = delaysMs;
    }

    /// <summary>全部のコマ（Frames[i] が i 番目のコマ。どのコマも重ね合わせ済みの 1 枚の絵）</summary>
    public IReadOnlyList<SKBitmap> Frames { get; }

    public int Width => Frames[0].Width;
    public int Height => Frames[0].Height;

    /// <summary>それぞれのコマを見せる時間（ミリ秒）</summary>
    public IReadOnlyList<int> DelaysMs { get; }

    public int Count => DelaysMs.Count;

    /// <summary>元の大きさより縮めて読んだ（表示できる場所が広がったら読み直す価値がある）</summary>
    public bool Downscaled { get; init; }

    public void Dispose()
    {
        foreach (var frame in Frames) frame.Dispose();
    }
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

    /// <summary>コマの数（画素はデコードしない）。読めなければ 0</summary>
    public static int FrameCount(string path)
    {
        if (!MayBeAnimated(path)) return 0;
        try
        {
            using var codec = SkiaDecoder.TryOpen(path);
            return codec == null ? 0 : Math.Max(1, codec.FrameCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
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
        using var codec = SkiaDecoder.TryOpen(path);
        if (codec == null) return null;
        int count = codec.FrameCount, width = codec.Info.Width, height = codec.Info.Height;
        if (count < 2 || (long)width * height * 4 * count > MaxDecodeBytes) return null;
        ct.ThrowIfCancellationRequested();

        var delays = codec.FrameInfo.Select(f => f.Duration < MinDelayMs ? DefaultDelayMs : f.Duration).ToList();
        var composed = SkiaDecoder.DecodeFrames(codec, count - 1, ct);
        var frames = new List<SKBitmap>(count);
        try
        {
            // 画面の大きさと、全部のコマの合計の上限の、小さいほうに合わせて縮める
            double scale = Math.Min(1.0, (double)maxEdge / Math.Max(width, height));
            double bytes = (double)width * height * 4 * count * scale * scale;
            if (bytes > MaxPlaybackBytes) scale *= Math.Sqrt(MaxPlaybackBytes / bytes);
            for (int i = 0; i < composed.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                frames.Add(Finish(composed[i], codec.EncodedOrigin, scale));
                // 重ね合わせは DecodeFrames で済んでいるので、原寸のコマはもう要らない（メモリの山を低くする）
                composed[i].Dispose();
                composed[i] = null!;
            }
            return new AnimationFrames(frames, delays) { Downscaled = scale < 1.0 };
        }
        catch
        {
            foreach (var f in frames) f.Dispose();
            throw;
        }
        finally
        {
            foreach (var c in composed) c?.Dispose();
        }
    }

    /// <summary>
    /// 重ね合わせた原寸のコマ（乗算済み）を、先頭のコマの静止画（ImageLoader.Load）と同じ形にする:
    /// EXIF の回転を直し、scale 倍に縮め、RGBA・乗算しない画像にする（元の composed はそのまま）
    /// </summary>
    private static SKBitmap Finish(SKBitmap composed, SKEncodedOrigin origin, double scale)
    {
        var oriented = SkiaDecoder.Orient(composed.Copy() ?? throw new InvalidOperationException("コマを写せません"), origin);
        try
        {
            if (scale < 1.0)
            {
                int w = Math.Max(1, (int)(oriented.Width * scale)), h = Math.Max(1, (int)(oriented.Height * scale));
                using var resized = SkiaDecoder.Resize(oriented, w, h);
                return SkiaDecoder.Convert(resized, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            }
            return SkiaDecoder.Convert(oriented, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        }
        finally
        {
            oriented.Dispose();
        }
    }

    /// <summary>index 番目（0 から）のコマを原寸で読む（重ね合わせ済み・EXIF の回転を直したもの）</summary>
    public static SKBitmap LoadFrame(string path, int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "そのコマはありません");
        using var codec = SkiaDecoder.TryOpen(path) ?? throw new InvalidDataException($"アニメーションとして読めません: {Path.GetFileName(path)}");
        if (index >= Math.Max(1, codec.FrameCount)) throw new ArgumentOutOfRangeException(nameof(index), "そのコマはありません");
        // そのコマまでだけ展開する（重ね合わせのため、前のコマは読む必要がある）
        var composed = SkiaDecoder.DecodeFrames(codec, index);
        try
        {
            return Finish(composed[index], codec.EncodedOrigin, 1.0);
        }
        finally
        {
            foreach (var c in composed) c.Dispose();
        }
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
