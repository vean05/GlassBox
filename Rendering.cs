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
