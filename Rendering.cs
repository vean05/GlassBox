using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;
using static GlassBox.Native;

namespace GlassBox;

internal sealed class Theme
{
    public bool Dark { get; init; }
    public Color Body { get; init; }
    public Color Title { get; init; }
    public Color Divider { get; init; }
    public Color Text { get; init; }
    public Color SubText { get; init; }
    public Color TextShadow { get; init; }
    public Color Hover { get; init; }
    public Color Selected { get; init; }
    public Color BorderTop { get; init; }
    public Color BorderBottom { get; init; }
    public Color Scroll { get; init; }
    public Color Accent { get; init; }

    static readonly Theme DarkTheme = new()
    {
        Dark = true,
        Body = Color.FromArgb(64, 16, 18, 26),
        Title = Color.FromArgb(30, 255, 255, 255),
        Divider = Color.FromArgb(28, 255, 255, 255),
        Text = Color.FromArgb(240, 255, 255, 255),
        SubText = Color.FromArgb(150, 255, 255, 255),
        TextShadow = Color.FromArgb(120, 0, 0, 0),
        Hover = Color.FromArgb(30, 255, 255, 255),
        Selected = Color.FromArgb(56, 255, 255, 255),
        BorderTop = Color.FromArgb(96, 255, 255, 255),
        BorderBottom = Color.FromArgb(18, 255, 255, 255),
        Scroll = Color.FromArgb(110, 255, 255, 255),
        Accent = Color.FromArgb(255, 120, 170, 255),
    };

    static readonly Theme LightTheme = new()
    {
        Dark = false,
        Body = Color.FromArgb(70, 255, 255, 255),
        Title = Color.FromArgb(70, 255, 255, 255),
        Divider = Color.FromArgb(30, 0, 0, 0),
        Text = Color.FromArgb(235, 18, 18, 22),
        SubText = Color.FromArgb(140, 18, 18, 22),
        TextShadow = Color.Transparent,
        Hover = Color.FromArgb(26, 0, 0, 0),
        Selected = Color.FromArgb(44, 0, 0, 0),
        BorderTop = Color.FromArgb(200, 255, 255, 255),
        BorderBottom = Color.FromArgb(70, 255, 255, 255),
        Scroll = Color.FromArgb(100, 0, 0, 0),
        Accent = Color.FromArgb(255, 0, 103, 192),
    };

    public static Theme Detect()
    {
        bool dark = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) dark = v == 0;
        }
        catch { }
        return dark ? DarkTheme : LightTheme;
    }
}

/// <summary>
/// 所有盒子共用的一块离屏画布（32 位预乘 alpha 的 DIB）。
/// GDI+ 画进去再整块 BitBlt 到窗口，alpha 通道原样交给 DWM，毛玻璃才能透出来。
/// </summary>
internal sealed class Surface : IDisposable
{
    IntPtr dc, dib, oldBitmap;
    Bitmap? bitmap;
    Graphics? graphics;
    int width, height;

    public Graphics Begin(int w, int h)
    {
        if (graphics == null || w > width || h > height) Allocate(Math.Max(w, width), Math.Max(h, height));
        var g = graphics!;
        g.ResetTransform();
        g.ResetClip();
        g.SetClip(new Rectangle(0, 0, w, h));
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent)) g.FillRectangle(clear, 0, 0, w, h);
        g.CompositingMode = CompositingMode.SourceOver;
        return g;
    }

    /// <summary>提前把画布扩到至少这么大，免得真正要画的那一帧才去分配。</summary>
    public void Reserve(int w, int h)
    {
        if (graphics == null || w > width || h > height) Allocate(Math.Max(w, width), Math.Max(h, height));
    }

    public void Blit(IntPtr target, int w, int h) => BitBlt(target, 0, 0, w, h, dc, 0, 0, SRCCOPY);

    /// <summary>画布的 DC，给 UpdateLayeredWindow 用。</summary>
    public IntPtr Dc => dc;

    /// <summary>画布像素（预乘 BGRA），直接写之前先 Flush 掉 GDI+ 还没画完的东西。</summary>
    public IntPtr Bits { get; private set; }
    public int Stride => width * 4;

    /// <summary>暂时用不到大画布时把内存还回去，下次 Begin 会重新分配。</summary>
    public void Trim() => Free();

    void Allocate(int w, int h)
    {
        Free();
        var info = new BITMAPINFO { Header = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 } };
        dc = CreateCompatibleDC(IntPtr.Zero);
        dib = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        oldBitmap = SelectObject(dc, dib);
        bitmap = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
        Bits = bits;
        graphics = Graphics.FromImage(bitmap);
        width = w;
        height = h;
    }

    void Free()
    {
        graphics?.Dispose();
        bitmap?.Dispose();
        if (dc != IntPtr.Zero)
        {
            SelectObject(dc, oldBitmap);
            DeleteObject(dib);
            DeleteDC(dc);
        }
        graphics = null;
        bitmap = null;
        dc = dib = oldBitmap = IntPtr.Zero;
        Bits = IntPtr.Zero;
        width = height = 0;
    }

    public void Dispose() => Free();
}

internal static class Shapes
{
    public static GraphicsPath RoundRect(RectangleF r, float radius, bool roundTop = true, bool roundBottom = true)
    {
        var path = new GraphicsPath();
        float d = Math.Max(1, radius * 2);
        if (roundTop)
        {
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        }
        else path.AddLine(r.X, r.Y, r.Right, r.Y);

        if (roundBottom)
        {
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        }
        else path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);

        path.CloseFigure();
        return path;
    }

    public static Rectangle Fit(Size source, Rectangle box)
    {
        if (source.Width <= 0 || source.Height <= 0) return box;
        float scale = Math.Min((float)box.Width / source.Width, (float)box.Height / source.Height);
        int w = (int)(source.Width * scale), h = (int)(source.Height * scale);
        return new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
    }
}

internal static class AppIcon
{
    /// <summary>托盘图标：一块渐变玻璃，上面四个小格子。</summary>
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(1, 1, 30, 30);
            using (var path = Shapes.RoundRect(rect, 8))
            using (var brush = new LinearGradientBrush(rect, Color.FromArgb(255, 96, 165, 250), Color.FromArgb(255, 167, 139, 250), 45f))
                g.FillPath(brush, path);
            using var cell = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            foreach (var (x, y) in new[] { (7f, 7f), (17f, 7f), (7f, 17f), (17f, 17f) })
                using (var p = Shapes.RoundRect(new RectangleF(x, y, 8, 8), 2.5f))
                    g.FillPath(cell, p);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>
/// 模糊流动的封面背景：封面缩到很小（顺便调鲜艳一点）再放大，就是一张很柔的模糊图；压暗一点让白字看得清。
/// 做得比要盖的地方大一圈，画的时候慢慢平移（每帧只是贴一张图，不重新模糊）。封面换了、大小变了才重做。
/// </summary>
internal sealed class Backdrop
{
    Bitmap? image, source;

    /// <param name="area">现在要盖住的地方（卡片展开的动画里会变）</param>
    /// <param name="full">卡片最后的大小：按它做图，动画途中不用一直重做</param>
    /// <param name="drift">只在放歌时走的时钟（秒）</param>
    public void Draw(Graphics g, RectangleF area, SizeF full, Bitmap cover, float drift, float alpha)
    {
        var want = new Size((int)(Math.Max(full.Width, area.Width) * 1.3f) + 2, (int)(Math.Max(full.Height, area.Height) * 1.5f) + 2);
        if (image == null || source != cover || want.Width > image.Width || want.Height > image.Height || want.Width < image.Width * .8f)
            Build(cover, want);
        if (image == null) return;
        // 慢慢漂：横竖两个方向各按自己的节奏来回，一圈半分钟左右
        float ox = (image.Width - area.Width) * (.5f + .5f * MathF.Sin(drift * .21f));
        float oy = (image.Height - area.Height) * (.5f + .5f * MathF.Sin(drift * .16f + 1.3f));
        var dest = new Rectangle((int)Math.Round(area.X - Math.Max(0, ox)), (int)Math.Round(area.Y - Math.Max(0, oy)), image.Width, image.Height);
        var mode = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor; // 原样贴上去，不缩放
        if (alpha >= .99f) g.DrawImage(image, dest, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
        else
        {
            using var fade = new ImageAttributes();
            fade.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(alpha, 0, 1) });
            g.DrawImage(image, dest, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, fade);
        }
        g.InterpolationMode = mode;
    }

    void Build(Bitmap cover, Size want)
    {
        Clear();
        try
        {
            // 按要盖的比例从封面中间裁一块
            float aspect = want.Width / (float)want.Height;
            float sw = cover.Width, sh = cover.Width / aspect;
            if (sh > cover.Height) { sh = cover.Height; sw = sh * aspect; }
            var crop = new RectangleF((cover.Width - sw) / 2, (cover.Height - sh) / 2, sw, sh);
            int tw = 40, th = Math.Max(10, (int)(tw / aspect));
            using var edges = new ImageAttributes();
            edges.SetWrapMode(WrapMode.TileFlipXY); // 边上不要淡成透明
            using var tiny = new Bitmap(tw, th, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(tiny))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using var vivid = new ImageAttributes();
                vivid.SetColorMatrix(Saturation(1.35f));
                vivid.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(cover, new Rectangle(0, 0, tw, th), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, vivid);
            }
            using var mid = new Bitmap(tw * 4, th * 4, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(mid))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(tiny, new Rectangle(0, 0, mid.Width, mid.Height), 0, 0, tw, th, GraphicsUnit.Pixel, edges);
            }
            image = new Bitmap(want.Width, want.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(image))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(mid, new Rectangle(0, 0, want.Width, want.Height), 0, 0, mid.Width, mid.Height, GraphicsUnit.Pixel, edges);
                // 压暗：整体一层，越往下越深（底下一排小字最需要看清）
                using (var dim = new SolidBrush(Color.FromArgb(92, 0, 0, 0))) g.FillRectangle(dim, 0, 0, want.Width, want.Height);
                using var fall = new LinearGradientBrush(new Rectangle(0, 0, want.Width, want.Height), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(70, 0, 0, 0), LinearGradientMode.Vertical);
                g.FillRectangle(fall, 0, 0, want.Width, want.Height);
            }
            source = cover;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            Clear(); // 封面刚好被换掉、释放了：下一帧再做
        }
    }

    static ColorMatrix Saturation(float k)
    {
        const float r = .3086f, g = .6094f, b = .082f;
        float sr = (1 - k) * r, sg = (1 - k) * g, sb = (1 - k) * b;
        return new ColorMatrix(new[]
        {
            new[] { sr + k, sr, sr, 0, 0 },
            new[] { sg, sg + k, sg, 0, 0 },
            new[] { sb, sb, sb + k, 0, 0 },
            new[] { 0f, 0, 0, 1, 0 },
            new[] { 0f, 0, 0, 0, 1 },
        });
    }

    public void Clear()
    {
        image?.Dispose();
        image = null;
        source = null;
    }
}
