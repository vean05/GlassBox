using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using static GlassBox.Native;

namespace GlassBox;

/// <summary>
/// 截图飞进胶囊：刚截的图先在屏幕中间定格一下（闪一下白），再沿一条弧线缩小、飞进胶囊。
/// 一个临时的透明窗口：不接鼠标、不抢焦点、不进任务栏，也不会被下一张截图拍进去；
/// 飞完（不到 1 秒）就关掉，画布和缩略图都还回去。
/// </summary>
internal sealed class ShotFlight : Form
{
    const int HoldMs = 280, FlyMs = 620;
    const float ShadowRoom = 26; // 四周留给阴影

    readonly Bitmap image;
    readonly Func<RectangleF> target; // 胶囊现在在屏幕上的位置（飞的途中它可能还在动）
    readonly Action arrived;
    readonly Surface surface = new();
    readonly System.Windows.Forms.Timer tick = new() { Interval = 10 };
    readonly RectangleF start;
    long since = -1;
    bool done;

    /// <summary>后台读图、缩小，读好了在界面线程上起飞；读不出来（文件还在写、不是图片）就直接 failed。</summary>
    public static void Launch(Control ui, string path, Func<RectangleF> target, Action arrived, Action failed)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var thumb = LoadThumb(path);
            try
            {
                ui.BeginInvoke(new Action(() =>
                {
                    if (thumb == null || ui.IsDisposed)
                    {
                        thumb?.Dispose();
                        failed();
                        return;
                    }
                    new ShotFlight(thumb, target, arrived).Show();
                }));
            }
            catch (InvalidOperationException) { thumb?.Dispose(); } // 正在退出
        });
    }

    /// <summary>截图工具可能还没写完：读不出来就等一下再试，最多一秒。读整个文件到内存里解码，不锁住它。</summary>
    static Bitmap? LoadThumb(string path)
    {
        for (int attempt = 0; attempt < 7; attempt++)
        {
            try
            {
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                using var full = Image.FromStream(stream);
                float k = Math.Min(1, 1000f / Math.Max(full.Width, full.Height));
                var thumb = new Bitmap(Math.Max(1, (int)(full.Width * k)), Math.Max(1, (int)(full.Height * k)), PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(thumb);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, thumb.Width, thumb.Height);
                return thumb;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(150);
            }
        }
        return null;
    }

    ShotFlight(Bitmap image, Func<RectangleF> target, Action arrived)
    {
        this.image = image;
        this.target = target;
        this.arrived = arrived;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        var screen = Screen.PrimaryScreen!.Bounds;
        Bounds = new Rectangle(screen.X + screen.Width / 2, screen.Y + screen.Height / 2, 1, 1);
        // 定格时占屏幕中间四成左右，按图本来的比例
        float k = Math.Min(screen.Width * .42f / image.Width, screen.Height * .46f / image.Height);
        float w = image.Width * k, h = image.Height * k;
        start = new RectangleF(screen.X + (screen.Width - w) / 2, screen.Y + (screen.Height - h) / 2, w, h);
        tick.Tick += (_, _) => Step();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = WS_POPUP;
            cp.ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); // 紧接着再截一张也不会拍到它
        since = Environment.TickCount64;
        Step();
        tick.Start();
    }

    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    void Step()
    {
        if (done) return;
        float s = DeviceDpi / 96f;
        long t = Environment.TickCount64 - since;
        RectangleF r;
        float alpha = 1, flash = 0, radius = 14 * s;
        if (t < HoldMs)
        {
            // 定格：轻轻弹一下，盖着的那层白光慢慢退掉，像快门
            float p = t / (float)HoldMs, pop = 1 - (1 - p) * (1 - p) * (1 - p);
            r = Scale(start, .94f + .06f * pop);
            flash = .6f * (1 - p);
        }
        else
        {
            float p = Math.Clamp((t - HoldMs) / (float)FlyMs, 0, 1);
            if (p >= 1)
            {
                Finish();
                return;
            }
            var end = target();
            var endCenter = new PointF(end.X + end.Width / 2, end.Y + end.Height / 2);
            var startCenter = new PointF(start.X + start.Width / 2, start.Y + start.Height / 2);
            // 先慢后快地被吸过去，走一条往右鼓的弧线；个头缩得比位置快，快到时已经很小了
            float move = p < .5f ? 4 * p * p * p : 1 - MathF.Pow(-2 * p + 2, 3) / 2;
            float shrink = 1 - MathF.Pow(1 - p, 2.2f);
            var control = new PointF(startCenter.X + start.Width * .32f, endCenter.Y + (startCenter.Y - endCenter.Y) * .55f);
            float u = 1 - move;
            var center = new PointF(
                u * u * startCenter.X + 2 * u * move * control.X + move * move * endCenter.X,
                u * u * startCenter.Y + 2 * u * move * control.Y + move * move * endCenter.Y);
            float endW = 34 * s, endH = Math.Min(endW * start.Height / start.Width, end.Height * .8f);
            float w = start.Width + (endW - start.Width) * shrink, h = start.Height + (endH - start.Height) * shrink;
            r = new RectangleF(center.X - w / 2, center.Y - h / 2, w, h);
            radius = (14 - 8 * shrink) * s;
            alpha = 1 - Math.Clamp((p - .78f) / .22f, 0, 1) * .7f;
        }
        Render(r, radius, alpha, flash, s);
    }

    static RectangleF Scale(RectangleF r, float k)
    {
        float w = r.Width * k, h = r.Height * k;
        return new RectangleF(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
    }

    void Render(RectangleF r, float radius, float alpha, float flash, float s)
    {
        if (!IsHandleCreated) return;
        float m = ShadowRoom * s;
        int left = (int)Math.Floor(r.X - m), top = (int)Math.Floor(r.Y - m);
        int ww = (int)Math.Ceiling(r.Right + m) - left, wh = (int)Math.Ceiling(r.Bottom + m) - top;
        var g = surface.Begin(ww, wh);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = r.Width > 200 * s ? InterpolationMode.HighQualityBilinear : InterpolationMode.Bilinear;
        var box = new RectangleF(r.X - left, r.Y - top, r.Width, r.Height);

        // 柔和的影子：往下偏一点的几圈半透明圆角
        float depth = Math.Clamp(r.Width / (300 * s), .25f, 1);
        for (int i = 5; i >= 1; i--)
        {
            var shadow = RectangleF.Inflate(box, i * 3 * s * depth, i * 3 * s * depth);
            shadow.Offset(0, 6 * s * depth);
            using var path = Shapes.RoundRect(shadow, radius + i * 3 * s * depth);
            using var brush = new SolidBrush(Color.FromArgb(10, 0, 0, 0));
            g.FillPath(brush, path);
        }
        using (var shape = Shapes.RoundRect(box, radius))
        {
            var state = g.Save();
            g.SetClip(shape, CombineMode.Intersect);
            g.DrawImage(image, box);
            if (flash > 0)
                using (var white = new SolidBrush(Color.FromArgb((int)(flash * 255), 255, 255, 255)))
                    g.FillRectangle(white, box);
            g.Restore(state);
            using var pen = new Pen(Color.FromArgb(200, 255, 255, 255), Math.Max(1, 2.2f * s * depth));
            g.DrawPath(pen, shape);
        }
        g.Flush(FlushIntention.Sync);

        var dst = new POINT { X = left, Y = top };
        var size = new SIZE { cx = ww, cy = wh };
        var src = new POINT();
        var blend = new BLENDFUNCTION { SourceConstantAlpha = (byte)Math.Clamp((int)(alpha * 255), 0, 255), AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, surface.Dc, ref src, 0, ref blend, ULW_ALPHA);
    }

    void Finish()
    {
        done = true;
        tick.Stop();
        try { arrived(); }
        catch (Exception ex) { Log.Error(ex); }
        Close(); // 非模态窗口关掉时自己会 Dispose
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tick.Dispose();
            surface.Dispose();
            image.Dispose();
        }
        base.Dispose(disposing);
    }
}
