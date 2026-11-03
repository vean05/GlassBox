using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static GlassBox.Native;

namespace GlassBox;

[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
}

/// <summary>图标在后台线程按需加载，加载完再通知界面重画，开机时不会卡住。</summary>
internal sealed class IconCache : IDisposable
{
    readonly ConcurrentDictionary<string, Bitmap?> loaded = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte> requested = new(StringComparer.OrdinalIgnoreCase);
    readonly BlockingCollection<string> queue = new();
    readonly Action onLoaded, onIdle;

    public int Size { get; }

    public IconCache(int size, Action onLoaded, Action onIdle)
    {
        Size = size;
        this.onLoaded = onLoaded;
        this.onIdle = onIdle;
        var worker = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "IconLoader" };
        worker.SetApartmentState(ApartmentState.STA); // 外壳接口要求 STA
        worker.Start();
    }

    public Bitmap? Get(string path)
    {
        if (loaded.TryGetValue(path, out var bitmap)) return bitmap;
        if (requested.TryAdd(path, 0)) queue.Add(path);
        return null;
    }

    public void Forget(string path)
    {
        requested.TryRemove(path, out _);
        if (loaded.TryRemove(path, out var bitmap)) bitmap?.Dispose();
    }

    void Run()
    {
        try
        {
            foreach (var path in queue.GetConsumingEnumerable())
            {
                loaded[path] = ShellIcons.Load(path, Size);
                onLoaded();
                if (queue.Count == 0)
                {
                    CoFreeUnusedLibraries(); // 卸掉加载图标时顺带载入的外壳扩展，省内存
                    onIdle();
                }
            }
        }
        catch (InvalidOperationException) { } // 退出时 CompleteAdding
    }

    public void Dispose() => queue.CompleteAdding();
}

internal static class ShellIcons
{
    const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_ICONONLY = 0x4, SIIGBF_THUMBNAILONLY = 0x8;
    static readonly Guid IID_IShellItemImageFactory = typeof(IShellItemImageFactory).GUID;
    static readonly HashSet<string> Thumbnailed = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic" };

    // 图片显示缩略图，其它只取图标（PDF 等生成缩略图又慢又占内存）
    public static Bitmap? Load(string path, int size) =>
        Load(path, size, Thumbnailed.Contains(Path.GetExtension(path)) ? SIIGBF_RESIZETOFIT : SIIGBF_ICONONLY);

    /// <summary>按比例缩小后的图片（系统用 WIC 解码，不用把整张大图读进内存）；没有缩略图就返回 null。</summary>
    public static Bitmap? Thumbnail(string path, int size) => Load(path, size, SIIGBF_THUMBNAILONLY);

    /// <summary>空格预览用的大图：有缩略图（图片、视频、PDF、Office 文档）就用缩略图，没有就是大图标。</summary>
    public static Bitmap? Preview(string path, int size) => Load(path, size, SIIGBF_RESIZETOFIT);

    static Bitmap? Load(string path, int size, int flags)
    {
        var iid = IID_IShellItemImageFactory;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
        var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ptr);
        Marshal.Release(ptr);
        try
        {
            if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out var hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            try { return ToBitmap(hbitmap); }
            finally { DeleteObject(hbitmap); }
        }
        catch (Exception) { return null; }
        finally { Marshal.ReleaseComObject(factory); }
    }

    static unsafe Bitmap? ToBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, sizeof(BITMAP), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var info = new BITMAPINFO { Header = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 } };
            IntPtr screen = GetDC(IntPtr.Zero);
            GetDIBits(screen, hbitmap, 0, (uint)h, data.Scan0, ref info, 0);
            ReleaseDC(IntPtr.Zero, screen);

            // 外壳给的位图有时没有 alpha、有时不是预乘的，统一成预乘 alpha
            byte* p = (byte*)data.Scan0;
            int n = w * h;
            bool anyAlpha = false, straight = false;
            for (int i = 0; i < n; i++)
            {
                byte a = p[i * 4 + 3];
                if (a != 0) anyAlpha = true;
                if (p[i * 4] > a || p[i * 4 + 1] > a || p[i * 4 + 2] > a) straight = true;
            }
            if (!anyAlpha)
            {
                for (int i = 0; i < n; i++) p[i * 4 + 3] = 255;
            }
            else if (straight)
            {
                for (int i = 0; i < n; i++)
                {
                    int a = p[i * 4 + 3];
                    p[i * 4] = (byte)(p[i * 4] * a / 255);
                    p[i * 4 + 1] = (byte)(p[i * 4 + 1] * a / 255);
                    p[i * 4 + 2] = (byte)(p[i * 4 + 2] * a / 255);
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
}
