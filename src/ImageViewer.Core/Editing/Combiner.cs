// 連結（image-sizechange の連結タブから移植）。複数の画像を横・縦・グリッドに並べて 1 枚にする
// 先に画像の大きさだけから配置を計算し（Layout）、それから描く（Render）。
// プレビューは配置を計算した後、はじめから縮めた大きさのキャンバスに描くので、何百枚並べても大きなキャンバスを作らない
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageViewer.Core.Editing;

public enum CombineLayout { Horizontal, Vertical, Grid }

/// <summary>
/// 大きさの揃え方。横 / 縦: None そのまま・Min 小さい方・Max 大きい方・Fixed 指定 px（横は高さ、縦は幅を揃える）。
/// グリッド: None 元の最大をセルに・Fixed セルを指定 px 角に
/// </summary>
public enum CombineNormalize { None, Min, Max, Fixed }

/// <summary>揃え（横並びなら上 / 中央 / 下、縦並びなら左 / 中央 / 右）</summary>
public enum CombineAlign { Start, Center, End }

public sealed record CombineOptions
{
    public CombineLayout Layout { get; init; } = CombineLayout.Horizontal;
    public int Columns { get; init; } = 2;
    public CombineNormalize Normalize { get; init; } = CombineNormalize.None;
    public int TargetPx { get; init; } = 600;
    public CombineAlign Align { get; init; } = CombineAlign.Start;
    public int Spacing { get; init; }
    public int Padding { get; init; }
    public string Background { get; init; } = "#ffffff";
    public bool Transparent { get; init; }
    /// <summary>保存形式（Keep は使わず PNG 扱い）</summary>
    public OutputFormat Format { get; init; } = OutputFormat.Png;

    /// <summary>
    /// プレビュー用に縮小した画像で組むときの設定（px で指定する値を同じ比率で縮める）。
    /// 「小さい方 / 大きい方に揃える」は比率が変わらないので、縮小した画像で組んでも見た目は同じになる
    /// </summary>
    public CombineOptions Scaled(double scale) => scale >= 1 ? this : this with
    {
        TargetPx = Math.Max(1, (int)Math.Round(TargetPx * scale)),
        Spacing = (int)Math.Round(Spacing * scale),
        Padding = (int)Math.Round(Padding * scale),
    };
}

/// <summary>連結の配置: 全体の大きさと、各画像を置く位置と大きさ（画素は扱わない）</summary>
public sealed record CombineLayoutResult(Size Canvas, IReadOnlyList<Rectangle> Places);

public static class Combiner
{
    /// <summary>"#RGB" / "#RRGGBB" を色に。透過なら完全な透明</summary>
    public static Rgba32 ParseColor(string hex, bool transparent)
    {
        if (transparent) return new Rgba32(0, 0, 0, 0);
        string s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(c => $"{c}{c}"));
        if (s.Length != 6 || !s.All(Uri.IsHexDigit)) throw new ArgumentException($"色の指定が正しくありません: {hex}");
        return new Rgba32(Convert.ToByte(s[..2], 16), Convert.ToByte(s.Substring(2, 2), 16), Convert.ToByte(s.Substring(4, 2), 16));
    }

    /// <summary>ファイルを原寸で読んで連結し、dst に保存する（重いので別スレッドで）</summary>
    public static (int Width, int Height) CombineFiles(IReadOnlyList<string> paths, CombineOptions options, string dst, CancellationToken ct = default)
    {
        var images = new List<Image<Rgba32>>(paths.Count);
        try
        {
            foreach (var p in paths)
            {
                ct.ThrowIfCancellationRequested();
                images.Add(Imaging.ImageLoader.Load(p));
            }
            using var result = Combine(images, options);
            ct.ThrowIfCancellationRequested();
            ImageSaver.Save(result, dst);
            return (result.Width, result.Height);
        }
        finally
        {
            foreach (var im in images) im.Dispose();
        }
    }

    /// <summary>
    /// プレビュー用に、どの画像も同じ比率 scale で縮小して読む。比率は「一番大きい画像の長辺が maxEdge 以下」かつ
    /// 「全部の画素数の合計が maxTotalPixels 以下」になるように決める（何百枚選んでもメモリが増えすぎない。拡大はしない）。
    /// 大きさを揃える計算が原寸と同じになるよう、全部同じ比率にする
    /// </summary>
    public static (List<Image<Rgba32>> Images, double Scale) LoadForPreview(IReadOnlyList<string> paths, int maxEdge, long maxTotalPixels,
        CancellationToken ct = default)
    {
        var headers = paths.Select(p => Imaging.ImageLoader.Identify(p)).ToList();
        int longest = headers.Max(h => h == null ? 0 : Math.Max(h.Width, h.Height));
        double totalPixels = headers.Sum(h => h == null ? 0 : (double)h.Width * h.Height);
        double scale = Math.Min(1, Math.Min(
            longest > 0 ? (double)maxEdge / longest : 1,
            totalPixels > 0 ? Math.Sqrt(maxTotalPixels / totalPixels) : 1));
        var images = new List<Image<Rgba32>>(paths.Count);
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var h = headers[i];
                // 大きさが分からない画像も縮小して読む（原寸で読んでメモリを使い切らないように）
                var options = h != null && scale >= 1 ? Imaging.LoadOptions.Full
                    : Imaging.LoadOptions.Thumbnail(Math.Max(1, (int)Math.Round((h != null ? Math.Max(h.Width, h.Height) : maxEdge) * scale)));
                images.Add(Imaging.ImageLoader.Load(paths[i], options));
            }
            return (images, scale);
        }
        catch
        {
            foreach (var im in images) im.Dispose();
            throw;
        }
    }

    /// <summary>連結した画像（原寸）</summary>
    public static Image<Rgba32> Combine(IReadOnlyList<Image<Rgba32>> images, CombineOptions options) =>
        Render(images, Layout(SizesOf(images), options), options, maxEdge: null);

    /// <summary>長辺が maxEdge 以下になるよう縮めて連結する（プレビュー用。はじめから縮めたキャンバスに描く）</summary>
    public static Image<Rgba32> CombineBounded(IReadOnlyList<Image<Rgba32>> images, CombineOptions options, int maxEdge) =>
        Render(images, Layout(SizesOf(images), options), options, maxEdge);

    private static List<Size> SizesOf(IReadOnlyList<Image<Rgba32>> images) => images.Select(im => new Size(im.Width, im.Height)).ToList();

    /// <summary>画像の大きさだけから配置を計算する</summary>
    public static CombineLayoutResult Layout(IReadOnlyList<Size> sizes, CombineOptions options)
    {
        if (sizes.Count == 0) throw new ArgumentException("画像がありません");
        int spacing = Math.Max(0, options.Spacing), padding = Math.Max(0, options.Padding);
        var places = new List<Rectangle>(sizes.Count);

        if (options.Layout == CombineLayout.Grid)
        {
            // セルは元の一番大きい幅 × 高さか、指定 px 角。各画像は比を保ってセルに収め（拡大はしない）、中央に置く
            bool fixedCell = options.Normalize == CombineNormalize.Fixed;
            int cw = fixedCell ? Math.Max(1, options.TargetPx) : sizes.Max(s => s.Width);
            int ch = fixedCell ? Math.Max(1, options.TargetPx) : sizes.Max(s => s.Height);
            int columns = Math.Min(Math.Max(1, options.Columns), sizes.Count);
            int rows = (sizes.Count + columns - 1) / columns;
            for (int i = 0; i < sizes.Count; i++)
            {
                var size = sizes[i];
                double s = Math.Min((double)cw / size.Width, (double)ch / size.Height);
                int w = s >= 1 ? size.Width : Math.Max(1, (int)Math.Round(size.Width * s));
                int h = s >= 1 ? size.Height : Math.Max(1, (int)Math.Round(size.Height * s));
                int r = i / columns, c = i % columns;
                places.Add(new Rectangle(padding + c * (cw + spacing) + (cw - w) / 2, padding + r * (ch + spacing) + (ch - h) / 2, w, h));
            }
            return new(new Size(columns * cw + spacing * (columns - 1) + 2 * padding, rows * ch + spacing * (rows - 1) + 2 * padding), places);
        }

        // 横は高さ、縦は幅を揃える（比を保って拡大縮小）
        bool horizontal = options.Layout == CombineLayout.Horizontal;
        var dims = sizes.Select(s => horizontal ? s.Height : s.Width).ToList();
        int? target = options.Normalize switch
        {
            CombineNormalize.Min => dims.Min(),
            CombineNormalize.Max => dims.Max(),
            CombineNormalize.Fixed => Math.Max(1, options.TargetPx),
            _ => null,
        };
        var fitted = sizes.Select(s => target is not int t ? s
            : horizontal ? (s.Height == t ? s : new Size(Math.Max(1, (int)Math.Round((double)s.Width * t / s.Height)), t))
            : s.Width == t ? s : new Size(t, Math.Max(1, (int)Math.Round((double)s.Height * t / s.Width)))).ToList();

        int n = fitted.Count;
        int contentW = horizontal ? fitted.Sum(s => s.Width) + spacing * (n - 1) : fitted.Max(s => s.Width);
        int contentH = horizontal ? fitted.Max(s => s.Height) : fitted.Sum(s => s.Height) + spacing * (n - 1);
        int cur = padding;
        foreach (var s in fitted)
        {
            places.Add(horizontal
                ? new Rectangle(cur, padding + AlignOffset(contentH, s.Height, options.Align), s.Width, s.Height)
                : new Rectangle(padding + AlignOffset(contentW, s.Width, options.Align), cur, s.Width, s.Height));
            cur += (horizontal ? s.Width : s.Height) + spacing;
        }
        return new(new Size(contentW + 2 * padding, contentH + 2 * padding), places);
    }

    private static int AlignOffset(int total, int size, CombineAlign align) => align switch
    {
        CombineAlign.Center => (total - size) / 2,
        CombineAlign.End => total - size,
        _ => 0,
    };

    /// <summary>配置どおりに描く。maxEdge を指定すると全体がその大きさに収まるよう縮めて描く</summary>
    private static Image<Rgba32> Render(IReadOnlyList<Image<Rgba32>> images, CombineLayoutResult layout, CombineOptions options, int? maxEdge)
    {
        var bg = ParseColor(options.Background, options.Transparent);
        int longest = Math.Max(layout.Canvas.Width, layout.Canvas.Height);
        double scale = maxEdge is int m && longest > m ? (double)m / longest : 1;
        int Scale(int v) => scale >= 1 ? v : (int)Math.Round(v * scale);

        var canvas = new Image<Rgba32>(Math.Max(1, Scale(layout.Canvas.Width)), Math.Max(1, Scale(layout.Canvas.Height)), bg);
        try
        {
            for (int i = 0; i < images.Count; i++)
            {
                var place = layout.Places[i];
                int w = Math.Max(1, Scale(place.Width)), h = Math.Max(1, Scale(place.Height));
                var at = new Point(Scale(place.X), Scale(place.Y));
                var image = images[i];
                if (image.Width == w && image.Height == h)
                {
                    canvas.Mutate(x => x.DrawImage(image, at, 1f));
                    continue;
                }
                // プレビューは速さ優先（Triangle）、原寸は画質優先（Lanczos3）
                using var resized = image.Clone(x => x.Resize(w, h, maxEdge != null ? KnownResamplers.Triangle : KnownResamplers.Lanczos3));
                canvas.Mutate(x => x.DrawImage(resized, at, 1f));
            }
            return canvas;
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }
}
