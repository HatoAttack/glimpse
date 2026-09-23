// 連結（image-sizechange の連結タブから移植）。複数の画像を横・縦・グリッドに並べて 1 枚にする
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
    /// プレビュー用に、どの画像も同じ比率 scale で縮小して読む（一番大きい画像の長辺が maxEdge になる比率。拡大はしない）。
    /// 大きさを揃える計算が原寸と同じになるよう、全部同じ比率にする
    /// </summary>
    public static (List<Image<Rgba32>> Images, double Scale) LoadForPreview(IReadOnlyList<string> paths, int maxEdge, CancellationToken ct = default)
    {
        var headers = paths.Select(p => Imaging.ImageLoader.Identify(p)).ToList();
        int longest = headers.Max(h => h == null ? 0 : Math.Max(h.Width, h.Height));
        double scale = longest > maxEdge ? (double)maxEdge / longest : 1;
        var images = new List<Image<Rgba32>>(paths.Count);
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var h = headers[i];
                var options = h == null || scale >= 1 ? Imaging.LoadOptions.Full
                    : Imaging.LoadOptions.Thumbnail(Math.Max(1, (int)Math.Round(Math.Max(h.Width, h.Height) * scale)));
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

    public static Image<Rgba32> Combine(IReadOnlyList<Image<Rgba32>> images, CombineOptions options)
    {
        if (images.Count == 0) throw new ArgumentException("画像がありません");
        var bg = ParseColor(options.Background, options.Transparent);
        return options.Layout == CombineLayout.Grid
            ? CombineGrid(images, Math.Max(1, options.Columns), options.Normalize == CombineNormalize.Fixed ? options.TargetPx : null,
                options.Spacing, options.Padding, bg)
            : CombineLinear(images, options.Layout == CombineLayout.Horizontal, options.Normalize, options.TargetPx,
                options.Align, options.Spacing, options.Padding, bg);
    }

    /// <summary>高さ（byHeight）または幅を target に合わせて比例で拡大縮小。同じならそのまま返す（呼び出し側は所有に注意）</summary>
    private static Image<Rgba32> ResizeTo(Image<Rgba32> image, int target, bool byHeight)
    {
        int w = image.Width, h = image.Height;
        if (byHeight)
            return h == target ? image : image.Clone(x => x.Resize(Math.Max(1, (int)Math.Round((double)w * target / h)), target, KnownResamplers.Lanczos3));
        return w == target ? image : image.Clone(x => x.Resize(target, Math.Max(1, (int)Math.Round((double)h * target / w)), KnownResamplers.Lanczos3));
    }

    /// <summary>比を保って cw×ch に収まるよう縮小（拡大はしない）</summary>
    private static Image<Rgba32> Contain(Image<Rgba32> image, int cw, int ch)
    {
        double s = Math.Min((double)cw / image.Width, (double)ch / image.Height);
        if (s >= 1) return image;
        return image.Clone(x => x.Resize(Math.Max(1, (int)Math.Round(image.Width * s)), Math.Max(1, (int)Math.Round(image.Height * s)),
            KnownResamplers.Lanczos3));
    }

    private static int AlignOffset(int total, int size, CombineAlign align) => align switch
    {
        CombineAlign.Center => (total - size) / 2,
        CombineAlign.End => total - size,
        _ => 0,
    };

    private static Image<Rgba32> CombineLinear(IReadOnlyList<Image<Rgba32>> images, bool horizontal, CombineNormalize normalize,
        int targetPx, CombineAlign align, int spacing, int padding, Rgba32 bg)
    {
        var work = new List<Image<Rgba32>>(images.Count);
        var owned = new List<Image<Rgba32>>();
        try
        {
            if (normalize != CombineNormalize.None)
            {
                var dims = images.Select(im => horizontal ? im.Height : im.Width);
                int target = Math.Max(1, normalize switch
                {
                    CombineNormalize.Min => dims.Min(),
                    CombineNormalize.Max => dims.Max(),
                    _ => targetPx,
                });
                foreach (var im in images)
                {
                    var r = ResizeTo(im, target, horizontal);
                    if (!ReferenceEquals(r, im)) owned.Add(r);
                    work.Add(r);
                }
            }
            else
            {
                work.AddRange(images);
            }

            int n = work.Count;
            int contentW = horizontal ? work.Sum(im => im.Width) + spacing * (n - 1) : work.Max(im => im.Width);
            int contentH = horizontal ? work.Max(im => im.Height) : work.Sum(im => im.Height) + spacing * (n - 1);
            var canvas = new Image<Rgba32>(contentW + 2 * padding, contentH + 2 * padding, bg);
            int cur = padding;
            foreach (var im in work)
            {
                var at = horizontal
                    ? new Point(cur, padding + AlignOffset(contentH, im.Height, align))
                    : new Point(padding + AlignOffset(contentW, im.Width, align), cur);
                canvas.Mutate(x => x.DrawImage(im, at, 1f));
                cur += (horizontal ? im.Width : im.Height) + spacing;
            }
            return canvas;
        }
        finally
        {
            foreach (var o in owned) o.Dispose();
        }
    }

    /// <summary>columns 列のグリッド。各画像はセルに収めて中央に置く。cellPx が null ならセルは元の画像の最大の幅 × 高さ</summary>
    private static Image<Rgba32> CombineGrid(IReadOnlyList<Image<Rgba32>> images, int columns, int? cellPx, int spacing, int padding, Rgba32 bg)
    {
        int cw = cellPx is int c ? Math.Max(1, c) : images.Max(im => im.Width);
        int ch = cellPx is int c2 ? Math.Max(1, c2) : images.Max(im => im.Height);
        columns = Math.Min(columns, images.Count);
        int rows = (images.Count + columns - 1) / columns;
        var canvas = new Image<Rgba32>(columns * cw + spacing * (columns - 1) + 2 * padding, rows * ch + spacing * (rows - 1) + 2 * padding, bg);
        for (int i = 0; i < images.Count; i++)
        {
            int r = i / columns, col = i % columns;
            var fit = Contain(images[i], cw, ch);
            try
            {
                var at = new Point(padding + col * (cw + spacing) + (cw - fit.Width) / 2, padding + r * (ch + spacing) + (ch - fit.Height) / 2);
                canvas.Mutate(x => x.DrawImage(fit, at, 1f));
            }
            finally
            {
                if (!ReferenceEquals(fit, images[i])) fit.Dispose();
            }
        }
        return canvas;
    }
}
