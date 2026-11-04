using System.Runtime.InteropServices;
using System.Text;

namespace GlassBox;

/// <summary>
/// YouTube 广告的“跳过”按钮：用 Windows 的辅助功能接口（UI Automation，读屏软件用的那个）看 Edge/Chrome 里的
/// YouTube 播放器有没有在放广告、“跳过”按钮出来了没有；要跳的时候“按”那个按钮（跟读屏软件按按钮一样），
/// 不动鼠标、不按键盘、不把浏览器切到前面。按的就是 YouTube 自己的跳过按钮，不屏蔽、不改网页。
///
/// 后台一个线程，只在设置打开、浏览器在放东西、有 YouTube 窗口时一秒看一次（先只读播放器的一个属性，
/// 在放广告才去找按钮）。浏览器窗口最小化时网页内容读不到，只能等它露出来（被别的窗口挡着没关系）。
/// </summary>
internal sealed unsafe class AdSkipper : IDisposable
{
    static readonly Guid CLSID_CUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    static readonly Guid IID_IUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
    // 虚表位置（前 3 个是 IUnknown 的）
    const int AutoElementFromHandle = 6, AutoCreatePropertyCondition = 23, AutoCreatePropertyConditionEx = 24, AutoCreateAndCondition = 25, AutoCreateOrCondition = 28;
    const int ElementFindFirst = 5, ElementGetCurrentPattern = 16, ElementGetClassName = 30;
    const int InvokeInvoke = 3;
    const int NameProperty = 30005, ControlTypeProperty = 30003, AutomationIdProperty = 30011, ClassNameProperty = 30012;
    const int ButtonControlType = 50000, InvokePattern = 10000, TreeScopeDescendants = 4;
    const int IgnoreCase = 1, MatchSubstring = 2;
    const ushort VT_I4 = 3, VT_BSTR = 8;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct VARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;
        [FieldOffset(8)] public int i4;
    }

    readonly Thread thread;
    readonly AutoResetEvent wake = new(false);
    readonly Dictionary<IntPtr, IntPtr> players = new(); // 浏览器窗口 → 它的 YouTube 播放器（UIA 元素）
    readonly Dictionary<IntPtr, long> missedAt = new();  // 找不到播放器的窗口：过一会儿再找
    volatile bool active, skipRequested, disposed;
    IntPtr automation, playerCondition, skipCondition;
    bool showing;

    /// <summary>“跳过”按钮出来了 / 没了（在后台线程上调）。</summary>
    public event Action<bool>? Changed;
    /// <summary>帮你按了一下“跳过”（在后台线程上调）。</summary>
    public event Action? Skipped;

    /// <summary>按钮一出来就自己按掉。</summary>
    public volatile bool AutoSkip;

    public AdSkipper()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "GlassBox ads", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>该不该看：设置开着、浏览器在放东西。关掉时什么都不做（线程睡着）。</summary>
    public void SetActive(bool on)
    {
        if (on == active) return;
        active = on;
        wake.Set();
    }

    /// <summary>用户点了“跳过广告”。</summary>
    public void Skip()
    {
        skipRequested = true;
        wake.Set();
    }

    void Loop()
    {
        while (!disposed)
        {
            wake.WaitOne(active ? 1000 : Timeout.Infinite);
            if (disposed) break;
            try
            {
                bool found = active && Probe(skipRequested || AutoSkip);
                skipRequested = false;
                if (found != showing)
                {
                    showing = found;
                    Changed?.Invoke(found);
                }
                if (!active) ReleaseAll(); // 不用了：把浏览器那边的东西都还掉
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                ReleaseAll();
            }
        }
        ReleaseAll();
        Release(ref skipCondition);
        Release(ref playerCondition);
        Release(ref automation);
    }

    /// <summary>看一圈 YouTube 窗口：有能跳的广告返回 true；click 就顺手按掉。</summary>
    bool Probe(bool click)
    {
        if (!EnsureAutomation()) return false;
        var windows = YouTubeWindows();
        foreach (var gone in players.Keys.Where(w => !windows.Contains(w)).ToList())
        {
            var p = players[gone];
            Release(ref p);
            players.Remove(gone);
        }
        foreach (var window in windows)
        {
            IntPtr player = Player(window);
            if (player == IntPtr.Zero) continue;
            IntPtr cls = IntPtr.Zero;
            if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(player, ElementGetClassName))(player, &cls) < 0)
            {
                Release(ref player); // 页面换了，元素失效：下次重新找
                players.Remove(window);
                continue;
            }
            string classes = cls == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(cls);
            if (cls != IntPtr.Zero) Marshal.FreeBSTR(cls);
            if (!classes.Contains("ad-showing")) continue; // 没在放广告：不用再往下找
            IntPtr button = IntPtr.Zero;
            if (((delegate* unmanaged<IntPtr, int, IntPtr, IntPtr*, int>)Slot(player, ElementFindFirst))(player, TreeScopeDescendants, skipCondition, &button) < 0 || button == IntPtr.Zero)
                continue;
            try
            {
                if (!click) return true;
                IntPtr invoke = IntPtr.Zero;
                if (((delegate* unmanaged<IntPtr, int, IntPtr*, int>)Slot(button, ElementGetCurrentPattern))(button, InvokePattern, &invoke) >= 0 && invoke != IntPtr.Zero)
                {
                    try
                    {
                        if (((delegate* unmanaged<IntPtr, int>)Slot(invoke, InvokeInvoke))(invoke) >= 0)
                        {
                            Skipped?.Invoke();
                            return false;
                        }
                    }
                    finally { Release(ref invoke); }
                }
                return true;
            }
            finally { Release(ref button); }
        }
        return false;
    }

    /// <summary>这个窗口里的 YouTube 播放器（id 是 movie_player 的那块）；找过没找到的 10 秒内不再找。</summary>
    IntPtr Player(IntPtr window)
    {
        if (players.TryGetValue(window, out var player)) return player;
        long now = Environment.TickCount64;
        if (missedAt.TryGetValue(window, out long when) && now - when < 10000) return IntPtr.Zero;
        IntPtr root = IntPtr.Zero;
        if (((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Slot(automation, AutoElementFromHandle))(automation, window, &root) < 0 || root == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            if (((delegate* unmanaged<IntPtr, int, IntPtr, IntPtr*, int>)Slot(root, ElementFindFirst))(root, TreeScopeDescendants, playerCondition, &player) >= 0 && player != IntPtr.Zero)
            {
                players[window] = player;
                missedAt.Remove(window);
                return player;
            }
        }
        finally { Release(ref root); }
        missedAt[window] = now;
        return IntPtr.Zero;
    }

    bool EnsureAutomation()
    {
        if (automation != IntPtr.Zero) return true;
        var clsid = CLSID_CUIAutomation;
        var iid = IID_IUIAutomation;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out automation) < 0 || automation == IntPtr.Zero)
        {
            automation = IntPtr.Zero;
            return false;
        }
        playerCondition = StringCondition(AutomationIdProperty, "movie_player", 0);
        // 跳过按钮：class 里带 skip-ad-button / ad-skip-button（YouTube 改过几次名字），或者名字里有“跳过/Skip/略過”的按钮
        IntPtr byClass = Or(StringCondition(ClassNameProperty, "skip-ad-button", MatchSubstring), StringCondition(ClassNameProperty, "ad-skip-button", MatchSubstring));
        IntPtr byName = Or(Or(StringCondition(NameProperty, "skip", MatchSubstring | IgnoreCase), StringCondition(NameProperty, "跳过", MatchSubstring)), StringCondition(NameProperty, "略過", MatchSubstring));
        IntPtr button = IntPtr.Zero;
        var type = new VARIANT { vt = VT_I4, i4 = ButtonControlType };
        ((delegate* unmanaged<IntPtr, int, VARIANT, IntPtr*, int>)Slot(automation, AutoCreatePropertyCondition))(automation, ControlTypeProperty, type, &button);
        skipCondition = Or(byClass, And(button, byName));
        return playerCondition != IntPtr.Zero && skipCondition != IntPtr.Zero;
    }

    IntPtr StringCondition(int property, string value, int flags)
    {
        var v = new VARIANT { vt = VT_BSTR, ptr = Marshal.StringToBSTR(value) };
        IntPtr result = IntPtr.Zero;
        try
        {
            if (flags == 0) ((delegate* unmanaged<IntPtr, int, VARIANT, IntPtr*, int>)Slot(automation, AutoCreatePropertyCondition))(automation, property, v, &result);
            else ((delegate* unmanaged<IntPtr, int, VARIANT, int, IntPtr*, int>)Slot(automation, AutoCreatePropertyConditionEx))(automation, property, v, flags, &result);
        }
        finally { Marshal.FreeBSTR(v.ptr); }
        return result;
    }

    /// <summary>两个条件合成一个；用过的两个随手放掉（新条件自己留着引用）。</summary>
    IntPtr Or(IntPtr a, IntPtr b) => Combine(AutoCreateOrCondition, a, b);

    IntPtr And(IntPtr a, IntPtr b) => Combine(AutoCreateAndCondition, a, b);

    IntPtr Combine(int slot, IntPtr a, IntPtr b)
    {
        IntPtr result = IntPtr.Zero;
        if (a != IntPtr.Zero && b != IntPtr.Zero) ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Slot(automation, slot))(automation, a, b, &result);
        Release(ref a);
        Release(ref b);
        return result;
    }

    /// <summary>没最小化、标题里带 YouTube 的 Edge/Chrome 窗口（标题是当前标签页的）。</summary>
    static HashSet<IntPtr> YouTubeWindows()
    {
        var found = new HashSet<IntPtr>();
        var name = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            name.Clear();
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() != "Chrome_WidgetWin_1") return true;
            name.Clear();
            GetWindowText(hwnd, name, name.Capacity);
            if (name.ToString().Contains("YouTube", StringComparison.OrdinalIgnoreCase)) found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    void ReleaseAll()
    {
        foreach (var p in players.Values.ToList())
        {
            var ptr = p;
            Release(ref ptr);
        }
        players.Clear();
        missedAt.Clear();
        if (showing)
        {
            showing = false;
            Changed?.Invoke(false);
        }
    }

    static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    static void Release(ref IntPtr ptr)
    {
        if (ptr != IntPtr.Zero) Marshal.Release(ptr);
        ptr = IntPtr.Zero;
    }

    public void Dispose()
    {
        disposed = true;
        active = false;
        wake.Set();
    }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr obj);
}
