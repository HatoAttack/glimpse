// 編集ダイアログで、選んだ画像を 1 枚ずつ出す: ◀ ▶ と見出し、画像の読み込み（続けて送ったら古い読み込みは捨てる）、
// 表示中の画像（原寸）の持ち主。読めた画像で何をするかは各ダイアログが決める
using ImageViewer.Core.Imaging;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageViewer.App.Dialogs;

internal sealed class ImageStepper : IDisposable
{
    public enum Outcome
    {
        /// <summary>読めた（Image が新しい画像になった）</summary>
        Shown,
        /// <summary>読めなかった（Image は null。見出しに理由を出した）</summary>
        Failed,
        /// <summary>読んでいる間に別の画像が頼まれた（何も変えていない。後の読み込みに任せる）</summary>
        Superseded,
    }

    private readonly IReadOnlyList<string> _paths;
    private readonly Label _name = new() { AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
    private readonly Button _prev = new() { Text = "◀ 前", AutoSize = true };
    private readonly Button _next = new() { Text = "次 ▶", AutoSize = true };
    private CancellationTokenSource? _loadCts;

    /// <summary>◀ ▶ と見出しの行（ダイアログの上に置く）</summary>
    public Control Bar { get; }

    /// <summary>◀ ▶ を押した（-1 / +1）</summary>
    public event Action<int>? StepRequested;

    public ImageStepper(IReadOnlyList<string> paths)
    {
        _paths = paths;
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6, 4, 6, 0) };
        _prev.Click += (_, _) => StepRequested?.Invoke(-1);
        _next.Click += (_, _) => StepRequested?.Invoke(1);
        _prev.Enabled = _next.Enabled = paths.Count > 1;
        bar.Controls.AddRange(new Control[] { _prev, _next, _name });
        Bar = bar;
    }

    public IReadOnlyList<string> Paths => _paths;
    public int Count => _paths.Count;
    public int Index { get; private set; } = -1;
    public string CurrentPath => _paths[Index];

    /// <summary>表示中の画像（回転補正済みの原寸。保存元）。読めなかったとき・まだ読んでいないときは null</summary>
    public SixLabors.ImageSharp.Image<Rgba32>? Image { get; private set; }

    /// <summary>
    /// 画像を読み込み中（Index はもう次の画像なのに Image はまだ前の画像）。この間は保存しない（前の画像の画素で上書きしないように）
    /// </summary>
    public bool Loading { get; private set; }

    /// <summary>Image が Index の画像で、そのまま使える（読み込み中でも、読めなかった後でもない）</summary>
    public bool Ready => Index >= 0 && Image != null && !Loading;

    /// <summary>◀ ▶ を使えるか（1 枚だけなら常に使えない）</summary>
    public bool StepEnabled
    {
        set => _prev.Enabled = _next.Enabled = value && _paths.Count > 1;
    }

    /// <summary>今の画像から delta 枚先（端はつながっている）</summary>
    public int IndexAt(int delta) => ((Index + delta) % _paths.Count + _paths.Count) % _paths.Count;

    /// <summary>index の画像を読んで Image を入れ替える。前に頼んだ読み込みは捨てる</summary>
    /// <param name="started">Index と Loading を変えた直後（読み始める前）に呼ぶ。ボタンの状態を合わせるため</param>
    public async Task<Outcome> ShowAsync(int index, Action started)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        Index = index;
        Loading = true;
        started();
        string path = _paths[index];
        string label = $"[{index + 1}/{_paths.Count}] {Path.GetFileName(path)}";
        _name.Text = $"{label}  （読み込み中…）";
        SixLabors.ImageSharp.Image<Rgba32> image;
        try
        {
            image = await Task.Run(() => ImageLoader.Load(path), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return Outcome.Superseded;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (cts.IsCancellationRequested) return Outcome.Superseded;
            Loading = false;
            Image?.Dispose();
            Image = null;
            _name.Text = $"{label}  （読み込めません: {ex.Message}）";
            return Outcome.Failed;
        }
        if (cts.IsCancellationRequested)
        {
            image.Dispose();
            return Outcome.Superseded;
        }
        Loading = false;
        Image?.Dispose();
        Image = image;
        _name.Text = $"{label}  （{image.Width} × {image.Height}）";
        return Outcome.Shown;
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        Image?.Dispose();
        Image = null;
    }
}
