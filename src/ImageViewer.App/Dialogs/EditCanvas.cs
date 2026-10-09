// 編集ダイアログ（切り抜き / モザイク・ぼかし / 枠・矢印）のキャンバス。
// 画像を収める倍率と位置を持ち、画像座標 ⇔ キャンバス座標を変換する。何を描くか・ドラッグで何をするかは各ダイアログが決める
namespace ImageViewer.App.Dialogs;

internal sealed class EditCanvas : Panel
{
    private readonly System.Windows.Forms.Timer _resizeDelay = new() { Interval = 80 };
    private int _imageWidth, _imageHeight;

    /// <summary>画像 → キャンバスの倍率</summary>
    public double Zoom { get; private set; } = 1;
    private double _offX, _offY;

    /// <summary>大きさが変わって落ち着いた（重い縮小をやり直すのは、ドラッグで大きさを変えている間でなく止まってから）</summary>
    public event EventHandler? Settled;

    public EditCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(32, 32, 32);
        Cursor = Cursors.Cross;
        _resizeDelay.Tick += (_, _) =>
        {
            _resizeDelay.Stop();
            Settled?.Invoke(this, EventArgs.Empty);
        };
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _resizeDelay.Stop();
        _resizeDelay.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _resizeDelay.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>width × height の画像を、キャンバスいっぱいに中央で収める。表示の大きさを返す</summary>
    public Size FitImage(int width, int height)
    {
        int cw = Math.Max(1, ClientSize.Width), ch = Math.Max(1, ClientSize.Height);
        _imageWidth = width;
        _imageHeight = height;
        Zoom = Math.Min((double)cw / width, (double)ch / height);
        int dw = Math.Max(1, (int)Math.Round(width * Zoom)), dh = Math.Max(1, (int)Math.Round(height * Zoom));
        _offX = (cw - dw) / 2.0;
        _offY = (ch - dh) / 2.0;
        return new Size(dw, dh);
    }

    /// <summary>収めた画像の左上（キャンバス座標）</summary>
    public PointF ImageOrigin => new((float)_offX, (float)_offY);

    /// <summary>表示用のビットマップを、収めた位置に描く</summary>
    public void DrawDisplay(Graphics g, Bitmap display) => g.DrawImageUnscaled(display, (int)Math.Round(_offX), (int)Math.Round(_offY));

    public (double X, double Y) ImageToCanvas(double ix, double iy) => (_offX + ix * Zoom, _offY + iy * Zoom);
    public (double X, double Y) CanvasToImage(double cx, double cy) => ((cx - _offX) / Zoom, (cy - _offY) / Zoom);

    public PointF ToCanvasPoint(double ix, double iy)
    {
        var (cx, cy) = ImageToCanvas(ix, iy);
        return new PointF((float)cx, (float)cy);
    }

    /// <summary>画像座標の点を画像の中に収める</summary>
    public (double X, double Y) ClampToImage((double X, double Y) p) => (Math.Clamp(p.X, 0, _imageWidth), Math.Clamp(p.Y, 0, _imageHeight));

    /// <summary>つかむ点（四隅など）の当たり判定の半径（キャンバスの px）</summary>
    public int HandleRadius => Math.Max(7, 7 * DeviceDpi / 96);

    /// <summary>キャンバス座標の点 (x, y) が、画像座標の点 handle のつかむ点の上か</summary>
    public bool HitsHandle(int x, int y, (double X, double Y) handle)
    {
        var (cx, cy) = ImageToCanvas(handle.X, handle.Y);
        return Math.Abs(x - cx) <= HandleRadius && Math.Abs(y - cy) <= HandleRadius;
    }

    /// <summary>つかむ点を描く（白い四角に青い縁）</summary>
    public void DrawHandles(Graphics g, IEnumerable<(double X, double Y)> handles)
    {
        using var fill = new SolidBrush(Color.White);
        using var border = new Pen(Color.FromArgb(0, 120, 215), 1);
        int hs = HandleRadius - 3;
        foreach (var (hx, hy) in handles)
        {
            var p = ToCanvasPoint(hx, hy);
            g.FillRectangle(fill, p.X - hs, p.Y - hs, hs * 2, hs * 2);
            g.DrawRectangle(border, p.X - hs, p.Y - hs, hs * 2, hs * 2);
        }
    }
}
