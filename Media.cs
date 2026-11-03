using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GlassBox;

/// <summary>正在播放的东西（YouTube、Spotify、网易云……只要它把播放状态报给 Windows）。</summary>
/// <param name="Art">96×96 的方形封面（取色、小图用）。</param>
/// <param name="Cover">保持原比例的大封面（视频是 16:9），上下的黑边已经切掉。</param>
/// <param name="Sources">现在所有在报播放状态的程序（同时开着 YouTube 和 Spotify 时可以切换）。</param>
internal sealed record NowPlaying(string Title, string Artist, string Source, bool Playing, TimeSpan Position, TimeSpan Duration, DateTime UpdatedUtc,
    Bitmap? Art, Bitmap? Cover, string[] Sources)
{
    /// <summary>播放器只在暂停、拖进度时报一次位置，中间按时间往前推。</summary>
    public TimeSpan CurrentPosition
    {
        get
        {
            if (!Playing) return Position;
            var now = Position + (DateTime.UtcNow - UpdatedUtc);
            return Duration > TimeSpan.Zero && now > Duration ? Duration : now;
        }
    }
}

/// <summary>
/// 读 Windows 的“全局媒体控制”（就是按音量键时弹出来的那个播放卡片背后的接口）。
/// 直接按 WinRT 的虚表调用，不引用 Windows SDK 投影（那个 dll 有 25MB），后台线程每 1–2.5 秒看一次。
/// </summary>
internal sealed unsafe class MediaWatcher : IDisposable
{
    public enum Command { None, TogglePlay, Next, Previous, Pause, Seek }

    static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    static readonly Guid IID_IStream = new("0000000C-0000-0000-C000-000000000046");
    static readonly Guid IID_ManagerStatics = new("2050C4EE-11A0-57DE-AED7-C97C70338245");
    const string ManagerClass = "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";

    // 各接口的虚表位置（前 6 个是 IInspectable 的）
    const int StaticsRequestAsync = 6, ManagerGetCurrentSession = 6, ManagerGetSessions = 7;
    const int SessionSourceApp = 6, SessionMediaProperties = 7, SessionTimeline = 8, SessionPlaybackInfo = 9, SessionPause = 11,
        SessionSkipNext = 16, SessionSkipPrevious = 17, SessionTogglePlayPause = 20, SessionChangePosition = 24;
    const int VectorGetAt = 6, VectorSize = 7;
    const int PropsTitle = 6, PropsArtist = 9, PropsThumbnail = 15;
    const int InfoPlaybackStatus = 7, StatusPlaying = 4;
    const int TimelineStart = 6, TimelineEnd = 7, TimelinePosition = 10, TimelineUpdated = 11;
    const int AsyncInfoStatus = 7, AsyncInfoCancel = 9, AsyncOperationGetResults = 8;
    const int StreamReferenceOpenRead = 6, StreamRead = 3;

    readonly AutoResetEvent wake = new(false);
    volatile bool stopped;
    int pending;
    long seekTicks, sleepAtTicks;
    volatile string? preferred; // 用户在卡片上选的那个程序；没选就跟着 Windows 的“当前”走

    public NowPlaying? Current { get; private set; }
    public event Action<NowPlaying?>? Changed;

    public void Start()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "GlassBox media", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public void Send(Command command)
    {
        Interlocked.Exchange(ref pending, (int)command);
        wake.Set();
    }

    /// <summary>跳到某个位置（从头算）。</summary>
    public void Seek(TimeSpan position)
    {
        Interlocked.Exchange(ref seekTicks, Math.Max(0, position.Ticks));
        Send(Command.Seek);
    }

    /// <summary>换成看/控制这个程序在放的东西。</summary>
    public void Select(string source)
    {
        preferred = source;
        wake.Set();
    }

    /// <summary>定时暂停：到点暂停正在放的；null 取消。</summary>
    public DateTime? SleepAtUtc
    {
        get { long t = Interlocked.Read(ref sleepAtTicks); return t == 0 ? null : new DateTime(t, DateTimeKind.Utc); }
        set { Interlocked.Exchange(ref sleepAtTicks, value?.Ticks ?? 0); wake.Set(); }
    }

    void Run()
    {
        RoInitialize(1); // 多线程套间
        IntPtr manager = IntPtr.Zero;
        string artKey = "";
        (Bitmap? Art, Bitmap? Cover) art = default;
        while (!stopped)
        {
            int wait;
            try
            {
                if (manager == IntPtr.Zero) manager = RequestManager();
                if (manager == IntPtr.Zero) wait = 60000; // 系统不支持或服务没起来，过一会儿再试
                else
                {
                    var command = (Command)Interlocked.Exchange(ref pending, 0);
                    if (SleepAtUtc is { } sleep && DateTime.UtcNow >= sleep)
                    {
                        SleepAtUtc = null;
                        command = Command.Pause;
                    }
                    var next = Read(manager, command, Interlocked.Read(ref seekTicks), preferred, ref artKey, ref art);
                    if (!Same(next, Current))
                    {
                        Current = next;
                        Changed?.Invoke(next);
                    }
                    wait = next?.Playing == true ? 1000 : 2500;
                }
                if (SleepAtUtc is { } at) wait = (int)Math.Clamp((at - DateTime.UtcNow).TotalMilliseconds + 50, 50, wait);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                Release(ref manager);
                wait = 10000;
            }
            wake.WaitOne(wait);
        }
        Release(ref manager);
    }

    /// <summary>
    /// 有些播放器每秒都报一次新位置；按时间推算本来就该在那儿的话不算变化，免得界面每秒重排一次。
    /// </summary>
    static bool Same(NowPlaying? a, NowPlaying? b) =>
        a == null || b == null ? a == b
        : a.Title == b.Title && a.Artist == b.Artist && a.Source == b.Source && a.Playing == b.Playing && a.Art == b.Art
          && a.Duration == b.Duration && Math.Abs((a.CurrentPosition - b.CurrentPosition).TotalSeconds) < 1.5
          && a.Sources.AsSpan().SequenceEqual(b.Sources);

    static NowPlaying? Read(IntPtr manager, Command command, long seek, string? preferred, ref string artKey, ref (Bitmap? Art, Bitmap? Cover) art)
    {
        var sources = new List<string>();
        IntPtr session = PickSession(manager, preferred, sources);
        if (session == IntPtr.Zero) return null;
        try
        {
            if (command != Command.None)
            {
                IntPtr op;
                if (command == Command.Seek)
                {
                    // 位置按时间轴的起点算：起点通常是 0，有的播放器不是
                    long origin = 0;
                    IntPtr line = GetObject(session, SessionTimeline);
                    if (line != IntPtr.Zero) { origin = GetLong(line, TimelineStart); Release(ref line); }
                    IntPtr value = IntPtr.Zero;
                    op = ((delegate* unmanaged<IntPtr, long, IntPtr*, int>)Slot(session, SessionChangePosition))(session, origin + seek, &value) < 0 ? IntPtr.Zero : value;
                }
                else
                {
                    int slot = command switch
                    {
                        Command.TogglePlay => SessionTogglePlayPause,
                        Command.Next => SessionSkipNext,
                        Command.Pause => SessionPause,
                        _ => SessionSkipPrevious,
                    };
                    op = GetObject(session, slot);
                }
                Await(op);
                Release(ref op);
                Thread.Sleep(200); // 播放器切歌、改状态要一点时间
            }

            string source = GetString(session, SessionSourceApp);
            IntPtr info = GetObject(session, SessionPlaybackInfo);
            int status = info == IntPtr.Zero ? 0 : GetInt(info, InfoPlaybackStatus);
            Release(ref info);

            string title = "", artist = "";
            IntPtr props = AwaitObject(GetObject(session, SessionMediaProperties));
            if (props != IntPtr.Zero)
            {
                title = GetString(props, PropsTitle);
                artist = GetString(props, PropsArtist);
                string key = source + "\n" + title + "\n" + artist;
                if (key != artKey)
                {
                    artKey = key;
                    art = LoadArt(props); // 旧封面由界面那边换下来时释放
                }
                Release(ref props);
            }
            if (title.Length == 0) return null;

            long start = 0, end = 0, position = 0, updated = 0;
            IntPtr timeline = GetObject(session, SessionTimeline);
            if (timeline != IntPtr.Zero)
            {
                start = GetLong(timeline, TimelineStart);
                end = GetLong(timeline, TimelineEnd);
                position = GetLong(timeline, TimelinePosition);
                updated = GetLong(timeline, TimelineUpdated);
                Release(ref timeline);
            }
            var updatedUtc = updated > 0 ? DateTime.FromFileTimeUtc(updated) : DateTime.UtcNow;
            return new NowPlaying(title, artist, source, status == StatusPlaying,
                TimeSpan.FromTicks(Math.Max(0, position - start)), TimeSpan.FromTicks(Math.Max(0, end - start)), updatedUtc,
                art.Art, art.Cover, sources.ToArray());
        }
        finally { Release(ref session); }
    }

    /// <summary>
    /// 选要看的那个播放：用户选过的程序还在就用它，不然用 Windows 认为的“当前”。顺便记下所有在报状态的程序。
    /// </summary>
    static IntPtr PickSession(IntPtr manager, string? preferred, List<string> sources)
    {
        IntPtr picked = IntPtr.Zero;
        IntPtr list = GetObject(manager, ManagerGetSessions);
        if (list != IntPtr.Zero)
        {
            try
            {
                int count = GetInt(list, VectorSize);
                for (int i = 0; i < count && i < 16; i++)
                {
                    IntPtr one = IntPtr.Zero;
                    if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(list, VectorGetAt))(list, (uint)i, &one) < 0 || one == IntPtr.Zero) continue;
                    string id = GetString(one, SessionSourceApp);
                    if (id.Length > 0 && !sources.Contains(id)) sources.Add(id);
                    if (picked == IntPtr.Zero && preferred != null && id == preferred) picked = one;
                    else Release(ref one);
                }
            }
            finally { Release(ref list); }
        }
        return picked != IntPtr.Zero ? picked : GetObject(manager, ManagerGetCurrentSession);
    }

    static IntPtr RequestManager()
    {
        if (WindowsCreateString(ManagerClass, ManagerClass.Length, out var name) < 0) return IntPtr.Zero;
        try
        {
            var iid = IID_ManagerStatics;
            if (RoGetActivationFactory(name, ref iid, out var factory) < 0 || factory == IntPtr.Zero) return IntPtr.Zero;
            try { return AwaitObject(GetObject(factory, StaticsRequestAsync)); }
            finally { Release(ref factory); }
        }
        finally { WindowsDeleteString(name); }
    }

    /// <summary>
    /// 封面：一张保持原比例的大图（最大 360×360，YouTube 缩略图上下的黑边先切掉），
    /// 和一张 96×96 的方形小图（取中间一块）。
    /// </summary>
    static (Bitmap? Art, Bitmap? Cover) LoadArt(IntPtr props)
    {
        IntPtr reference = GetObject(props, PropsThumbnail);
        if (reference == IntPtr.Zero) return default;
        IntPtr stream = IntPtr.Zero, com = IntPtr.Zero;
        try
        {
            stream = AwaitObject(GetObject(reference, StreamReferenceOpenRead));
            if (stream == IntPtr.Zero) return default;
            var iid = IID_IStream;
            if (CreateStreamOverRandomAccessStream(stream, ref iid, out com) < 0 || com == IntPtr.Zero) return default;
            using var memory = new MemoryStream();
            var buffer = new byte[64 * 1024];
            var read = (delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)Slot(com, StreamRead);
            fixed (byte* p = buffer)
            {
                while (memory.Length < 8 << 20)
                {
                    uint got = 0;
                    if (read(com, p, (uint)buffer.Length, &got) < 0 || got == 0) break;
                    memory.Write(buffer, 0, (int)got);
                }
            }
            if (memory.Length == 0) return default;
            memory.Position = 0;
            using var full = new Bitmap(memory);
            var content = ContentBounds(full);
            float k = Math.Min(1, 360f / Math.Max(content.Width, content.Height));
            var cover = Scale(full, content, Math.Max(1, (int)(content.Width * k)), Math.Max(1, (int)(content.Height * k)));
            int side = Math.Min(content.Width, content.Height);
            var square = Scale(full, new Rectangle(content.X + (content.Width - side) / 2, content.Y + (content.Height - side) / 2, side, side), 96, 96);
            return (square, cover);
        }
        catch (Exception) { return default; }
        finally
        {
            Release(ref com);
            Release(ref stream);
            Release(ref reference);
        }
    }

    static Bitmap Scale(Bitmap source, Rectangle from, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var wrap = new ImageAttributes();
        wrap.SetWrapMode(WrapMode.TileFlipXY); // 边上不要被插值混进一圈透明
        g.DrawImage(source, new Rectangle(0, 0, width, height), from.X, from.Y, from.Width, from.Height, GraphicsUnit.Pixel, wrap);
        return result;
    }

    /// <summary>去掉四周几乎全黑的边（4:3 的视频缩略图里装的是 16:9 的画面，上下各一条黑边）。</summary>
    static Rectangle ContentBounds(Bitmap image)
    {
        int w = image.Width, h = image.Height;
        var data = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* scan = (byte*)data.Scan0;
            int stride = data.Stride;
            bool Dark(int x0, int y0, int dx, int dy, int n)
            {
                int step = Math.Max(1, n / 48);
                for (int i = 0; i < n; i += step)
                {
                    uint px = ((uint*)(scan + (y0 + dy * i) * stride))[x0 + dx * i];
                    if (Math.Max(px >> 16 & 255, Math.Max(px >> 8 & 255, px & 255)) > 24) return false;
                }
                return true;
            }
            int top = 0, bottom = h, left = 0, right = w;
            while (top < h / 3 && Dark(0, top, 1, 0, w)) top++;
            while (bottom > h - h / 3 && Dark(0, bottom - 1, 1, 0, w)) bottom--;
            while (left < w / 3 && Dark(left, top, 0, 1, bottom - top)) left++;
            while (right > w - w / 3 && Dark(right - 1, top, 0, 1, bottom - top)) right--;
            // 整张图本来就很暗（黑底的专辑封面）：不切
            if (bottom - top < h / 3 || right - left < w / 3) return new Rectangle(0, 0, w, h);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }
        finally { image.UnlockBits(data); }
    }

    // ---------- WinRT 虚表调用 ----------

    static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    static IntPtr GetObject(IntPtr obj, int slot)
    {
        IntPtr value = IntPtr.Zero;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, &value);
        return hr < 0 ? IntPtr.Zero : value;
    }

    static int GetInt(IntPtr obj, int slot)
    {
        int value = 0;
        return ((delegate* unmanaged<IntPtr, int*, int>)Slot(obj, slot))(obj, &value) < 0 ? 0 : value;
    }

    static long GetLong(IntPtr obj, int slot)
    {
        long value = 0;
        return ((delegate* unmanaged<IntPtr, long*, int>)Slot(obj, slot))(obj, &value) < 0 ? 0 : value;
    }

    static string GetString(IntPtr obj, int slot)
    {
        IntPtr hstring = IntPtr.Zero;
        if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, &hstring) < 0 || hstring == IntPtr.Zero) return "";
        try
        {
            uint length;
            char* chars = WindowsGetStringRawBuffer(hstring, &length);
            return chars == null ? "" : new string(chars, 0, (int)length);
        }
        finally { WindowsDeleteString(hstring); }
    }

    /// <summary>等异步操作结束（最多 3 秒）。成功返回 true。</summary>
    static bool Await(IntPtr op)
    {
        if (op == IntPtr.Zero) return false;
        var iid = IID_IAsyncInfo;
        if (Marshal.QueryInterface(op, ref iid, out var info) < 0) return false;
        try
        {
            long deadline = Environment.TickCount64 + 3000;
            while (true)
            {
                int status = GetInt(info, AsyncInfoStatus); // 0 进行中，1 完成，2 取消，3 出错
                if (status != 0) return status == 1;
                if (Environment.TickCount64 > deadline)
                {
                    ((delegate* unmanaged<IntPtr, int>)Slot(info, AsyncInfoCancel))(info);
                    return false;
                }
                Thread.Sleep(5);
            }
        }
        finally { Marshal.Release(info); }
    }

    /// <summary>等异步操作结束并取结果对象；操作本身用完就释放。</summary>
    static IntPtr AwaitObject(IntPtr op)
    {
        try { return Await(op) ? GetObject(op, AsyncOperationGetResults) : IntPtr.Zero; }
        finally { Release(ref op); }
    }

    static void Release(ref IntPtr ptr)
    {
        if (ptr != IntPtr.Zero) Marshal.Release(ptr);
        ptr = IntPtr.Zero;
    }

    public void Dispose()
    {
        stopped = true;
        wake.Set();
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] static extern char* WindowsGetStringRawBuffer(IntPtr hstring, uint* length);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")] static extern int RoInitialize(int type);
    [DllImport("shcore.dll")] static extern int CreateStreamOverRandomAccessStream(IntPtr randomAccessStream, ref Guid iid, out IntPtr stream);
}
