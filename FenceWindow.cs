using System.Drawing.Drawing2D;
using System.Drawing.Text;
using static GlassBox.Native;

namespace GlassBox;

/// <summary>一个毛玻璃收纳盒。贴在桌面层（归 Progman 所有），不抢焦点，不进任务栏和 Alt+Tab。</summary>
internal sealed class FenceWindow : Form
{
    static readonly StringFormat TitleFormat = new(StringFormatFlags.NoWrap)
    {
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
    };
    static readonly StringFormat LabelFormat = new(StringFormatFlags.LineLimit)
    {
        Alignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
    };
    static readonly StringFormat CenterFormat = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    readonly GlassBoxApp app;
    readonly ToolTip tip = new() { ShowAlways = true, InitialDelay = 700, ReshowDelay = 300 };
    // 动画：每个图标当前画在哪（内容坐标，不含滚动），每帧向目标位置靠近；全部到位后计时器就停
    readonly Dictionary<string, PointF> positions = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Windows.Forms.Timer animation = new() { Interval = 15 };
    // 悬停展开：折叠的盒子鼠标停一下就临时展开（peeking），移开后自动收回；Config.Collapsed 保持不变
    readonly System.Windows.Forms.Timer hoverTimer = new();
    bool peeking, hoverOpen;
    Font titleFont = null!, labelFont = null!, smallFont = null!;
    int hover = -1, selected = -1, pressed = -1, dropIndex = -1, scrollY, scrollTarget, heightTarget = -1;
    bool hoverChevron, moving, mouseInside;
    Point pressPoint, moveCursor, moveOrigin, mousePoint;

    public FenceConfig Config { get; }
    public List<DesktopItem> Items { get; } = new();

    public FenceWindow(GlassBoxApp app, FenceConfig config)
    {
        this.app = app;
        Config = config;
        FormBorderStyle = FormBorderStyle.None;
        // 不设 ShowInTaskbar = false：那样 WinForms 会另建一个隐藏窗口当主人，盒子就挂不到桌面上了。
        // 被桌面拥有的工具窗口本来就不会出现在任务栏。
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        AllowDrop = true;
        Text = config.Name;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        CreateFonts();
        MinimumSize = new Size(Px(150), TitleH);
        Bounds = new Rectangle(config.X, config.Y, config.W, config.Collapsed ? TitleH : config.H);
        animation.Tick += (_, _) => Animate();
        hoverTimer.Tick += (_, _) => OnHoverTimer();
    }

    /// <summary>现在是不是只显示标题栏（折叠着且没有被悬停临时展开）。</summary>
    bool Folded => Config.Collapsed && !peeking;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_CLIPCHILDREN; // 带框架 DWM 才给毛玻璃和阴影；框架本身在 WM_NCCALCSIZE 里去掉
            cp.ExStyle = (cp.ExStyle & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            cp.Parent = DesktopShell.Host; // 归桌面所有：Win+D 显示桌面时盒子还在
            return cp;
        }
    }

    // ---------- 尺寸（随 DPI 缩放） ----------

    int Px(float v) => (int)MathF.Round(v * DeviceDpi / 96f);
    float Pxf(float v) => v * DeviceDpi / 96f;
    int TitleH => Px(34);
    int Pad => Px(8);
    int CellW => Px(80);
    int CellH => Px(86);
    int IconPx => Px(40);
    Rectangle ChevronRect => new(ClientSize.Width - TitleH, 0, TitleH, TitleH);
    Rectangle ContentRect => new(Pad, TitleH + Px(4), Math.Max(0, ClientSize.Width - Pad * 2), Math.Max(0, ClientSize.Height - TitleH - Px(4) - Pad / 2));
    int Columns => Math.Max(1, ContentRect.Width / CellW);
    int GridLeft => ContentRect.X + (ContentRect.Width - Columns * CellW) / 2;
    int ContentHeight => (Items.Count + Columns - 1) / Columns * CellH;
    int MaxScroll => Math.Max(0, ContentHeight - ContentRect.Height);

    Rectangle CellRect(int i) => new(GridLeft + i % Columns * CellW, ContentRect.Y + i / Columns * CellH - scrollY, CellW, CellH);

    int HitItem(Point p)
    {
        if (Folded || !ContentRect.Contains(p) || p.X < GridLeft) return -1;
        int col = (p.X - GridLeft) / CellW;
        if (col >= Columns) return -1;
        int i = (p.Y - ContentRect.Y + scrollY) / CellH * Columns + col;
        if (i < 0 || i >= Items.Count) return -1;
        var cell = CellRect(i);
        cell.Inflate(-Px(2), -Px(2));
        return cell.Contains(p) ? i : -1;
    }

    /// <summary>放下的位置，按“拿掉正在拖的那个之后”的序号算。</summary>
    int InsertIndex(Point p)
    {
        int count = Items.Count - (DraggedIndex >= 0 ? 1 : 0);
        if (Folded || p.Y < ContentRect.Y) return count;
        int col = Math.Clamp((p.X - GridLeft) / CellW, 0, Columns - 1);
        int row = Math.Max(0, (p.Y - ContentRect.Y + scrollY) / CellH);
        return Math.Clamp(row * Columns + col, 0, count);
    }

    int DraggedIndex => app.DraggingPath == null ? -1 : Items.FindIndex(i => i.Path.Equals(app.DraggingPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>图标该排在第几格：正在拖的那个让出位置，放下的位置空出一格。</summary>
    int SlotOf(int i, int dragged)
    {
        int slot = dragged >= 0 && i > dragged ? i - 1 : i;
        return dropIndex >= 0 && slot >= dropIndex ? slot + 1 : slot;
    }

    PointF SlotLocation(int slot) => new(GridLeft + slot % Columns * CellW, ContentRect.Y + slot / Columns * CellH);

    // ---------- 对外 ----------

    public void RefreshItems()
    {
        Items.Clear();
        foreach (var path in Config.Items)
            if (app.TryGetItem(path, out var item)) Items.Add(item);
        hover = selected = pressed = -1;
        scrollY = Math.Clamp(scrollY, 0, MaxScroll);
        scrollTarget = Math.Clamp(scrollTarget, 0, MaxScroll);
        var alive = Items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in positions.Keys.Where(k => !alive.Contains(k)).ToList()) positions.Remove(gone);
        Text = Config.Name;
        Invalidate();
    }

    public void AttachToDesktop()
    {
        var host = DesktopShell.Host;
        if (host != IntPtr.Zero) SetWindowLongPtr(Handle, GWLP_HWNDPARENT, host);
    }

    public void SendToBottom() => SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public void ApplyTheme()
    {
        Glass.Apply(Handle, app.Theme, app.Config.GlassMode);
        Invalidate();
    }

    public void ToggleCollapse()
    {
        if (peeking)
        {
            // 悬停展开时点箭头 = 固定展开
            peeking = false;
            Config.Collapsed = false;
            heightTarget = Math.Max(Config.H, TitleH * 2);
            app.ScheduleSave();
            Kick();
            return;
        }
        Config.Collapsed = !Config.Collapsed;
        heightTarget = Config.Collapsed ? TitleH : Math.Max(Config.H, TitleH * 2);
        app.ScheduleSave();
        Kick();
    }

    void Peek(bool open)
    {
        if (!Config.Collapsed || peeking == open) return;
        peeking = open;
        if (open)
        {
            // 下方空间不够就展开到屏幕底为止，多出来的靠滚动
            var area = Screen.FromHandle(Handle).WorkingArea;
            heightTarget = Math.Clamp(Config.H, TitleH * 2, Math.Max(TitleH * 2, area.Bottom - Top));
        }
        else
        {
            heightTarget = TitleH;
            hover = -1;
        }
        Kick();
    }

    void ScheduleHover(bool open, int delay)
    {
        hoverTimer.Stop();
        hoverOpen = open;
        if (open && peeking) return;
        hoverTimer.Interval = delay;
        hoverTimer.Start();
    }

    void OnHoverTimer()
    {
        hoverTimer.Stop();
        bool inside = Bounds.Contains(Cursor.Position);
        if (hoverOpen && inside) Peek(true);
        else if (!hoverOpen && !inside && dropIndex < 0 && !moving) Peek(false);
    }

    void Kick()
    {
        if (!animation.Enabled) animation.Start();
        Invalidate();
    }

    /// <summary>每帧把位置、滚动、高度往目标挪 35%，都到位了就停。</summary>
    void Animate()
    {
        const float k = 0.35f;
        bool moving = false;

        int dragged = DraggedIndex;
        for (int i = 0; i < Items.Count; i++)
        {
            if (i == dragged) continue;
            var target = SlotLocation(SlotOf(i, dragged));
            string key = Items[i].Path;
            if (!positions.TryGetValue(key, out var p)) continue;
            float dx = target.X - p.X, dy = target.Y - p.Y;
            if (Math.Abs(dx) < 0.5f && Math.Abs(dy) < 0.5f) positions[key] = target;
            else
            {
                positions[key] = new PointF(p.X + dx * k, p.Y + dy * k);
                moving = true;
            }
        }

        if (scrollY != scrollTarget)
        {
            int step = (int)((scrollTarget - scrollY) * k);
            scrollY = step == 0 ? scrollTarget : scrollY + step;
            moving = true;
        }

        if (heightTarget >= 0)
        {
            int step = (int)((heightTarget - Height) * k);
            if (step == 0)
            {
                Height = heightTarget;
                heightTarget = -1;
            }
            else
            {
                Height += step;
                moving = true;
            }
        }

        if (!moving) animation.Stop();
        Invalidate();
    }

    void SaveBounds()
    {
        Config.X = Left;
        Config.Y = Top;
        Config.W = Width;
        if (!Config.Collapsed) Config.H = Height;
        app.ScheduleSave();
    }

    // ---------- 窗口消息 ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Glass.Apply(Handle, app.Theme, app.Config.GlassMode);
        AttachToDesktop();
        SendMessage(Handle, WM_NCACTIVATE, 1, IntPtr.Zero);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        // WinForms 每次显示窗口时都会按自己的 Owner 属性重设主人，所以显示之后再挂一次
        if (Visible) AttachToDesktop();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
                m.Result = IntPtr.Zero; // 整个窗口都是客户区
                return;
            case WM_NCHITTEST:
                long lp = (long)m.LParam;
                m.Result = HitTestBorder(PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF))));
                return;
            case WM_NCACTIVATE:
                m.WParam = 1; // 一直保持“激活”外观，否则 DWM 会把不活动窗口的亚克力换成纯色
                break;
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;
            case WM_WINDOWPOSCHANGING:
                unsafe
                {
                    var pos = (WINDOWPOS*)m.LParam;
                    if ((pos->flags & SWP_NOZORDER) == 0) pos->hwndInsertAfter = HWND_BOTTOM;
                }
                break;
            case WM_SYSCOMMAND:
                int command = (int)m.WParam & 0xFFF0;
                if (command is SC_MINIMIZE or SC_MAXIMIZE) return;
                break;
            case WM_NCLBUTTONDBLCLK:
                return; // 双击边框不要触发系统的纵向最大化
            case WM_EXITSIZEMOVE:
                base.WndProc(ref m);
                SaveBounds();
                return;
            case WM_GETMINMAXINFO:
                base.WndProc(ref m);
                unsafe
                {
                    // 系统给带边框窗口的最小高度比标题栏高，折叠后会多出一截，这里改成标题栏高度
                    var info = (MINMAXINFO*)m.LParam;
                    info->ptMinTrackSize = new POINT { X = Px(150), Y = TitleH };
                }
                return;
        }
        base.WndProc(ref m);
    }

    int HitTestBorder(Point p)
    {
        int b = Px(6), w = ClientSize.Width, h = ClientSize.Height;
        bool left = p.X < b, right = p.X >= w - b, top = p.Y < b, bottom = p.Y >= h - b;
        if (Config.Collapsed) return left ? HTLEFT : right ? HTRIGHT : HTCLIENT;
        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return HTCLIENT;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        CreateFonts();
        MinimumSize = new Size(Px(150), TitleH);
        if (Config.Collapsed) Height = TitleH;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        scrollY = Math.Clamp(scrollY, 0, MaxScroll);
        scrollTarget = Math.Clamp(scrollTarget, 0, MaxScroll);
    }

    void CreateFonts()
    {
        titleFont?.Dispose();
        labelFont?.Dispose();
        smallFont?.Dispose();
        titleFont = new Font("Microsoft YaHei UI", Px(13), FontStyle.Bold, GraphicsUnit.Pixel);
        labelFont = new Font("Microsoft YaHei UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
        smallFont = new Font("Microsoft YaHei UI", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animation.Dispose();
            hoverTimer.Dispose();
            tip.Dispose();
            titleFont.Dispose();
            labelFont.Dispose();
            smallFont.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- 绘制 ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;
        var g = app.Surface.Begin(w, h);
        Render(g, w, h);
        var hdc = e.Graphics.GetHdc();
        try { app.Surface.Blit(hdc, w, h); }
        finally { e.Graphics.ReleaseHdc(hdc); }
    }

    void Render(Graphics g, int w, int h)
    {
        var t = app.Theme;
        float radius = Pxf(8);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using (var body = Shapes.RoundRect(new RectangleF(0, 0, w, h), radius))
        using (var brush = new SolidBrush(app.Tone.FenceBody(t))) // 光感玻璃：底色带一点壁纸的颜色
            g.FillPath(brush, body);

        using (var title = Shapes.RoundRect(new RectangleF(0, 0, w, TitleH), radius, roundBottom: Folded))
        using (var brush = new SolidBrush(t.Title))
            g.FillPath(brush, title);

        if (!Folded)
            using (var pen = new Pen(t.Divider))
                g.DrawLine(pen, Px(10), TitleH - 0.5f, w - Px(10), TitleH - 0.5f);

        using (var outline = Shapes.RoundRect(new RectangleF(0.5f, 0.5f, w - 1, h - 1), radius))
        using (var gradient = new LinearGradientBrush(new Rectangle(0, 0, w, h + 1), t.BorderTop, t.BorderBottom, LinearGradientMode.Vertical))
        using (var pen = new Pen(gradient))
            g.DrawPath(pen, outline);
        if (mouseInside) DrawSheen(g, w, h, radius);

        var chevron = ChevronRect;
        string count = Items.Count.ToString();
        float countW = g.MeasureString(count, smallFont).Width;
        DrawText(g, Config.Name, titleFont, t.Text, new RectangleF(Px(12), 0, chevron.Left - Px(12) - countW - Px(6), TitleH), TitleFormat);
        using (var sub = new SolidBrush(t.SubText))
            g.DrawString(count, smallFont, sub, new RectangleF(chevron.Left - countW - Px(2), 0, countW + Px(2), TitleH), TitleFormat);
        DrawChevron(g, chevron, t);

        if (Folded) return;

        var content = ContentRect;
        if (Items.Count == 0)
        {
            using var sub = new SolidBrush(t.SubText);
            g.DrawString(dropIndex >= 0 ? "松开放入这个盒子" : "拖放文件到这里", labelFont, sub, content, CenterFormat);
            return;
        }

        var state = g.Save();
        g.SetClip(content, CombineMode.Intersect);
        int dragged = DraggedIndex;
        bool settling = false;
        for (int i = 0; i < Items.Count; i++)
        {
            if (i == dragged) continue; // 正在拖的图标由系统的拖动影子来画
            var target = SlotLocation(SlotOf(i, dragged));
            if (!positions.TryGetValue(Items[i].Path, out var p)) positions[Items[i].Path] = p = target;
            if (p != target) settling = true;
            var cell = new RectangleF(p.X, p.Y - scrollY, CellW, CellH);
            if (cell.IntersectsWith(content)) DrawItem(g, i, cell, t);
        }
        g.Restore(state);
        if (settling && !animation.Enabled) animation.Start();

        if (MaxScroll > 0 && mouseInside)
        {
            float track = content.Height;
            float thumb = Math.Max(Px(24), track * content.Height / ContentHeight);
            float y = content.Y + (track - thumb) * scrollY / MaxScroll;
            using var path = Shapes.RoundRect(new RectangleF(w - Px(6), y, Pxf(3), thumb), Pxf(1.5f));
            using var brush = new SolidBrush(t.Scroll);
            g.FillPath(brush, path);
        }
    }

    /// <summary>鼠标附近的边框亮起来，像光打在玻璃边上。</summary>
    void DrawSheen(Graphics g, int w, int h, float radius)
    {
        float r = Pxf(150);
        using var light = new GraphicsPath();
        light.AddEllipse(mousePoint.X - r, mousePoint.Y - r, r * 2, r * 2);
        using var brush = new PathGradientBrush(light)
        {
            CenterPoint = mousePoint,
            CenterColor = Color.FromArgb(app.Theme.Dark ? 170 : 230, 255, 255, 255),
            SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) },
        };
        using var outline = Shapes.RoundRect(new RectangleF(0.5f, 0.5f, w - 1, h - 1), radius);
        using var pen = new Pen(brush, Pxf(1.4f));
        g.DrawPath(pen, outline);
    }

    void DrawItem(Graphics g, int i, RectangleF cell, Theme t)
    {
        var item = Items[i];
        cell.Inflate(-Px(2), -Px(2));
        if (i == selected || i == hover)
        {
            using var path = Shapes.RoundRect(cell, Pxf(6));
            using var brush = new SolidBrush(i == selected ? t.Selected : t.Hover);
            g.FillPath(brush, path);
        }
        DrawIconAndLabel(g, item, cell, t.Text, app.Theme.TextShadow);
    }

    void DrawIconAndLabel(Graphics g, DesktopItem item, RectangleF cell, Color text, Color shadow)
    {
        var iconBox = new Rectangle((int)(cell.X + (cell.Width - IconPx) / 2), (int)cell.Y + Px(5), IconPx, IconPx);
        var icon = app.Icons.Get(item.Path);
        if (icon != null) g.DrawImage(icon, Shapes.Fit(icon.Size, iconBox));

        var label = new RectangleF(cell.X + Px(2), iconBox.Bottom + Px(4), cell.Width - Px(4), labelFont.GetHeight(g) * 2 + 1);
        if (shadow.A > 0)
            using (var brush = new SolidBrush(shadow))
                g.DrawString(item.Name, labelFont, brush, new RectangleF(label.X, label.Y + 1, label.Width, label.Height), LabelFormat);
        using (var brush = new SolidBrush(text))
            g.DrawString(item.Name, labelFont, brush, label, LabelFormat);
    }

    void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect, StringFormat format)
    {
        if (app.Theme.TextShadow.A > 0)
        {
            using var shadow = new SolidBrush(app.Theme.TextShadow);
            g.DrawString(text, font, shadow, new RectangleF(rect.X, rect.Y + 1, rect.Width, rect.Height), format);
        }
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, rect, format);
    }

    void DrawChevron(Graphics g, Rectangle r, Theme t)
    {
        if (hoverChevron)
        {
            var bg = r;
            bg.Inflate(-Px(7), -Px(7));
            using var path = Shapes.RoundRect(bg, Pxf(5));
            using var brush = new SolidBrush(t.Hover);
            g.FillPath(brush, path);
        }
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = Pxf(4);
        float dir = Config.Collapsed ? 1 : -1; // 折叠时箭头朝下（展开），展开时朝上（折叠）
        using var pen = new Pen(t.SubText, Math.Max(1.2f, Pxf(1.5f))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - s, cy - dir * s / 2), new PointF(cx, cy + dir * s / 2), new PointF(cx + s, cy - dir * s / 2) });
    }

    // ---------- 鼠标 ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (ChevronRect.Contains(e.Location))
        {
            ToggleCollapse();
            return;
        }
        if (e.Y < TitleH)
        {
            moving = true;
            moveCursor = Cursor.Position;
            moveOrigin = Location;
            Capture = true;
            return;
        }
        selected = pressed = HitItem(e.Location);
        pressPoint = e.Location;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (moving)
        {
            var c = Cursor.Position;
            Location = Snap(new Point(moveOrigin.X + c.X - moveCursor.X, moveOrigin.Y + c.Y - moveCursor.Y));
            return;
        }
        if (pressed >= 0 && e.Button == MouseButtons.Left)
        {
            var drag = SystemInformation.DragSize;
            if (Math.Abs(e.X - pressPoint.X) > drag.Width || Math.Abs(e.Y - pressPoint.Y) > drag.Height)
            {
                int i = pressed;
                pressed = -1;
                StartDrag(i);
                return;
            }
        }

        int h = HitItem(e.Location);
        bool hc = ChevronRect.Contains(e.Location);
        if (h != hover) tip.SetToolTip(this, h >= 0 ? Items[h].Name : "");
        hover = h;
        hoverChevron = hc;
        mouseInside = true;
        mousePoint = e.Location;
        Invalidate(); // 边框高光跟着鼠标走
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (moving)
        {
            moving = false;
            Capture = false;
            SaveBounds();
            return;
        }
        pressed = -1;
        if (e.Button != MouseButtons.Right) return;

        var screen = PointToScreen(e.Location);
        int i = HitItem(e.Location);
        if (i >= 0)
        {
            selected = i;
            Invalidate();
            app.ShowItemMenu(Config, Items[i], screen);
        }
        else app.ShowFenceMenu(this, screen);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || ChevronRect.Contains(e.Location)) return;
        if (e.Y < TitleH)
        {
            ToggleCollapse();
            return;
        }
        int i = HitItem(e.Location);
        if (i >= 0) app.Open(Items[i]);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int next = Math.Clamp(scrollTarget - e.Delta * CellH / 120, 0, MaxScroll);
        if (next == scrollTarget) return;
        scrollTarget = next;
        hover = -1;
        Kick();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (app.Config.HoverExpand && Config.Collapsed) ScheduleHover(true, 250);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        hoverChevron = mouseInside = false;
        if (Config.Collapsed) ScheduleHover(false, 450);
        Invalidate();
    }

    /// <summary>拖动时吸附到屏幕边缘和其它盒子的边上，方便排整齐。</summary>
    Point Snap(Point p)
    {
        int reach = Px(12), gap = Px(12), w = Width, h = Height;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var xs = new List<int> { area.Left + gap, area.Right - gap - w };
        var ys = new List<int> { area.Top + gap, area.Bottom - gap - h };
        foreach (var other in app.Fences)
        {
            if (other == this || !other.Visible) continue;
            xs.AddRange(new[] { other.Left, other.Right - w, other.Right + gap, other.Left - gap - w });
            ys.AddRange(new[] { other.Top, other.Bottom - h, other.Bottom + gap, other.Top - gap - h });
        }
        int Nearest(int v, List<int> targets)
        {
            foreach (int t in targets)
                if (Math.Abs(v - t) <= reach) return t;
            return v;
        }
        return new Point(Nearest(p.X, xs), Nearest(p.Y, ys));
    }

    // ---------- 拖放 ----------

    void StartDrag(int i)
    {
        var item = Items[i];
        var cell = CellRect(i);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { item.Path });
        using var image = CreateDragImage(item);
        var grab = new Point(Math.Clamp(pressPoint.X - cell.X, 0, image.Width - 1), Math.Clamp(pressPoint.Y - cell.Y, 0, image.Height - 1));

        app.DraggingPath = item.Path;
        hover = selected = -1;
        Kick(); // 原来的位置立刻合拢
        try { DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, image, grab, false); }
        finally
        {
            app.DraggingPath = null;
            dropIndex = -1;
            app.InvalidateAll(); // 取消拖动的话图标会滑回原位
        }
    }

    /// <summary>拖动时跟着鼠标走的影子：一块半透明玻璃片，上面是图标和名字。</summary>
    Bitmap CreateDragImage(DesktopItem item)
    {
        var bitmap = new Bitmap(CellW, CellH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var rect = new RectangleF(1, 1, CellW - 2, CellH - 2);
        using (var path = Shapes.RoundRect(rect, Pxf(8)))
        {
            using (var fill = new SolidBrush(Color.FromArgb(150, 28, 30, 38))) g.FillPath(fill, path);
            using (var pen = new Pen(Color.FromArgb(110, 255, 255, 255))) g.DrawPath(pen, path);
        }
        DrawIconAndLabel(g, item, rect, Color.White, Color.Transparent);
        return bitmap;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        hoverTimer.Stop();
        Peek(true); // 拖到折叠的盒子上就展开，方便放到指定位置
        UpdateDrop(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        UpdateDrop(e);
    }

    void UpdateDrop(DragEventArgs e)
    {
        // 盒子之间拖动只是换位置；从资源管理器拖进来只是“收纳一个引用”，不移动文件
        if (app.DraggingPath != null)
        {
            e.Effect = DragDropEffects.Move;
            e.DropImageType = DropImageType.Move;
            e.Message = "移到 %1";
        }
        else if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Link;
            e.DropImageType = DropImageType.Link;
            e.Message = "收纳到 %1";
        }
        else
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        e.MessageReplacementToken = Config.Name;
        int index = InsertIndex(PointToClient(new Point(e.X, e.Y)));
        if (index != dropIndex)
        {
            dropIndex = index;
            Kick(); // 其它图标滑开让位
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        dropIndex = -1;
        if (Config.Collapsed) ScheduleHover(false, 450);
        Kick();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        var at = PointToClient(new Point(e.X, e.Y));
        int index = InsertIndex(at);
        dropIndex = -1;
        if (app.DraggingPath is { } path)
        {
            app.MoveItem(path, Config, index);
            positions[path] = new PointF(at.X - CellW / 2f, at.Y + scrollY - CellH / 2f); // 从松手的地方滑进空位
        }
        else if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) app.AddPaths(files, Config, index);
        if (Config.Collapsed) ScheduleHover(false, 900);
        Kick();
    }
}
