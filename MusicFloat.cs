using System.Drawing.Drawing2D;
using static GlassBox.Native;

namespace GlassBox;

/// <summary>
/// 从胶囊里拖出来的音乐卡片：一块浮在桌面上的小播放器（封面、歌名、歌词），放在哪里都行，
/// 拖回屏幕顶部正中就收回胶囊。里面画什么由胶囊决定（跟音乐卡片共用歌词、封面、颜色），这里只管窗口：
/// 置顶、不抢焦点、不进任务栏；在放歌时 30 帧，暂停时不动；全屏游戏、全屏视频时藏起来。
/// </summary>
internal sealed class MusicFloat : Form
{
    public const float CardW = 500, CardH = 136;
    const float ShadowRoom = 26;

    readonly CapsuleWindow owner;
    readonly Surface surface = new();
    readonly System.Windows.Forms.Timer tick = new() { Interval = 33 };
    readonly System.Windows.Forms.Timer check = new() { Interval = 500 };
    Point cardPos; // 卡片（不含阴影）左上角在屏幕上的位置
    bool dragging, hover, hiddenForFullscreen, live;
    long busyUntil; // 暂停时也要动一会儿（滚轮调音量浮出的数字）
    Point dragFrom, dragStart;

    public MusicFloat(CapsuleWindow owner, Point cardTopLeft)
    {
        this.owner = owner;
        cardPos = cardTopLeft;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        Text = L.T("玻璃收纳 · 音乐", "GlassBox · Music");
        Bounds = new Rectangle(cardTopLeft.X, cardTopLeft.Y, 1, 1);
        tick.Tick += (_, _) =>
        {
            Render();
            if (!live && Environment.TickCount64 > busyUntil) tick.Stop();
        };
        check.Tick += (_, _) => Check();
    }

    float S => DeviceDpi / 96f;
    public Rectangle CardOnScreen => new(cardPos.X, cardPos.Y, (int)(CardW * S), (int)(CardH * S));
    public bool Dragging => dragging;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = WS_POPUP;
            cp.ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Render();
        check.Start();
    }

    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    /// <summary>卡片左上角挪到这里（胶囊拖出来的那一下一直在调）。</summary>
    public void MoveCard(Point topLeft)
    {
        cardPos = topLeft;
        Render();
    }

    /// <summary>内容变了（换歌、歌词找到了、暂停了）：画一帧，在放歌就一直出帧。</summary>
    public void Repaint(bool live)
    {
        this.live = live;
        if (live && !hiddenForFullscreen) tick.Start();
        else if (Environment.TickCount64 > busyUntil) tick.Stop();
        Render();
    }

    /// <summary>接下来 ms 毫秒一直出帧（暂停时也是）。</summary>
    public void Animate(int ms)
    {
        busyUntil = Environment.TickCount64 + ms;
        if (!hiddenForFullscreen) tick.Start();
    }

    void Check()
    {
        // 全屏游戏、全屏看视频时让开
        bool busy = FullscreenBusy();
        if (busy != hiddenForFullscreen)
        {
            hiddenForFullscreen = busy;
            if (busy)
            {
                tick.Stop();
                Hide();
            }
            else if (owner.FloatShouldShow)
            {
                Show();
                Repaint(owner.FloatLive);
            }
        }
        bool over = CardOnScreen.Contains(Cursor.Position);
        if (over != hover)
        {
            hover = over;
            Render();
        }
    }

    public void Render()
    {
        if (!IsHandleCreated || !Visible) return;
        float s = S, m = ShadowRoom * s;
        int ww = (int)Math.Ceiling((CardW + ShadowRoom * 2) * s), wh = (int)Math.Ceiling((CardH + ShadowRoom * 2) * s);
        var g = surface.Begin(ww, wh);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        var card = new RectangleF(m, m, CardW * s, CardH * s);
        float radius = 22 * s;
        // 柔和的影子：往下偏一点的几圈半透明圆角
        for (int i = 6; i >= 1; i--)
        {
            var shadow = RectangleF.Inflate(card, i * 3 * s, i * 3 * s);
            shadow.Offset(0, 7 * s);
            using var path = Shapes.RoundRect(shadow, radius + i * 3 * s);
            using var brush = new SolidBrush(Color.FromArgb(11, 0, 0, 0));
            g.FillPath(brush, path);
        }
        var mouse = Cursor.Position;
        try { owner.PaintFloat(g, card, new Point(mouse.X - cardPos.X + (int)m, mouse.Y - cardPos.Y + (int)m), hover || dragging); }
        catch (Exception ex) { Log.Error(ex); }
        g.Flush(FlushIntention.Sync);

        var dst = new POINT { X = cardPos.X - (int)m, Y = cardPos.Y - (int)m };
        var size = new SIZE { cx = ww, cy = wh };
        var src = new POINT();
        var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, surface.Dc, ref src, 0, ref blend, ULW_ALPHA);
    }

    Point Local(MouseEventArgs e)
    {
        // 窗口左上角在屏幕上是 cardPos - 阴影；换算成画的时候用的坐标（卡片在 (阴影, 阴影)）
        var screen = Cursor.Position;
        int m = (int)(ShadowRoom * S);
        return new Point(screen.X - cardPos.X + m, screen.Y - cardPos.Y + m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (owner.FloatMouseDown(Local(e))) return;
        dragging = true;
        dragFrom = Cursor.Position;
        dragStart = cardPos;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging)
        {
            var now = Cursor.Position;
            MoveCard(new Point(dragStart.X + now.X - dragFrom.X, dragStart.Y + now.Y - dragFrom.Y));
            owner.FloatMoved(released: false);
            return;
        }
        if (!hover) hover = true;
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            owner.FloatMenu(Cursor.Position);
            return;
        }
        if (!dragging) return;
        dragging = false;
        Capture = false;
        owner.FloatMoved(released: true);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (dragging && !Capture)
        {
            dragging = false;
            owner.FloatMoved(released: true);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = false;
        Render();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        owner.FloatWheel(e.Delta);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tick.Dispose();
            check.Dispose();
            surface.Dispose();
        }
        base.Dispose(disposing);
    }
}
