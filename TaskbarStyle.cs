using System.Runtime.InteropServices;
using System.Security.Cryptography;
using static GlassBox.Native;

namespace GlassBox;

/// <summary>Windows 任务栏的窗口（主屏一个，其他屏幕各一个）。</summary>
internal static class SystemTaskbar
{
    public static List<IntPtr> Windows()
    {
        var list = new List<IntPtr>();
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) list.Add(main);
        IntPtr after = IntPtr.Zero;
        while ((after = FindWindowEx(IntPtr.Zero, after, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) list.Add(after);
        return list;
    }

    /// <summary>
    /// 以前的版本有“隐藏任务栏”（配合底部 Dock，已删掉）：要是那时被强制结束、任务栏还藏着，
    /// 磁盘上会留着这个记号，启动时把任务栏还回来。
    /// </summary>
    public static void RecoverIfNeeded()
    {
        string marker = Path.Combine(ConfigStore.Dir, "taskbar-hidden");
        if (!File.Exists(marker)) return;
        foreach (var w in Windows()) ShowWindow(w, SW_SHOWNA);
        try { File.Delete(marker); }
        catch { }
    }
}

/// <summary>
/// 任务栏透明 / 磨砂（跟 TranslucentTB 一个效果，但做在玻璃收纳里）。
/// Win11 的任务栏背景是 XAML 画的，从外面改不动：用系统自带的 XAML 诊断接口把 GlassTap.dll 载进 explorer，
/// 由它把背景那两层矩形调成透明。“磨砂”再给任务栏窗口加上系统的亚克力模糊（背景透明后才看得见）。
/// 不改文件、不改注册表；关掉开关、玻璃收纳退出或崩溃都会还原（DLL 自己盯着玻璃收纳的进程），
/// explorer 重启后 DLL 就没了，要重新载入。
/// </summary>
internal static class TaskbarStyle
{
    public const int Normal = 0, Clear = 1, Frost = 2;
    const int WM_TAP_MODE = 0x8001, WM_TAP_COUNT = 0x8002; // 跟 GlassTap.cpp 里的 WM_APP+1 / +2 对上
    const int E_NOTFOUND = unchecked((int)0x80070490);
    const int ACCENT_DISABLED = 0, ACCENT_ENABLE_TRANSPARENTGRADIENT = 2, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4, WCA_ACCENT_POLICY = 19;
    static readonly Guid TapClsid = new("5C2D7E61-3B1A-4E7D-9C2F-6A1B8E0D4F31");
    static readonly IntPtr HWND_MESSAGE = new(-3);
    static readonly object Gate = new();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    delegate int InitializeXamlDiagnosticsEx(string endPoint, uint pid, string? diagnosticsDll, string tapDll, Guid tapClsid, string? initData);

    static int Build => Environment.OSVersion.Version.Build;

    /// <summary>只在测过的版本上动手：22H2 / 23H2 用的是同一套任务栏。别的版本结构可能不同，什么都不做。</summary>
    public static bool Supported => Build is 22621 or 22631;

    /// <summary>可能要等 explorer 载入 DLL，放在后台线程调。成功返回 null，否则返回给用户看的原因。</summary>
    public static string? Apply(int style)
    {
        bool dark = SystemDark();
        lock (Gate)
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return L.T("找不到任务栏", "Taskbar not found.");
            GetWindowThreadProcessId(tray, out uint explorer);
            var tap = FindTap(explorer);
            if (tap == IntPtr.Zero)
            {
                if (style == Normal) return null; // 从没载入过：本来就是原样
                if (!Supported) return L.T($"这个 Windows 版本（{Build}）还没适配，为了安全不动任务栏。", $"This Windows version ({Build}) isn't supported yet, so the taskbar is left alone for safety.");
                if (Inject(explorer) is { } error) return error;
                for (int i = 0; i < 40 && (tap = FindTap(explorer)) == IntPtr.Zero; i++) Thread.Sleep(100);
                if (tap == IntPtr.Zero) return L.T("任务栏没有响应，换个时间再试试。", "The taskbar didn't respond. Try again later.");
            }
            if (style != Normal)
            {
                // 刚载入时还在一层层找背景；找不到说明这个版本的任务栏长得不一样
                int layers = 0;
                for (int i = 0; i < 30 && (layers = LayerCount(tap)) == 0; i++) Thread.Sleep(100);
                if (layers == 0) return L.T("没找到任务栏背景（这个版本的任务栏结构不一样），已保持原样。", "Couldn't find the taskbar background (this taskbar is built differently), left as is.");
            }
            PostMessage(tap, WM_TAP_MODE, style == Normal ? IntPtr.Zero : 1, style == Normal ? IntPtr.Zero : Environment.ProcessId);
            // 背景层拿掉以后，任务栏窗口自己的底板在托盘那块和顶上 2 像素是纯黑的：
            // 透明模式给它一个 0% 不透明的“透明渐变”，磨砂模式铺亚克力，都把底板盖掉
            var (state, color) = style switch
            {
                Clear => (ACCENT_ENABLE_TRANSPARENTGRADIENT, 0x00000000u),
                Frost => (ACCENT_ENABLE_ACRYLICBLURBEHIND, dark ? 0x40201C1Au : 0x38F3F3F3u),
                _ => (ACCENT_DISABLED, 0u),
            };
            foreach (var w in SystemTaskbar.Windows()) SetAccent(w, state, color);
            return null;
        }
    }

    /// <summary>
    /// 不排队、不等待地还原（退出、崩溃、启动时收拾上次留下的）：GlassTap 回到原样，底板去掉效果。
    /// 不拿锁，崩溃处理里调也不会卡住。
    /// </summary>
    public static void Revert()
    {
        try
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;
            GetWindowThreadProcessId(tray, out uint explorer);
            var tap = FindTap(explorer);
            if (tap == IntPtr.Zero) return; // 没载入过，什么都没改
            PostMessage(tap, WM_TAP_MODE, IntPtr.Zero, IntPtr.Zero);
            foreach (var w in SystemTaskbar.Windows()) SetAccent(w, ACCENT_DISABLED, 0);
        }
        catch { }
    }

    /// <summary>任务栏跟的是“Windows 模式”（不是“应用模式”）的深浅色。</summary>
    static bool SystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not 1;
        }
        catch { return true; }
    }

    static IntPtr FindTap(uint explorer)
    {
        IntPtr after = IntPtr.Zero;
        while ((after = FindWindowEx(HWND_MESSAGE, after, "GlassBox.Tap", null)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(after, out uint pid);
            if (pid == explorer) return after;
        }
        return IntPtr.Zero;
    }

    static int LayerCount(IntPtr tap) =>
        SendMessageTimeout(tap, WM_TAP_COUNT, IntPtr.Zero, IntPtr.Zero, 2 /* SMTO_ABORTIFHUNG */, 500, out var count) == IntPtr.Zero ? 0 : (int)count;

    /// <summary>
    /// 载入这一步交给一个用完就退的 GlassBox.exe --tap 进程：XAML 诊断接口一用，调用方进程里就会留下
    /// 一套 XAML 组件和一个一直醒着的连接线程（实测多 12MB、每秒十几毫秒 CPU），主进程不能沾。
    /// </summary>
    static string? Inject(uint explorer)
    {
        string dll;
        try { dll = StageDll(); }
        catch (Exception ex)
        {
            Log.Error(ex);
            return L.T("找不到 GlassTap.dll（应该在玻璃收纳的文件夹里）。", "GlassTap.dll not found (it should be in the GlassBox folder).");
        }
        try
        {
            using var helper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                ArgumentList = { "--tap", explorer.ToString(), dll },
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            if (!helper.WaitForExit(15000))
            {
                helper.Kill();
                return L.T("载入超时。", "Loading timed out.");
            }
            int hr = helper.ExitCode;
            return hr == 0 ? null : L.T($"载入失败（0x{hr:X8}）。", $"Loading failed (0x{hr:X8}).");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return L.T("载入失败：", "Loading failed: ") + ex.Message;
        }
    }

    /// <summary>GlassBox.exe --tap &lt;explorer 进程号&gt; &lt;dll&gt;：真正调用 XAML 诊断接口的地方，返回值当退出码。</summary>
    public static int InjectHere(uint explorer, string dll)
    {
        var module = LoadLibrary("Windows.UI.Xaml.dll");
        var entry = module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, "InitializeXamlDiagnosticsEx");
        if (entry == IntPtr.Zero) return unchecked((int)0x8007007F); // ERROR_PROC_NOT_FOUND
        var init = Marshal.GetDelegateForFunctionPointer<InitializeXamlDiagnosticsEx>(entry);
        int hr = E_NOTFOUND;
        // 连接名被别的诊断工具占着会返回“找不到”，换个名字再试
        for (int i = 1; i <= 10 && hr == E_NOTFOUND; i++)
            hr = init($"VisualDiagConnection{i}", explorer, null, dll, TapClsid, null);
        return hr;
    }

    /// <summary>
    /// 把 DLL 复制到 %LOCALAPPDATA%\GlassBox\tap 再载入：explorer 会一直占着它，
    /// 直接用程序文件夹里的那份的话，以后更新玻璃收纳就覆盖不了。文件名带内容哈希，新旧版本互不影响。
    /// </summary>
    static string StageDll()
    {
        string source = Path.Combine(AppContext.BaseDirectory, "GlassTap.dll");
        byte[] bytes = File.ReadAllBytes(source);
        string hash = Convert.ToHexString(SHA256.HashData(bytes), 0, 6).ToLowerInvariant();
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBox", "tap");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, $"GlassTap-{hash}.dll");
        // 已经有一份的话核对整份内容：这个文件夹谁都能写，被换掉的 DLL 会被 explorer 载入，宁可不载
        if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
        else if (!SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
        {
            File.WriteAllBytes(target, bytes); // 被占着写不进去会抛异常，上面就报“载入失败”，不会用到坏的那份
            if (!SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new IOException("GlassTap.dll 对不上");
        }
        foreach (var old in Directory.GetFiles(dir, "GlassTap-*.dll"))
            if (!old.Equals(target, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old); } catch { } // 还被旧的 explorer 占着就下次再删
        return target;
    }

    static void SetAccent(IntPtr hwnd, int state, uint color)
    {
        var policy = new ACCENT_POLICY { AccentState = state, AccentFlags = 2, GradientColor = color };
        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
