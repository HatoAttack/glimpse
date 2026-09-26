// Quick Look: 一覧の上に重ねて画像を大きく表示する（別ウィンドウは作らない）
// - Space を短く押すと開いたまま（もう一度 Space / Esc で閉じる）。押し続けた場合は離すと閉じる
// - ← → で前後の画像、¥（MarkKey）でチェックの付け外し、^（MarkNextKey）で付け外しして次へ
// - 表示は画面の大きさに縮小して読む。持つのは今の画像と前後 1 枚ずつの最大 3 枚、閉じたら全部解放する
// - 開いた直後はサムネイルを引き伸ばして先に見せ、裏で本来の画像を読む（待ち時間を増やさない）
// - I で右側に詳細パネル（画像には重ねず、画像は残りの幅に合わせる）
// - 開くときは一覧のサムネイルの位置から広がり、閉じるときは今の画像のサムネイルの位置へ縮んで戻る
//   （Windows の「アニメーション効果」がオフなら動かさない）
// - Z を押している間だけ 100%（画像の 1 画素を画面の 1 画素で、ぼかさずに）。見える範囲はカーソルの位置で決まる。
//   ピントやノイズの確認用。原寸で読み直すのはこのときだけで、読んだものは次の画像へ移る・閉じるまで持つ
// - フィルムストリップ（前後の画像のサムネイルを下に 1 列）は、← → やホイールで送り始めたら下から出して、画像はその分縮む。
//   一度出したら閉じるまで出したまま。Space を押し続けて見ているときは出さない。クリックでその画像へ
// - GIF / WEBP のアニメは再生する（先に先頭のコマの静止画を見せ、裏で全部のコマを読む）。P で止める / 再開、, . で 1 コマずつ、
//   Ctrl+S で今のコマを原寸の PNG で保存（フレーム保存）。持つのは今の画像のコマだけで、次の画像へ移る・閉じると捨てる
// - E で右側に補正パネル。表示中の画像にそのままかけて見せ（100% でも）、Ctrl+S / 「保存」で元のファイルを上書きする。
//   保存していない補正があるまま送る・閉じる・パネルを閉じるときは、保存するか聞く。アニメでは開かない
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ImageViewer.App.Chrome;
using ImageViewer.App.Theming;
using ImageViewer.Core.Editing;
using ImageViewer.Core.Imaging;
using ImageViewer.Core.Thumbnails;

namespace ImageViewer.App.Viewer;

public sealed class QuickLookView : Control
{
    private const int HoldThresholdMs = 350; // これより長く Space を押していたら「押している間だけ」

    private IReadOnlyList<FileInfo> _items = Array.Empty<FileInfo>();
    private int _index = -1;
    private readonly Dictionary<string, Bitmap> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private CancellationTokenSource? _loadCts;
    private readonly Stopwatch _openedFor = new();
    private bool _openedByKey;
    private bool _spaceReleased;

    /// <summary>サムネイル（読み込みが終わるまでの仮表示用）</summary>
    public Func<FileInfo, Bitmap?>? PlaceholderProvider { get; set; }

    /// <summary>チェックの状態（一覧と共有する）</summary>
    public Func<int, bool>? IsMarked { get; set; }
    public Func<int>? MarkedCount { get; set; }

    public Keys MarkKey { get; set; } = Keys.Oem5;
    public Keys MarkNextKey { get; set; } = Keys.Oem7;

    /// <summary>表示中の画像が変わった（一覧の選択を合わせる用）。引数は画像番号</summary>
    public event EventHandler<int>? CurrentChanged;

    /// <summary>チェックの付け外しを頼む。引数は画像番号</summary>
    public event EventHandler<int>? ToggleMarkRequested;

    public event EventHandler? Closed;

    /// <summary>
    /// 開く / 閉じる動きの後ろに映す部品（一覧と、一覧の右の詳細パネル）。動いている間はこれらを撮った画像を描く。
    /// 1 枚表示の間は隠れて 1 枚表示が広がる部品も入れておく（開いた直後にその場所が空かないように）
    /// </summary>
    public Func<IEnumerable<Control>>? BackdropProvider { get; set; }

    /// <summary>画像番号のサムネイルが描かれている位置（画面座標。見えていなければ null）</summary>
    public Func<int, Rectangle?>? ThumbBoundsProvider { get; set; }

    /// <summary>フレーム保存でファイルを作った（本体が一覧を読み直す）。引数は作ったファイル</summary>
    public event EventHandler<string>? FrameSaved;

    /// <summary>閉じる動きを始める前（本体はここで、1 枚表示の間だけ隠していた部品を戻して一覧を閉じた後の並びにする）</summary>
    public event EventHandler? Closing;

    /// <summary>右側の詳細パネル（中身は本体が入れる）。1 枚表示は常に暗い地なのでダークの配色で描く</summary>
    public DetailsPanel Details { get; } = new() { Dock = DockStyle.Right, FixedPalette = Palette.Dark, Visible = false };

    /// <summary>I キーで詳細パネルを出した / 閉じた（本体が設定に保存する）</summary>
    public event EventHandler? DetailsToggled;

    /// <summary>詳細パネルを出す / 閉じる（I キー・本体の ⓘ / Ctrl+I）</summary>
    public void ToggleDetails()
    {
        Details.Visible = !Details.Visible;
        // 閉じて広くなったら読み直す（狭い幅で読んだものは拡大しないので、そのままだと小さいまま）
        if (!Details.Visible) ReloadForNewSize();
        Invalidate();
        DetailsToggled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>右側の補正パネル（E で開け閉め）。前回の補正（Adjust.Last）は本体が入れる</summary>
    public AdjustPanel Adjust { get; } = new() { Dock = DockStyle.Right, Visible = false };

    /// <summary>補正して保存した（本体が一覧を読み直し、前回の補正を設定に書く）。引数は保存したファイル</summary>
    public event EventHandler<string>? ImageAdjusted;

    /// <summary>画面に合わせて読むときの長辺</summary>
    private int MaxEdge => Math.Max(1, Math.Max(ContentWidth, ClientSize.Height));

    /// <summary>画像を描ける幅（詳細パネルを出していればその分を除く）</summary>
    private int ContentWidth => Math.Max(1, ClientSize.Width - (Details.Visible ? Details.Width : 0) - (Adjust.Visible ? Adjust.Width : 0));

    public bool IsOpen => Visible;
    public int CurrentIndex => _index;

    public QuickLookView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(24, 24, 24);
        ForeColor = Color.White;
        ImeMode = ImeMode.Disable;
        TabStop = true;
        Visible = false;
        Controls.Add(Details);
        Controls.Add(Adjust);
        Adjust.OptionsChanged += (_, _) => Invalidate();
        Adjust.AutoRequested += (_, _) => RunAuto();
        Adjust.SaveRequested += (_, _) => _ = SaveAdjustAsync();
        Adjust.CompareChanged += (_, on) =>
        {
            _comparing = on;
            Invalidate();
        };
        _zoomTimer.Tick += OnZoomTick;
        _filmTimer.Tick += OnFilmTick;
        _animTimer.Tick += OnAnimTick;
        _noticeTimer.Tick += (_, _) =>
        {
            ClearNotice();
            Invalidate();
        };
    }

    /// <param name="byKey">Space で開いた（押し続けたかどうかを離したときに判定する）</param>
    public void Open(IReadOnlyList<FileInfo> items, int index, bool byKey)
    {
        if (index < 0 || index >= items.Count) return;
        if (_closing) FinishClose();
        _items = items;
        _openedByKey = byKey;
        HideFilmstrip();
        _spaceReleased = !byKey;
        _openedFor.Restart();
        // サムネイルが見えていれば、そこから広がって開く（一覧の画面は隠れる前に撮る）
        if (AnimationsEnabled && ThumbBoundsProvider?.Invoke(index) is Rectangle thumb && TakeBackdrop())
            StartZoom(closing: false, thumb, Rectangle.Empty);
        Visible = true;
        BringToFront();
        Focus();
        ShowIndex(index);
    }

    /// <summary>
    /// 閉じる。今の画像のサムネイルが一覧で見えていればそこへ、見えていなければその場で縮んで消える。
    /// 保存していない補正があれば先に保存するか聞く（やめたら閉じない）
    /// </summary>
    public async void Close()
    {
        if (await ConfirmLeaveAsync()) Close(animate: true);
    }

    private void Close(bool animate)
    {
        if (!Visible) return;
        if (_closing)
        {
            if (!animate) FinishClose();
            return;
        }
        if (animate && AnimationsEnabled && !_lastImageRect.IsEmpty && _index >= 0)
        {
            var from = RectangleToScreen(_lastImageRect);
            // 閉じた後の並び（詳細パネルを戻した一覧）で、行き先と後ろの画像を決める
            Closing?.Invoke(this, EventArgs.Empty);
            if (!TakeBackdrop())
            {
                FinishClose();
                return;
            }
            var to = ThumbBoundsProvider?.Invoke(_index) is Rectangle thumb
                ? thumb
                : new Rectangle(from.X + from.Width / 2, from.Y + from.Height / 2, 0, 0);
            StartZoom(closing: true, from, to);
            return;
        }
        FinishClose();
    }

    private void FinishClose()
    {
        EndZoom();
        _actualSize = false;
        ReleaseFull();
        ReleaseAnimation();
        ClearNotice();
        ResetAdjust();
        Adjust.Visible = false;
        Visible = false;
        _loadCts?.Cancel();
        foreach (var b in _cache.Values) b.Dispose();
        _cache.Clear();
        _failed.Clear();
        _index = -1;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>一覧の中身が入れ替わった（並べ替え・リネーム等）。同じファイルを表示し続けられなければ閉じる</summary>
    public void ItemsChanged(IReadOnlyList<FileInfo> items)
    {
        if (!Visible) return;
        string? current = _index >= 0 && _index < _items.Count ? _items[_index].FullName : null;
        int found = current == null ? -1 : items.ToList().FindIndex(f => string.Equals(f.FullName, current, StringComparison.OrdinalIgnoreCase));
        if (found < 0)
        {
            Close(animate: false); // 一覧が変わったので、戻る先のサムネイルも無い
            return;
        }
        _items = items;
        _index = found;
        Invalidate();
    }

    private void ShowIndex(int index)
    {
        if (_zooming && !_closing && _index >= 0) EndZoom(); // 広がっている途中で送ったら、動きは打ち切る
        // 送ったら画面に合わせた大きさに戻す。原寸で読んだものは捨てる
        _actualSize = false;
        if (_index >= 0 && _index < _items.Count && index != _index) ReleaseFull();
        _index = Math.Clamp(index, 0, _items.Count - 1);
        CurrentChanged?.Invoke(this, _index);
        Invalidate();
        _ = LoadAroundAsync();
        if (!string.Equals(_animPath, _items[_index].FullName, StringComparison.OrdinalIgnoreCase))
        {
            ReleaseAnimation();
            ClearNotice();
            _ = LoadAnimationAsync(_items[_index].FullName, 0, paused: false);
        }
    }

    /// <summary>今の画像 → 次 → 前 の順に読む。それ以外は捨てる</summary>
    private async Task LoadAroundAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var wanted = new[] { _index, _index + 1, _index - 1 }
            .Where(i => i >= 0 && i < _items.Count)
            .Select(i => _items[i].FullName)
            .ToList();
        foreach (var key in _cache.Keys.Where(k => !wanted.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _cache[key].Dispose();
            _cache.Remove(key);
        }

        int maxEdge = MaxEdge;
        foreach (var path in wanted)
        {
            if (cts.IsCancellationRequested) return;
            if (_cache.ContainsKey(path) || _failed.Contains(path)) continue;
            Bitmap? bmp = null;
            try
            {
                await _decodeGate.WaitAsync(cts.Token);
                try
                {
                    cts.Token.ThrowIfCancellationRequested();
                    bmp = await Task.Run(() =>
                    {
                        using var image = ImageLoader.Load(path, LoadOptions.Thumbnail(maxEdge));
                        return ThumbnailGenerator.ToPArgbBitmap(image);
                    });
                }
                finally
                {
                    _decodeGate.Release();
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (cts.IsCancellationRequested) return;
                _failed.Add(path);
            }
            if (bmp == null) continue;
            if (cts.IsCancellationRequested || !Visible || _cache.ContainsKey(path))
            {
                bmp.Dispose(); // 閉じた・別の画像へ移った・並行した読み込みが先に入れた
                if (cts.IsCancellationRequested || !Visible) return;
                continue;
            }
            _cache[path] = bmp;
            if (_index >= 0 && string.Equals(_items[_index].FullName, path, StringComparison.OrdinalIgnoreCase)) Invalidate();
        }
    }

    // ---- 描画 ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_index < 0 || _index >= _items.Count) return;
        if (_zooming)
        {
            PaintZoom(g);
            return;
        }
        if (_actualSize && PaintActualSize(g)) return;
        var file = _items[_index];
        int bar = Font.Height * 2;
        var area = ImageArea;

        Rectangle imageRect = Rectangle.Empty;
        if (ShownBitmap(file) is Bitmap bmp)
        {
            imageRect = Fit(bmp.Size, area, allowUpscale: false);
            // フィルムストリップが出てくる間は速さを優先する（止まったら高い品質で描き直す）
            g.InterpolationMode = _filmSliding ? InterpolationMode.Low
                : imageRect.Width < bmp.Width ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(Adjusted(bmp, _adjustedView), imageRect);
        }
        else if (_failed.Contains(file.FullName))
        {
            TextRenderer.DrawText(g, "この画像は表示できません", Font, area, Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else if (PlaceholderProvider?.Invoke(file) is Bitmap thumb)
        {
            // 読み込み中: サムネイルを引き伸ばして先に見せる
            imageRect = Fit(thumb.Size, area, allowUpscale: true);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(thumb, imageRect);
        }
        _lastImageRect = imageRect;

        if (IsMarked?.Invoke(_index) == true && !imageRect.IsEmpty) DrawCheck(g, imageRect);
        if (_filmstrip) PaintFilmstrip(g);

        // 上: ファイル名と位置、チェック数
        var top = new Rectangle(16, 0, ContentWidth - 32, bar);
        TextRenderer.DrawText(g, $"{file.Name}    {_index + 1} / {_items.Count}{AnimationStatus}{AdjustStatus}", Font, top, Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        int marked = MarkedCount?.Invoke() ?? 0;
        if (marked > 0)
            TextRenderer.DrawText(g, $"チェック {marked} 枚", Font, top, Color.FromArgb(255, 170, 60),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);

        // 下: 操作の案内
        var bottom = new Rectangle(16, ClientSize.Height - bar, ContentWidth - 32, bar);
        string actualSize = KeyFree(ActualSizeKey) ? "    Z 100%" : "";
        TextRenderer.DrawText(g, $"← → 前後    {KeyName(MarkKey)} チェック    {KeyName(MarkNextKey)} チェックして次へ{actualSize}{AnimationGuide}{AdjustGuide}    I 詳細    Space / Esc 閉じる",
            Font, bottom, Color.Gray, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

        if (_notice != null && !imageRect.IsEmpty) PaintNotice(g, imageRect);
    }

    /// <summary>今の画像として描くもの: アニメを読み終えていれば今のコマ、まだなら先頭のコマの静止画</summary>
    private Bitmap? ShownBitmap(FileInfo file)
    {
        if (_animFrames != null && string.Equals(_animPath, file.FullName, StringComparison.OrdinalIgnoreCase))
            return _animFrames[_animFrame];
        return _cache.TryGetValue(file.FullName, out var bmp) ? bmp : null;
    }

    /// <summary>画像を置く枠（上下の文字の行と、詳細パネルを除いた部分）</summary>
    private Rectangle ImageArea
    {
        get
        {
            int bar = Font.Height * 2;
            return new Rectangle(16, bar, Math.Max(1, ContentWidth - 32), Math.Max(1, ClientSize.Height - bar * 2 - FilmstripShownHeight));
        }
    }

    /// <summary>今の画像（読み込み済みなら本来の画像、まだならサムネイル）と、止まっているときに置く位置</summary>
    private (Image Image, Rectangle Rect)? CurrentImage()
    {
        var file = _items[_index];
        if (ShownBitmap(file) is Bitmap bmp) return (bmp, Fit(bmp.Size, ImageArea, allowUpscale: false));
        if (PlaceholderProvider?.Invoke(file) is Bitmap thumb) return (thumb, Fit(thumb.Size, ImageArea, allowUpscale: true));
        return null;
    }

    private static string KeyName(Keys key) => key switch
    {
        Keys.Oem5 => "¥",
        Keys.Oem7 => "^",
        _ => new KeysConverter().ConvertToString(key) ?? key.ToString(),
    };

    private static Rectangle Fit(Size image, Rectangle area, bool allowUpscale)
    {
        double scale = Math.Min((double)area.Width / image.Width, (double)area.Height / image.Height);
        if (!allowUpscale) scale = Math.Min(1.0, scale);
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    private void DrawCheck(Graphics g, Rectangle image)
    {
        int d = LogicalToDeviceUnits(34);
        var r = new Rectangle(image.X + 8, image.Y + 8, d, d);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(Color.FromArgb(232, 112, 0))) g.FillEllipse(fill, r);
        using (var ring = new Pen(Color.White, 2.5f)) g.DrawEllipse(ring, r);
        using (var tick = new Pen(Color.White, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(tick, new[]
            {
                new PointF(r.X + d * 0.27f, r.Y + d * 0.52f),
                new PointF(r.X + d * 0.44f, r.Y + d * 0.68f),
                new PointF(r.X + d * 0.74f, r.Y + d * 0.34f),
            });
        g.SmoothingMode = SmoothingMode.None;
    }

    // ---- 開く / 閉じる動き ----

    private const int ZoomMs = 160;
    private readonly System.Windows.Forms.Timer _zoomTimer = new() { Interval = 10 };
    private readonly Stopwatch _zoomClock = new();
    private bool _zooming, _closing;
    private Bitmap? _backdrop;            // 動いている間の後ろ（一覧を撮ったもの）
    private Point _backdropAt;            // _backdrop の左上（画面座標）
    private Rectangle _zoomFrom, _zoomTo; // 画面座標。開くときの _zoomTo は使わない（今の画像の位置へ向かう）
    private Rectangle _lastImageRect;     // 止まっているときに最後に描いた画像の位置（閉じる動きの起点）

    /// <summary>Windows の「アニメーション効果」（設定 → アクセシビリティ → 視覚効果）がオンか。読めなければオン扱い</summary>
    private static bool AnimationsEnabled =>
        !SystemParametersInfo(SpiGetClientAreaAnimation, 0, out bool on, 0) || on;

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

    /// <summary>後ろに映す部品を、画面での並びのまま 1 枚に撮る（1 枚表示に隠れていても描ける）</summary>
    private bool TakeBackdrop()
    {
        var parts = (BackdropProvider?.Invoke() ?? Enumerable.Empty<Control>())
            .Where(c => c is { Visible: true, IsHandleCreated: true, Width: > 0, Height: > 0 })
            .Select(c => (Control: c, Bounds: c.RectangleToScreen(new Rectangle(Point.Empty, c.Size))))
            .ToList();
        if (parts.Count == 0) return false;
        var all = parts.Select(p => p.Bounds).Aggregate(Rectangle.Union);
        _backdrop?.Dispose();
        _backdrop = new Bitmap(all.Width, all.Height);
        _backdropAt = all.Location;
        using var g = Graphics.FromImage(_backdrop);
        foreach (var (control, bounds) in parts)
        {
            using var shot = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(shot, new Rectangle(Point.Empty, control.Size));
            g.DrawImageUnscaled(shot, bounds.X - all.X, bounds.Y - all.Y);
        }
        return true;
    }

    private void StartZoom(bool closing, Rectangle from, Rectangle to)
    {
        _zooming = true;
        _closing = closing;
        _zoomFrom = from;
        _zoomTo = to;
        _zoomClock.Restart();
        _zoomTimer.Start();
        Invalidate();
    }

    private void EndZoom()
    {
        _zoomTimer.Stop();
        _zooming = _closing = false;
        _backdrop?.Dispose();
        _backdrop = null;
        Invalidate();
    }

    private void OnZoomTick(object? sender, EventArgs e)
    {
        if (_zoomClock.ElapsedMilliseconds < ZoomMs)
        {
            Invalidate();
            return;
        }
        if (_closing) FinishClose();
        else EndZoom(); // 止まった位置で、文字の行と高い品質で描き直す
    }

    /// <summary>動いている間: 一覧の上を暗くしていき、画像をサムネイルの位置と 1 枚表示の位置の間に描く</summary>
    private void PaintZoom(Graphics g)
    {
        double t = Math.Min(1.0, _zoomClock.ElapsedMilliseconds / (double)ZoomMs);
        double eased = 1 - Math.Pow(1 - t, 3); // 終わりがゆっくり
        double shown = _closing ? 1 - eased : eased; // 1 枚表示にどれだけ近いか

        if (_backdrop != null) g.DrawImageUnscaled(_backdrop, PointToClient(_backdropAt));
        using (var dim = new SolidBrush(Color.FromArgb((int)(255 * shown), BackColor))) g.FillRectangle(dim, ClientRectangle);

        if (CurrentImage() is not var (image, rest)) return;
        var from = RectangleToClient(_zoomFrom);
        var to = _closing ? RectangleToClient(_zoomTo) : rest;
        var rect = Lerp(from, to, eased);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        // 動いている間は速さを優先する（止まったら高い品質で描き直す）
        g.InterpolationMode = InterpolationMode.Low;
        g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        g.DrawImage(image, rect);
    }

    private static Rectangle Lerp(Rectangle a, Rectangle b, double t)
    {
        int L(int x, int y) => (int)Math.Round(x + (y - x) * t);
        return Rectangle.FromLTRB(L(a.Left, b.Left), L(a.Top, b.Top), L(a.Right, b.Right), L(a.Bottom, b.Bottom));
    }

    // ---- 100%（Z を押している間だけ） ----

    private const Keys ActualSizeKey = Keys.Z;

    /// <summary>チェックのキー（settings.json で変えられる）と同じキーなら、チェックを優先して 100% やアニメの操作には使わない</summary>
    private bool KeyFree(Keys key) => MarkKey != key && MarkNextKey != key;
    private bool _actualSize;             // Z を押している間
    private Bitmap? _full;                // 原寸で読んだ今の画像
    private string? _fullPath;            // 原寸で読んだ（読んでいる）画像
    private Size _fullSize;               // 原寸の大きさ（読み終わる前はヘッダーから）
    private bool _fullFailed;
    private CancellationTokenSource? _fullCts;
    private bool _resumeAfterActualSize;  // 100% を見ている間だけアニメを止めた

    private void BeginActualSize()
    {
        // Space を押し続けて見ているとき（離したら閉じる）は使わない。ちらっと見るだけの表示なので
        if (_actualSize || !_spaceReleased || _zooming || _index < 0) return;
        _actualSize = true;
        // アニメは止めて、今のコマを原寸で見る（離したら元どおり動かす）
        _resumeAfterActualSize = _animFrames != null && !_animPaused;
        if (_resumeAfterActualSize) PauseAnimation(true);
        _ = LoadFullAsync();
        Invalidate();
    }

    private void EndActualSize()
    {
        if (!_actualSize) return;
        _actualSize = false;
        if (_resumeAfterActualSize) PauseAnimation(false);
        _resumeAfterActualSize = false;
        Invalidate();
    }

    /// <summary>今の画像を原寸で読む。先にヘッダーで大きさを調べ、読み終わるまでは今の画像を引き伸ばして見せる</summary>
    private async Task LoadFullAsync()
    {
        string path = _items[_index].FullName;
        // アニメは今のコマを読む（読んだものはコマごとに「パス#コマ」で覚える）
        int? frame = _animFrames != null && string.Equals(_animPath, path, StringComparison.OrdinalIgnoreCase) ? _animFrame : null;
        string key = frame is int f ? $"{path}#{f}" : path;
        if (string.Equals(_fullPath, key, StringComparison.OrdinalIgnoreCase)) return; // 読み込み済み・読み込み中
        ReleaseFull();
        _fullPath = key;
        var cts = _fullCts = new CancellationTokenSource();
        try
        {
            var header = await Task.Run(() => ImageLoader.Identify(path));
            if (cts.IsCancellationRequested) return;
            if (header != null)
            {
                _fullSize = new Size(header.Width, header.Height);
                Invalidate();
            }
            var bmp = await Task.Run(() =>
            {
                using var image = frame is int i ? AnimationLoader.LoadFrame(path, i) : ImageLoader.Load(path, LoadOptions.Full);
                return ThumbnailGenerator.ToPArgbBitmap(image);
            });
            if (cts.IsCancellationRequested)
            {
                bmp.Dispose(); // 次の画像・コマへ移った・閉じた
                return;
            }
            _full = bmp;
            _fullSize = bmp.Size;
        }
        catch (Exception) // 大きすぎてメモリが足りない場合も含む
        {
            if (cts.IsCancellationRequested) return;
            _fullFailed = true;
        }
        Invalidate();
    }

    private void ReleaseFull()
    {
        _adjustedFull.Dispose();
        _fullCts?.Cancel();
        _fullCts = null;
        _full?.Dispose();
        _full = null;
        _fullPath = null;
        _fullSize = Size.Empty;
        _fullFailed = false;
    }

    /// <summary>100% で描く。原寸の大きさがまだ分からない・読めなかったときは false（いつもの表示のまま）</summary>
    private bool PaintActualSize(Graphics g)
    {
        if (_fullFailed || _fullSize.IsEmpty) return false;
        // 原寸の画像。補正中は、原寸に補正をかけたものが裏で出来るまで null（その間は画面用の画像を引き伸ばして見せる）
        Image? full = _full;
        string? waiting = _full == null ? "読み込み中…" : null;
        if (_full != null && Adjusting)
        {
            var (adjusted, state) = _adjustedFull.GetOrStart(_full, Adjust.Options, Invalidate);
            full = adjusted;
            if (adjusted == null) waiting = state == AdjustedBitmap.State.Failed ? "原寸には補正をかけられません（メモリ不足）" : "補正中…";
        }
        Image? image = full;
        if (image == null)
        {
            // 読み終わるまでは、今の画像を原寸の大きさに引き伸ばして見せる
            if (CurrentImage() is not var (current, _)) return false;
            image = current is Bitmap shown && shown == ShownBitmap(_items[_index]) ? Adjusted(shown, _adjustedView) : current;
        }

        // 画像の上をカーソルで見て回る: 表示領域の端にカーソルがあれば、画像のその端が見える
        var view = new Rectangle(0, 0, ContentWidth, ClientSize.Height);
        var cursor = PointToClient(Cursor.Position);
        int Offset(int imageLength, int viewLength, int at) => imageLength <= viewLength
            ? (viewLength - imageLength) / 2
            : -(int)Math.Round((imageLength - viewLength) * Math.Clamp(at / (double)Math.Max(1, viewLength - 1), 0, 1));
        var dest = new Rectangle(Offset(_fullSize.Width, view.Width, cursor.X), Offset(_fullSize.Height, view.Height, cursor.Y),
            _fullSize.Width, _fullSize.Height);

        g.PixelOffsetMode = PixelOffsetMode.Half;
        if (image == full)
        {
            // 画素をそのまま写す（拡大縮小しないので、ぼかさない）。見えている部分だけ描く
            var visible = Rectangle.Intersect(dest, view);
            var source = new Rectangle(visible.X - dest.X, visible.Y - dest.Y, visible.Width, visible.Height);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(image, visible, source, GraphicsUnit.Pixel);
        }
        else
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(image, dest);
        }

        // 左上に倍率（読み込み中・補正中はそのことも）
        string label = waiting == null ? "100%" : $"100%  {waiting}";
        var size = TextRenderer.MeasureText(label, Font);
        var box = new Rectangle(12, 12, size.Width + 16, size.Height + 8);
        using (var back = new SolidBrush(Color.FromArgb(160, 0, 0, 0))) g.FillRectangle(back, box);
        TextRenderer.DrawText(g, label, Font, box, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        return true;
    }

    // ---- フィルムストリップ ----

    private const int FilmSlideMs = 180;
    private readonly System.Windows.Forms.Timer _filmTimer = new() { Interval = 10 };
    private readonly Stopwatch _filmClock = new();
    private bool _filmstrip;              // 出している（閉じるまで出したまま）
    private bool _filmSliding;            // 下から出てきている途中

    private int FilmstripHeight => LogicalToDeviceUnits(84);
    private int FilmThumbSize => FilmstripHeight - LogicalToDeviceUnits(20);
    private int FilmGap => LogicalToDeviceUnits(6);

    /// <summary>今フィルムストリップが取っている高さ（出てくる途中はその分だけ）</summary>
    private int FilmstripShownHeight
    {
        get
        {
            if (!_filmstrip) return 0;
            if (!_filmSliding) return FilmstripHeight;
            double t = Math.Min(1.0, _filmClock.ElapsedMilliseconds / (double)FilmSlideMs);
            return (int)Math.Round(FilmstripHeight * (1 - Math.Pow(1 - t, 3)));
        }
    }

    /// <summary>フィルムストリップを置く場所（下の操作の案内の上。出てくる途中は下にずれている）</summary>
    private Rectangle FilmstripBounds
    {
        get
        {
            int bar = Font.Height * 2;
            int bottom = ClientSize.Height - bar;
            return new Rectangle(0, bottom - FilmstripShownHeight, ContentWidth, FilmstripHeight);
        }
    }

    /// <summary>← → やホイールで送る。送り始めたらフィルムストリップを出す（Space を押し続けて見ているときは出さない）</summary>
    private async void Navigate(int index)
    {
        if (!await ConfirmLeaveAsync()) return;
        if (_spaceReleased && !_filmstrip) ShowFilmstrip();
        ShowIndex(index);
    }

    private void ShowFilmstrip()
    {
        _filmstrip = true;
        if (AnimationsEnabled)
        {
            _filmSliding = true;
            _filmClock.Restart();
            _filmTimer.Start();
        }
        Invalidate();
    }

    private void HideFilmstrip()
    {
        _filmTimer.Stop();
        _filmstrip = _filmSliding = false;
    }

    private void OnFilmTick(object? sender, EventArgs e)
    {
        if (_filmClock.ElapsedMilliseconds >= FilmSlideMs)
        {
            _filmTimer.Stop();
            _filmSliding = false; // 止まった大きさで、高い品質で描き直す
        }
        Invalidate();
    }

    /// <summary>サムネイルが出来た（一覧と同じものを使うので、フィルムストリップに出ていれば描き直す）</summary>
    public void OnThumbnailReady()
    {
        if (Visible && _filmstrip) Invalidate(FilmstripBounds);
    }

    /// <summary>フィルムストリップのそれぞれの枠（今の画像を真ん中にして、入るだけ左右に並べる）</summary>
    private IEnumerable<(int Index, Rectangle Cell)> FilmstripCells()
    {
        var strip = FilmstripBounds;
        int size = FilmThumbSize, pitch = size + FilmGap;
        int y = strip.Y + (strip.Height - size) / 2;
        int centerX = strip.X + (strip.Width - size) / 2;
        int side = strip.Width / 2 / pitch + 1;
        for (int i = Math.Max(0, _index - side); i <= Math.Min(_items.Count - 1, _index + side); i++)
            yield return (i, new Rectangle(centerX + (i - _index) * pitch, y, size, size));
    }

    private int? FilmstripHitTest(Point p)
    {
        if (!FilmstripBounds.Contains(p)) return null;
        foreach (var (i, cell) in FilmstripCells())
            if (cell.Contains(p)) return i;
        return null;
    }

    private void PaintFilmstrip(Graphics g)
    {
        var strip = FilmstripBounds;
        var state = g.Save();
        // 出てくる途中は、下の操作の案内の行に重ならないように切る
        g.SetClip(Rectangle.FromLTRB(strip.Left, strip.Top, strip.Right, ClientSize.Height - Font.Height * 2));
        using (var back = new SolidBrush(Color.FromArgb(34, 34, 34))) g.FillRectangle(back, strip);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using var empty = new SolidBrush(Color.FromArgb(52, 52, 52));
        using var mark = new SolidBrush(Color.FromArgb(232, 112, 0));
        foreach (var (i, cell) in FilmstripCells())
        {
            if (!cell.IntersectsWith(strip)) continue;
            if (PlaceholderProvider?.Invoke(_items[i]) is Bitmap thumb)
            {
                var r = Fit(thumb.Size, cell, allowUpscale: true);
                g.DrawImage(thumb, r);
                if (i != _index)
                    using (var dim = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillRectangle(dim, r); // 今の画像以外は少し暗く
            }
            else
            {
                g.FillRectangle(empty, cell); // サムネイルがまだ無い
            }
            if (IsMarked?.Invoke(i) == true)
            {
                int d = LogicalToDeviceUnits(10);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(mark, cell.X + 4, cell.Y + 4, d, d);
                g.SmoothingMode = SmoothingMode.None;
            }
            if (i == _index)
            {
                using var border = new Pen(Color.White, LogicalToDeviceUnits(2));
                var b = Rectangle.Inflate(cell, LogicalToDeviceUnits(2), LogicalToDeviceUnits(2));
                g.DrawRectangle(border, b);
            }
        }
        g.Restore(state);
    }

    // ---- アニメ（GIF / WEBP） ----

    private const Keys PauseKey = Keys.P;
    private const Keys SaveFrameKey = Keys.Control | Keys.S;
    private readonly System.Windows.Forms.Timer _animTimer = new();
    private string? _animPath;            // 読んだ（読んでいる）アニメ
    private Bitmap[]? _animFrames;        // 全部のコマ（画面に合わせて縮めたもの）
    private int[] _animDelays = Array.Empty<int>();
    private int _animFrame;
    private bool _animPaused;
    private bool _animDownscaled;         // 元より縮めて読んだ
    private int _animEdge;                // 読んだときの長辺の上限
    private CancellationTokenSource? _animCts;

    /// <summary>
    /// 全部のコマを読んで再生を始める。コマが 1 つ・大きすぎて読めないときは先頭のコマの静止画のまま。
    /// startFrame / paused は、広くなって読み直すときに今の状態を引き継ぐため
    /// </summary>
    private async Task LoadAnimationAsync(string path, int startFrame, bool paused)
    {
        if (!AnimationLoader.MayBeAnimated(path)) return;
        _animCts?.Cancel();
        var cts = _animCts = new CancellationTokenSource();
        _animPath = path;
        int maxEdge = MaxEdge;
        (Bitmap[] Frames, int[] Delays, bool Downscaled)? loaded;
        try
        {
            // 静止画の読み込み（今の画像と前後）と順番に。先頭のコマの静止画が先に出る
            await _decodeGate.WaitAsync(cts.Token);
            try
            {
                loaded = await Task.Run(() => DecodeAnimation(path, maxEdge, cts.Token), cts.Token);
            }
            finally
            {
                _decodeGate.Release();
            }
        }
        catch (Exception) // 取り消し・読めない・メモリが足りない → 静止画のまま
        {
            return;
        }
        if (loaded is not var (frames, delays, downscaled)) return;
        if (cts.IsCancellationRequested || !Visible)
        {
            foreach (var b in frames) b.Dispose(); // 別の画像へ移った・閉じた・読み直しが始まった
            return;
        }
        DisposeAnimationFrames();
        if (Adjust.Visible)
        {
            // 補正パネルを開いた後でアニメだと分かった: 保存すると静止画になるので閉じる
            HideAdjust();
            ShowNotice("アニメーションは補正できません（保存すると静止画になるため）");
        }
        _animFrames = frames;
        _animDelays = delays;
        _animDownscaled = downscaled;
        _animEdge = maxEdge;
        _animFrame = Math.Clamp(startFrame, 0, frames.Length - 1);
        _animPaused = paused;
        if (_actualSize && !_animPaused)
        {
            // 100% を見ている間に読み終えた: 離すまで止めておく
            _animPaused = _resumeAfterActualSize = true;
        }
        if (!_animPaused) StartAnimTimer();
        Invalidate();
    }

    private static (Bitmap[], int[], bool)? DecodeAnimation(string path, int maxEdge, CancellationToken ct)
    {
        using var anim = AnimationLoader.Load(path, maxEdge, ct);
        if (anim == null) return null;
        var frames = new Bitmap[anim.Count];
        try
        {
            for (int i = 0; i < frames.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                frames[i] = ThumbnailGenerator.ToPArgbBitmap(anim.Image.Frames[i]);
            }
        }
        catch
        {
            foreach (var b in frames) b?.Dispose();
            throw;
        }
        return (frames, anim.DelaysMs.ToArray(), anim.Downscaled);
    }

    private void DisposeAnimationFrames()
    {
        _animTimer.Stop();
        if (_animFrames != null)
            foreach (var b in _animFrames) b.Dispose();
        _animFrames = null;
    }

    private void ReleaseAnimation()
    {
        _animCts?.Cancel();
        _animCts = null;
        DisposeAnimationFrames();
        _animPath = null;
        _animDelays = Array.Empty<int>();
        _animFrame = 0;
        _animPaused = _animDownscaled = _resumeAfterActualSize = false;
        _animEdge = 0;
    }

    private void StartAnimTimer()
    {
        _animTimer.Interval = Math.Max(1, _animDelays[_animFrame]);
        _animTimer.Start();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        _animTimer.Stop();
        if (_animFrames == null || _animPaused || !Visible) return;
        _animFrame = (_animFrame + 1) % _animFrames.Length;
        StartAnimTimer();
        Invalidate();
    }

    private void PauseAnimation(bool pause)
    {
        if (_animFrames == null) return;
        _animPaused = pause;
        _animTimer.Stop();
        if (!pause) StartAnimTimer();
        Invalidate();
    }

    /// <summary>1 コマ進める / 戻す（端では反対の端へ）。止めてから送る</summary>
    private void StepAnimation(int delta)
    {
        if (_animFrames == null) return;
        _resumeAfterActualSize = false; // コマを選んだら、100% をやめても止めたまま
        PauseAnimation(true);
        _animFrame = (_animFrame + delta + _animFrames.Length) % _animFrames.Length;
        if (_actualSize) _ = LoadFullAsync(); // 100% で見ているなら、そのコマを原寸で読み直す
        Invalidate();
    }

    /// <summary>上の行に出すアニメの状態（アニメでなければ空）</summary>
    private string AnimationStatus => _animFrames == null ? ""
        : $"    {(_animPaused ? "一時停止" : "再生中")}  コマ {_animFrame + 1} / {_animFrames.Length}";

    /// <summary>下の操作の案内に足すアニメの操作（アニメでなければ空）</summary>
    private string AnimationGuide
    {
        get
        {
            if (_animFrames == null) return "";
            string pause = KeyFree(PauseKey) ? $"    P {(_animPaused ? "再生" : "一時停止")}" : "";
            string step = KeyFree(Keys.Oemcomma) && KeyFree(Keys.OemPeriod) ? "    , . コマ送り" : "";
            return $"{pause}{step}    Ctrl+S フレーム保存";
        }
    }

    /// <summary>今のコマを原寸の PNG で、元のファイルと同じフォルダに保存する</summary>
    private async Task SaveFrameAsync()
    {
        // 保存中の Ctrl+S（押し続けたときのキーリピートも）は受けない。原寸で読み直すので、重なるとメモリを使いすぎる
        if (_animFrames == null || _animPath == null || _savingFrame) return;
        string source = _animPath;
        int frame = _animFrame, count = _animFrames.Length;
        _savingFrame = true;
        try
        {
            string saved = await Task.Run(() => AnimationLoader.SaveFrame(source, frame, count));
            ShowNotice($"フレーム {frame + 1} を保存しました（{Path.GetFileName(saved)}）");
            FrameSaved?.Invoke(this, saved);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowNotice($"フレームを保存できませんでした: {ex.Message}");
        }
        finally
        {
            _savingFrame = false;
        }
    }

    private bool _savingFrame;

    // ---- 補正（E で右側にパネル） ----

    private const Keys AdjustKey = Keys.E;
    private const Keys SaveKey = Keys.Control | Keys.S;
    private bool _comparing;                                  // 「補正前」を押している間
    private readonly AdjustedBitmap _adjustedView = new();    // 画面に合わせて読んだ画像に補正をかけたもの
    private readonly AdjustedBitmap _adjustedFull = new();    // 100% 用に原寸で読んだ画像に補正をかけたもの

    /// <summary>補正をかけて見せているか（パネルを開いていて、値が既定でなく、「補正前」を押していない）</summary>
    private bool Adjusting => Adjust.Visible && !_comparing && !Adjust.Options.IsIdentity;

    /// <summary>保存していない補正があるか</summary>
    private bool HasUnsavedAdjust => Adjust.Visible && !Adjust.Options.IsIdentity;

    private Image Adjusted(Bitmap source, AdjustedBitmap cache) => Adjusting ? cache.Get(source, Adjust.Options) : source;

    private async void ToggleAdjust()
    {
        if (Adjust.Visible)
        {
            if (await ConfirmLeaveAsync()) HideAdjust();
            return;
        }
        // Space を押し続けて見ているとき（離したら閉じる）は開かない。ちらっと見るだけの表示なので
        if (!_spaceReleased || _zooming || _index < 0 || _failed.Contains(_items[_index].FullName)) return;
        // アニメや複数ページの TIFF かどうかは、再生の読み込みを待たずにヘッダーで調べる（読み込み中や、大きすぎて再生しないアニメもあるため）
        string path = _items[_index].FullName;
        bool animated = _animFrames != null || await Task.Run(() => Adjuster.FrameCount(path)) > 1;
        if (!Visible || Adjust.Visible || _index < 0 || !string.Equals(_items[_index].FullName, path, StringComparison.OrdinalIgnoreCase)) return;
        if (animated)
        {
            ShowNotice(Adjuster.MultiFrameMessage);
            return;
        }
        ResetAdjust();
        Adjust.Visible = true;
        Invalidate();
    }

    private void HideAdjust()
    {
        ResetAdjust();
        Adjust.Visible = false;
        ReloadForNewSize(); // 広くなったので読み直す（狭い幅で読んだものは拡大しないので、そのままだと小さいまま）
        Invalidate();
    }

    /// <summary>値を既定に戻し、補正をかけた画像を捨てる</summary>
    private void ResetAdjust()
    {
        _comparing = false;
        Adjust.Options = new AdjustOptions();
        _adjustedView.Dispose();
        _adjustedFull.Dispose();
    }

    /// <summary>保存していない補正があれば、保存するか聞く。今の画像から離れてよければ true（捨てたときは値を戻してある）</summary>
    private async Task<bool> ConfirmLeaveAsync()
    {
        if (!HasUnsavedAdjust) return true;
        if (Adjust.Saving) return false; // 保存が終わるまでは離れない
        var answer = MessageBox.Show(FindForm(), "補正を保存しますか？（元のファイルを上書きします）", "補正",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        Focus();
        if (answer == DialogResult.Cancel) return false;
        if (answer == DialogResult.No)
        {
            ResetAdjust();
            Invalidate();
            return true;
        }
        return await SaveAdjustAsync();
    }

    /// <summary>
    /// 今の画像に補正をかけて保存する。書き出せる形式なら上書き、HEIC / RAW などは元を残して同じ名前の JPG に。
    /// 撮影情報（EXIF）を残せない画像は、先に確かめる。保存できたら true
    /// </summary>
    private async Task<bool> SaveAdjustAsync()
    {
        if (!HasUnsavedAdjust || Adjust.Saving || _index < 0) return false;
        string source = _items[_index].FullName;
        var options = Adjust.Options;
        // 上書きするのは元のファイルだけ。JPG を別に作るときは、保存している間にほかで同じ名前ができても上書きせず、次の名前にする
        bool overwrite = ImageSaver.CanWrite(Path.GetExtension(source));
        string target = overwrite ? source : ImageSaver.UniquePath(Path.ChangeExtension(source, ".jpg"));
        Adjust.Saving = true;
        try
        {
            bool allowMetadataLoss = false;
            while (true)
            {
                try
                {
                    await Task.Run(() => Adjuster.ApplyToFile(source, target, options, overwrite, allowMetadataLoss));
                    break;
                }
                catch (DestinationExistsException)
                {
                    target = ImageSaver.UniquePath(Path.ChangeExtension(source, ".jpg"));
                }
                catch (MetadataLossException)
                {
                    var answer = MessageBox.Show(FindForm(),
                        $"{Path.GetFileName(source)} は撮影情報（EXIF など）を残して保存できません。撮影情報なしで保存しますか？",
                        "補正", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    Focus();
                    if (answer != DialogResult.OK) return false;
                    allowMetadataLoss = true;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowNotice($"保存できませんでした: {ex.Message}");
            return false;
        }
        finally
        {
            Adjust.Saving = false;
        }

        Adjust.Last = options;
        if (Visible && _index >= 0 && string.Equals(_items[_index].FullName, source, StringComparison.OrdinalIgnoreCase))
        {
            if (target == source)
            {
                // 上書きした: 補正をかけた画面用の画像を、そのまま今の画像にする（読み直すまでの間も補正後の見た目のまま）
                if (_cache.TryGetValue(source, out var old) && _adjustedView.Take(old, options) is Bitmap baked)
                {
                    old.Dispose();
                    _cache[source] = baked;
                }
                ReleaseFull(); // 原寸で読んだものは補正前なので捨てる
            }
            ResetAdjust();
            Invalidate();
        }
        ShowNotice(target == source ? "補正して上書きしました" : $"補正して {Path.GetFileName(target)} に保存しました");
        ImageAdjusted?.Invoke(this, target);
        return true;
    }

    /// <summary>自動補正: 今の画像（画面に合わせて読んだもの）からレベル補正の値を決める。ほかのスライダーはそのまま</summary>
    private void RunAuto()
    {
        if (_index < 0 || ShownBitmap(_items[_index]) is not Bitmap bmp) return;
        var auto = AdjustedBitmap.Auto(bmp);
        Adjust.Options = Adjust.Options with { BlackPoint = auto.BlackPoint, WhitePoint = auto.WhitePoint, Gamma = auto.Gamma };
        if (auto.IsIdentity) ShowNotice("自動補正: 直すところが見つかりませんでした");
    }

    /// <summary>上の行に出す補正の状態</summary>
    private string AdjustStatus => !HasUnsavedAdjust ? "" : _comparing ? "    補正前を表示中" : "    補正中（未保存）";

    /// <summary>下の操作の案内に足す補正の操作</summary>
    private string AdjustGuide => !KeyFree(AdjustKey) ? "" : Adjust.Visible ? "    Ctrl+S 保存    E 補正を閉じる" : "    E 補正";

    /// <summary>
    /// 画像に補正をかけたもの。元の画像と値が同じ間は作り直さない。
    /// 画面用（小さい）はその場で作り（Get）、原寸は裏で作る（GetOrStart。UI を止めない・メモリが足りなければあきらめる）
    /// </summary>
    private sealed class AdjustedBitmap : IDisposable
    {
        public enum State { Ready, Pending, Failed }

        private Bitmap? _source;
        private AdjustOptions? _options;
        private Bitmap? _result;
        private bool _failed;                  // _source / _options で作れなかった
        private CancellationTokenSource? _cts; // 裏で作っている途中

        public Bitmap Get(Bitmap source, AdjustOptions options)
        {
            if (_result != null && Matches(source, options)) return _result;
            var result = Make(ReadPixels(source), source.Width, source.Height, options);
            Dispose();
            (_source, _options, _result) = (source, options, result);
            return result;
        }

        /// <summary>出来ていれば返す。まだなら裏で作り始め、出来たら ready を呼ぶ（UI のスレッドで）</summary>
        public (Bitmap? Result, State State) GetOrStart(Bitmap source, AdjustOptions options, Action ready)
        {
            if (Matches(source, options))
            {
                if (_result != null) return (_result, State.Ready);
                if (_failed) return (null, State.Failed);
                if (_cts != null) return (null, State.Pending);
            }
            Dispose();
            (_source, _options) = (source, options);
            (byte[] Pixels, int Stride) read;
            try
            {
                // 画素の読み出しだけは UI のスレッドで（描いている Bitmap を別のスレッドから触らない）
                read = ReadPixels(source);
            }
            catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException)
            {
                _failed = true;
                return (null, State.Failed);
            }
            var cts = _cts = new CancellationTokenSource();
            int w = source.Width, h = source.Height;
            Task.Run(() => Make(read, w, h, options), cts.Token).ContinueWith(t =>
            {
                if (cts.IsCancellationRequested)
                {
                    if (t.IsCompletedSuccessfully) t.Result.Dispose(); // 値が変わった・捨てた
                    return;
                }
                _cts = null;
                if (t.IsCompletedSuccessfully) _result = t.Result;
                else _failed = true; // メモリが足りない など
                ready();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
            return (null, State.Pending);
        }

        /// <summary>作ったものを引き取る（以後この入れ物は持たない）。まだ作っていなければ作る</summary>
        public Bitmap Take(Bitmap source, AdjustOptions options)
        {
            var result = Get(source, options);
            _result = null;
            Dispose();
            return result;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts = null;
            _result?.Dispose();
            (_source, _options, _result, _failed) = (null, null, null, false);
        }

        private bool Matches(Bitmap source, AdjustOptions options) => ReferenceEquals(_source, source) && _options == options;

        private static Bitmap Make((byte[] Pixels, int Stride) read, int width, int height, AdjustOptions options)
        {
            var (pixels, stride) = read;
            Adjuster.ApplyBgra(pixels, width, height, stride, options);
            Premultiply(pixels);
            var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            var data = result.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            finally
            {
                result.UnlockBits(data);
            }
            return result;
        }

        public static AdjustOptions Auto(Bitmap source)
        {
            var (pixels, stride) = ReadPixels(source);
            return Adjuster.AutoBgra(pixels, source.Width, source.Height, stride);
        }

        /// <summary>
        /// 画素を BGRA の並び（1 行 = stride バイト）で読む。表示用の Bitmap は乗算済みアルファなので、
        /// 補正は保存のとき（ImageSharp の画像）と同じく乗算を外した色にかける（半透明のところで結果がずれないように）
        /// </summary>
        private static (byte[] Pixels, int Stride) ReadPixels(Bitmap source)
        {
            var data = source.LockBits(new Rectangle(Point.Empty, source.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            byte[] pixels;
            try
            {
                pixels = new byte[data.Stride * source.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            finally
            {
                source.UnlockBits(data);
            }
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int a = pixels[i + 3];
                if (a is 0 or 255) continue;
                pixels[i] = (byte)Math.Min(255, (pixels[i] * 255 + a / 2) / a);
                pixels[i + 1] = (byte)Math.Min(255, (pixels[i + 1] * 255 + a / 2) / a);
                pixels[i + 2] = (byte)Math.Min(255, (pixels[i + 2] * 255 + a / 2) / a);
            }
            return (pixels, data.Stride);
        }

        /// <summary>乗算済みアルファに戻す（透明な画素は色も 0）</summary>
        private static void Premultiply(byte[] pixels)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int a = pixels[i + 3];
                if (a == 255) continue;
                pixels[i] = (byte)((pixels[i] * a + 127) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * a + 127) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * a + 127) / 255);
            }
        }
    }

    // ---- お知らせ（画像の上に少しの間だけ出す） ----

    private readonly System.Windows.Forms.Timer _noticeTimer = new() { Interval = 2500 };
    private string? _notice;

    private void ShowNotice(string text)
    {
        if (!Visible) return;
        _notice = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
        Invalidate();
    }

    private void ClearNotice()
    {
        _noticeTimer.Stop();
        _notice = null;
    }

    /// <summary>画像の上の端に、暗い地に乗せて出す</summary>
    private void PaintNotice(Graphics g, Rectangle image)
    {
        var size = TextRenderer.MeasureText(_notice, Font);
        var box = new Rectangle(0, 0, Math.Min(Math.Max(1, ContentWidth - 32), size.Width + 24), size.Height + 12);
        box.X = Math.Clamp(image.X + (image.Width - box.Width) / 2, 16, Math.Max(16, ContentWidth - 16 - box.Width));
        box.Y = image.Y + LogicalToDeviceUnits(12);
        using (var back = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.FillRectangle(back, box);
        TextRenderer.DrawText(g, _notice, Font, box, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ReloadForNewSize();
    }

    /// <summary>表示できる大きさが変わったら読み直す（小さく読んだものを引き伸ばさない）</summary>
    private void ReloadForNewSize()
    {
        if (!Visible || _closing) return; // 閉じる途中は描いている画像を捨てない
        foreach (var b in _cache.Values) b.Dispose();
        _cache.Clear();
        _ = LoadAroundAsync();
        // アニメは読み直しが重いので、縮めて読んでいて、前より広くなったときだけ（今のコマと止めているかはそのまま）
        if (_animPath != null && _animFrames != null && _animDownscaled && MaxEdge > _animEdge)
            _ = LoadAnimationAsync(_animPath, _animFrame, _animPaused);
    }

    // ---- 操作 ----

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Space
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_closing)
        {
            e.Handled = e.SuppressKeyPress = true; // 閉じる途中の操作は受けない
            return;
        }
        if (!e.Control && !e.Alt && e.KeyCode == MarkKey)
        {
            ToggleMarkRequested?.Invoke(this, _index);
            Invalidate();
        }
        else if (!e.Control && !e.Alt && e.KeyCode == MarkNextKey)
        {
            ToggleMarkRequested?.Invoke(this, _index);
            if (_index < _items.Count - 1) Navigate(_index + 1);
            else Invalidate();
        }
        else
        {
            switch (e.KeyCode)
            {
                case Keys.Left or Keys.Up when _index > 0: Navigate(_index - 1); break;
                case Keys.Right or Keys.Down when _index < _items.Count - 1: Navigate(_index + 1); break;
                case Keys.Home: Navigate(0); break;
                case Keys.End: Navigate(_items.Count - 1); break;
                case Keys.Escape: Close(); break;
                case Keys.I when !e.Control && !e.Alt: ToggleDetails(); break;
                case AdjustKey when !e.Control && !e.Alt: ToggleAdjust(); break;
                case ActualSizeKey when !e.Control && !e.Alt: BeginActualSize(); break;
                case PauseKey when !e.Control && !e.Alt && _animFrames != null: PauseAnimation(!_animPaused); break;
                case Keys.Oemcomma when !e.Control && !e.Alt && _animFrames != null: StepAnimation(-1); break;
                case Keys.OemPeriod when !e.Control && !e.Alt && _animFrames != null: StepAnimation(+1); break;
                // 開いたときの Space を押し続けている間（キーリピート）は閉じない
                case Keys.Space when _spaceReleased: Close(); break;
                default: return;
            }
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    /// <summary>Ctrl+S は本体のショートカット（設定で割り当てられていることもある）より先に受ける</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == SaveKey && Visible && !_closing && Adjust.Visible)
        {
            _ = SaveAdjustAsync();
            return true;
        }
        if (keyData == SaveFrameKey && Visible && !_closing && _animFrames != null)
        {
            _ = SaveFrameAsync();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == ActualSizeKey) EndActualSize();
        if (e.KeyCode != Keys.Space || _spaceReleased) return;
        _spaceReleased = true;
        // 開いたときの Space を長く押していた = 「押している間だけ」見たい → 離したら閉じる
        if (_openedByKey && _openedFor.ElapsedMilliseconds >= HoldThresholdMs) Close();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_closing) return;
        if (e.Delta < 0 && _index < _items.Count - 1) Navigate(_index + 1);
        else if (e.Delta > 0 && _index > 0) Navigate(_index - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // ダブルクリックの 2 回目は無視する（1 回目で並びが寄り直していて、隣の画像に当たるため）。
        // 100% 表示の間はフィルムストリップを隠しているので反応しない
        if (_closing || e.Button != MouseButtons.Left || e.Clicks > 1 || !_filmstrip || _actualSize) return;
        if (FilmstripHitTest(e.Location) is int i && i != _index) Navigate(i);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_actualSize) Invalidate(); // 100% のときはカーソルで見える範囲が動く
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_filmstrip && !_actualSize && FilmstripBounds.Contains(e.Location)) return; // フィルムストリップのダブルクリックでは閉じない
        if (!_closing) Close();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        EndActualSize(); // Z を離したことが届かないので戻す
        // 別の操作（メニュー・ダイアログ等）に移ったら、押し続けの判定はやめる
        _spaceReleased = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loadCts?.Cancel();
            _zoomTimer.Dispose();
            _filmTimer.Dispose();
            ReleaseAnimation();
            _animTimer.Dispose();
            _noticeTimer.Dispose();
            _backdrop?.Dispose();
            ReleaseFull();
            _adjustedView.Dispose();
            foreach (var b in _cache.Values) b.Dispose();
            _cache.Clear();
        }
        base.Dispose(disposing);
    }
}
