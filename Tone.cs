using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;
using static GlassBox.Native;

namespace GlassBox;

/// <summary>
/// 光感玻璃：玻璃的颜色从壁纸里取，再按一天里的时间调色温（清晨偏冷、傍晚偏暖、夜里压暗）。
/// 还留着一张缩小并模糊过的主屏壁纸，胶囊直接画在上面，看起来就是透过玻璃看到的壁纸。
/// 这张图只有约 320×200，占内存不到 300KB。
/// </summary>
internal sealed class GlassTone : IDisposable
{
    const int BackdropWidth = 320;

    // 一天里的色调关键帧：时刻、偏向的颜色、偏多少、玻璃额外压暗的颜色、名字
    static readonly (float Hour, Color Color, float Mix, Color Veil, string Name)[] Keys =
    {
        (0, Color.FromArgb(70, 85, 200), .38f, Color.FromArgb(107, 8, 12, 40), "深夜"),
        (5, Color.FromArgb(90, 110, 210), .32f, Color.FromArgb(82, 10, 16, 48), "凌晨"),
        (7, Color.FromArgb(160, 200, 255), .22f, Color.FromArgb(20, 140, 180, 255), "清晨"),
        (10, Color.FromArgb(255, 255, 255), 0f, Color.FromArgb(0, 0, 0, 0), "上午"),
        (14, Color.FromArgb(255, 250, 240), .04f, Color.FromArgb(0, 0, 0, 0), "午后"),
        (17.5f, Color.FromArgb(255, 150, 70), .30f, Color.FromArgb(30, 255, 130, 50), "傍晚"),
        (20, Color.FromArgb(110, 90, 200), .30f, Color.FromArgb(77, 14, 10, 40), "夜晚"),
        (24, Color.FromArgb(70, 85, 200), .38f, Color.FromArgb(107, 8, 12, 40), "深夜"),
    };

    readonly Control ui;
    readonly System.Windows.Forms.Timer clock = new() { Interval = 5 * 60 * 1000 };
    readonly ImageAttributes clampEdges = new();
    Bitmap? backdrop;
    Rectangle backdropScreen;
    Color wallColor = Color.FromArgb(255, 160, 80);
    float topLuminance = .3f;
    // 实际屏幕上胶囊背后的样子（Wallpaper Engine 之类的动态壁纸、桌面小部件都在里面），有就优先用
    Bitmap? live;
    Rectangle liveScreen;
    Color? liveColor;
    float? liveLuminance;
    long lastLive = -100000;
    string loadedKey = "";
    int generation;
    bool disposed;

    public event Action? Changed;

    /// <summary>玻璃的底色（已经按时间调过）。</summary>
    public Color Tint { get; private set; } = Color.FromArgb(200, 140, 90);
    /// <summary>夜里给玻璃再盖一层的颜色，白天是透明。</summary>
    public Color Veil { get; private set; }
    /// <summary>壁纸顶部很亮：玻璃上用深色字。</summary>
    public bool DarkInk { get; private set; }
    /// <summary>胶囊背后有多亮（0..1）。</summary>
    public float TopLuminance => liveLuminance ?? topLuminance;
    public string Period { get; private set; } = "";
    /// <summary>颜色或背景图每变一次加一，用的人据此判断要不要重建缓存。</summary>
    public int Version { get; private set; }
    Color BaseColor => liveColor ?? wallColor;

    public GlassTone(Control ui)
    {
        this.ui = ui;
        clampEdges.SetWrapMode(WrapMode.TileFlipXY); // 放大时边缘不发黑
        clock.Tick += (_, _) => Retone();
        clock.Start();
        Retone();
    }

    // ---------- 时间色调 ----------

    void Retone()
    {
        var now = DateTime.Now;
        float hour = now.Hour + now.Minute / 60f;
        int i = 0;
        while (i < Keys.Length - 2 && hour >= Keys[i + 1].Hour) i++;
        var (h0, c0, m0, v0, name) = Keys[i];
        var (h1, c1, m1, v1, _) = Keys[i + 1];
        float t = (hour - h0) / (h1 - h0);

        var toward = Lerp(c0, c1, t);
        float mix = m0 + (m1 - m0) * t;
        var tint = Lerp(BaseColor, toward, mix);
        var veil = Color.FromArgb((int)(v0.A + (v1.A - v0.A) * t), Lerp(v0, v1, t));
        bool darkInk = TopLuminance > .6f && hour > 6 && hour < 19;

        if (tint == Tint && veil == Veil && darkInk == DarkInk && name == Period) return;
        Version++;
        Tint = tint;
        Veil = veil;
        DarkInk = darkInk;
        Period = name;
        Changed?.Invoke();
    }

    static Color Lerp(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>盒子模式用：系统亚克力上那层颜色往壁纸色偏一点。</summary>
    public Color FenceBody(Theme theme) => Color.FromArgb(theme.Body.A + 8, Lerp(theme.Body, Tint, theme.Dark ? .32f : .22f));

    // ---------- 壁纸 ----------

    /// <summary>重新读壁纸（启动时、改了壁纸或分辨率后）。在后台线程解码，读好再换上。</summary>
    public void Reload()
    {
        var screen = Screen.PrimaryScreen!.Bounds;
        string path = WallpaperPath();
        string style = "10";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            style = key?.GetValue("WallpaperStyle") as string ?? "10";
        }
        catch { }
        string stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString() : "";
        string loadKey = $"{path}|{stamp}|{style}|{screen}|{GetSysColor(COLOR_DESKTOP)}";
        if (loadKey == loadedKey) return;
        loadedKey = loadKey;

        int gen = ++generation;
        var worker = new Thread(() =>
        {
            Bitmap? image = null;
            Color color = wallColor;
            float luminance = topLuminance;
            try { image = Build(path, style, screen, out color, out luminance); }
            catch (Exception ex) { Log.Error(ex); }
            try
            {
                ui.BeginInvoke(new Action(() =>
                {
                    if (disposed || gen != generation) { image?.Dispose(); return; }
                    backdrop?.Dispose();
                    backdrop = image;
                    backdropScreen = screen;
                    Version++;
                    wallColor = color;
                    topLuminance = luminance;
                    Tint = Color.Empty; // 强制通知一次
                    Retone();
                }));
            }
            catch (InvalidOperationException) { image?.Dispose(); } // 正在退出
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Wallpaper" };
        worker.SetApartmentState(ApartmentState.STA); // 缩略图接口要 STA
        worker.Start();
    }

    static string WallpaperPath()
    {
        var buffer = new char[520];
        if (!SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)buffer.Length, buffer, 0)) return "";
        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    static Bitmap Build(string path, string style, Rectangle screen, out Color color, out float topLuminance)
    {
        int w = BackdropWidth, h = Math.Max(1, (int)Math.Round(BackdropWidth * (double)screen.Height / screen.Width));
        int bg = GetSysColor(COLOR_DESKTOP);
        var result = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.Clear(Color.FromArgb(bg & 0xFF, (bg >> 8) & 0xFF, (bg >> 16) & 0xFF));
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (File.Exists(path))
            {
                // 优先要系统缩略图（省内存）；壁纸缓存文件没有扩展名拿不到缩略图，就直接解码
                Image? image = ShellIcons.Thumbnail(path, Math.Max(w, h) * 2);
                Stream? stream = null;
                try
                {
                    if (image == null)
                    {
                        stream = File.OpenRead(path);
                        image = Image.FromStream(stream, false, false);
                    }
                    g.DrawImage(image, Place(image.Size, new Size(w, h), style));
                }
                finally
                {
                    image?.Dispose();
                    stream?.Dispose();
                }
            }
        }
        BoxBlur(result, 3, 3);
        Analyze(result, out color, out topLuminance);
        return result;
    }

    /// <summary>按“契合度”设置算出壁纸画在屏幕上的位置（填充 / 适应 / 拉伸；平铺和居中按填充算）。</summary>
    static RectangleF Place(Size image, Size target, string style)
    {
        if (style == "2") return new RectangleF(0, 0, target.Width, target.Height);
        float sx = (float)target.Width / image.Width, sy = (float)target.Height / image.Height;
        float scale = style == "6" ? Math.Min(sx, sy) : Math.Max(sx, sy);
        float w = image.Width * scale, h = image.Height * scale;
        return new RectangleF((target.Width - w) / 2, (target.Height - h) / 2, w, h);
    }

    /// <summary>三次方框模糊 ≈ 高斯模糊。</summary>
    static unsafe void BoxBlur(Bitmap bitmap, int radius, int passes)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        var temp = new uint[w * h];
        try
        {
            uint* pixels = (uint*)data.Scan0;
            fixed (uint* t = temp)
            {
                for (int pass = 0; pass < passes; pass++)
                {
                    for (int y = 0; y < h; y++) Blur1D(pixels + y * w, t + y * w, w, 1, radius);
                    for (int x = 0; x < w; x++) Blur1D(t + x, pixels + x, h, w, radius);
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
    }

    static unsafe void Blur1D(uint* src, uint* dst, int n, int step, int r)
    {
        int div = r * 2 + 1, a = 0, red = 0, green = 0, blue = 0;
        for (int i = -r; i <= r; i++)
        {
            uint p = src[Math.Clamp(i, 0, n - 1) * step];
            a += (int)(p >> 24); red += (int)(p >> 16 & 255); green += (int)(p >> 8 & 255); blue += (int)(p & 255);
        }
        for (int i = 0; i < n; i++)
        {
            dst[i * step] = (uint)(a / div) << 24 | (uint)(red / div) << 16 | (uint)(green / div) << 8 | (uint)(blue / div);
            uint add = src[Math.Min(i + r + 1, n - 1) * step], remove = src[Math.Max(i - r, 0) * step];
            a += (int)(add >> 24) - (int)(remove >> 24);
            red += (int)(add >> 16 & 255) - (int)(remove >> 16 & 255);
            green += (int)(add >> 8 & 255) - (int)(remove >> 8 & 255);
            blue += (int)(add & 255) - (int)(remove & 255);
        }
    }

    /// <summary>取主色：越鲜艳、越亮的像素权重越大；顺便量一下屏幕顶部中间有多亮。</summary>
    static unsafe void Analyze(Bitmap bitmap, out Color color, out float topLuminance)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        double r = 0, g = 0, b = 0, weight = 0, top = 0;
        int topCount = 0;
        try
        {
            uint* p = (uint*)data.Scan0;
            for (int y = 0; y < h; y += 2)
                for (int x = 0; x < w; x += 2)
                {
                    uint px = p[y * w + x];
                    int R = (int)(px >> 16 & 255), G = (int)(px >> 8 & 255), B = (int)(px & 255);
                    int max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                    double sat = max == 0 ? 0 : (max - min) / (double)max;
                    double lum = (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255;
                    double wt = sat * sat * (0.15 + lum) + 0.002;
                    r += R * wt; g += G * wt; b += B * wt; weight += wt;
                    if (y < h * 0.14 && x > w * 0.28 && x < w * 0.72) { top += lum; topCount++; }
                }
        }
        finally { bitmap.UnlockBits(data); }

        r /= weight; g /= weight; b /= weight;
        double peak = Math.Max(r, Math.Max(g, b));
        double k = peak > 0 ? 235 / peak : 1; // 提到差不多的亮度，暗壁纸也能取出有颜色的玻璃
        color = Color.FromArgb((int)Math.Min(255, r * k), (int)Math.Min(255, g * k), (int)Math.Min(255, b * k));
        topLuminance = topCount > 0 ? (float)(top / topCount) : .3f;
    }

    /// <summary>
    /// 拍一下屏幕上 <paramref name="region"/> 这块（缩小到 1/5 再模糊），当玻璃背后的样子。
    /// 拍的时候让 <paramref name="self"/> 暂时不进截图，免得把胶囊自己拍进去；用户自己截图不受影响。
    /// </summary>
    public void Sample(Rectangle region, IntPtr self)
    {
        if (region.Width < 16 || region.Height < 16) return;
        if (!SetWindowDisplayAffinity(self, WDA_EXCLUDEFROMCAPTURE)) return; // 老系统不支持：继续用壁纸文件
        int w = Math.Max(8, region.Width / 5), h = Math.Max(8, region.Height / 5);
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        try
        {
            DwmFlush(); // 等下一帧合成，排除才生效
            IntPtr screen = GetDC(IntPtr.Zero);
            using (var g = Graphics.FromImage(bitmap))
            {
                IntPtr dc = g.GetHdc();
                SetStretchBltMode(dc, HALFTONE);
                SetBrushOrgEx(dc, 0, 0, IntPtr.Zero);
                StretchBlt(dc, 0, 0, w, h, screen, region.X, region.Y, region.Width, region.Height, SRCCOPY);
                g.ReleaseHdc(dc);
            }
            ReleaseDC(IntPtr.Zero, screen);
        }
        finally { SetWindowDisplayAffinity(self, WDA_NONE); }

        Opaque(bitmap);
        BoxBlur(bitmap, 3, 3);
        Analyze(bitmap, out var color, out var luminance);
        live?.Dispose();
        live = bitmap;
        liveScreen = region;
        Version++;
        // 动态壁纸一直在变，连着拍的时候颜色慢慢跟过去，不要一跳一跳的；
        // 隔了很久才拍到（一直被窗口挡着）就直接用新的，旧颜色已经不可信了
        bool recent = Environment.TickCount64 - lastLive < 6000;
        lastLive = Environment.TickCount64;
        liveColor = recent && liveColor is { } old ? Lerp(old, color, .5f) : color;
        liveLuminance = recent && liveLuminance is { } oldLum ? oldLum + (luminance - oldLum) * .5f : luminance;
        Retone();
    }

    static unsafe void Opaque(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            uint* p = (uint*)data.Scan0;
            for (int i = 0, n = bitmap.Width * bitmap.Height; i < n; i++) p[i] |= 0xFF000000; // GDI 写进来的 alpha 是 0
        }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>把屏幕上 <paramref name="screenRect"/> 这块的模糊背景画到 <paramref name="dest"/>。</summary>
    public bool DrawBackdrop(Graphics g, RectangleF dest, RectangleF screenRect)
    {
        var (image, area) = live != null ? (live, liveScreen) : (backdrop, backdropScreen);
        if (image == null) return false;
        float scale = (float)image.Width / area.Width;
        var src = new RectangleF((screenRect.X - area.X) * scale, (screenRect.Y - area.Y) * scale,
            screenRect.Width * scale, screenRect.Height * scale);
        var mode = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(image, new[] { dest.Location, new PointF(dest.Right, dest.Y), new PointF(dest.X, dest.Bottom) },
            src, GraphicsUnit.Pixel, clampEdges);
        g.InterpolationMode = mode;
        return true;
    }

    public void Dispose()
    {
        disposed = true;
        clock.Dispose();
        clampEdges.Dispose();
        backdrop?.Dispose();
        backdrop = null;
        live?.Dispose();
        live = null;
    }
}
