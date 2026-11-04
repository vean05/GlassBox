using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using static GlassBox.Native;

namespace GlassBox;

/// <summary>和 Windows 桌面（explorer）打交道：找桌面窗口、开关系统桌面图标。</summary>
internal static class DesktopShell
{
    const int ToggleDesktopIconsCommand = 0x7402; // 等同于桌面右键「查看 → 显示桌面图标」
    const uint SMTO_ABORTIFHUNG = 2;

    public static IntPtr Progman => FindWindow("Progman", null);

    public static IntPtr FindDefView() => Find().DefView;

    /// <summary>
    /// 装着桌面图标层的那个顶层窗口。盒子要归它所有才会浮在桌面上面——
    /// 很多机器上它是 Progman 上面一层全屏的 WorkerW，挂在 Progman 上会被整个盖住。
    /// </summary>
    public static IntPtr Host
    {
        get
        {
            var host = Find().Host;
            return host != IntPtr.Zero ? host : Progman;
        }
    }

    static (IntPtr Host, IntPtr DefView) Find()
    {
        var progman = Progman;
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero) return (progman, defView);

        IntPtr host = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            defView = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero) return true;
            host = hwnd;
            return false;
        }, IntPtr.Zero);
        return (host, defView);
    }

    public static bool? IconsVisible()
    {
        var defView = FindDefView();
        if (defView == IntPtr.Zero) return null;
        var list = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        return list == IntPtr.Zero ? null : IsWindowVisible(list);
    }

    /// <summary>
    /// 留个记号“图标是玻璃收纳藏的”：这个开关 Windows 会一直记着，玻璃收纳被强制结束、没正常退出时，
    /// 下次启动（或者卸载）看到记号就知道该还原。
    /// </summary>
    static string HiddenMarker => Path.Combine(ConfigStore.Dir, "icons-hidden");

    public static bool HidByUs => File.Exists(HiddenMarker);

    public static void SetIconsVisible(bool visible)
    {
        try
        {
            if (visible) File.Delete(HiddenMarker);
            else
            {
                Directory.CreateDirectory(ConfigStore.Dir);
                File.WriteAllText(HiddenMarker, "");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var current = IconsVisible();
        if (current == null || current == visible) return;
        // 资源管理器卡住时不要跟着一起卡死
        SendMessageTimeout(FindDefView(), WM_COMMAND, (IntPtr)ToggleDesktopIconsCommand, IntPtr.Zero, SMTO_ABORTIFHUNG, 2000, out _);
    }

    /// <summary>这个窗口是不是桌面的空白处（壁纸层）。系统图标可见时点在图标层上不算，免得误判点到了图标。</summary>
    public static bool IsDesktopBackground(IntPtr hwnd)
    {
        return ClassOf(hwnd) is "Progman" or "WorkerW" or "SHELLDLL_DefView";
    }
}

/// <summary>
/// 全局钩子放在自己的一条线程上：电脑上每一次按键、每一次点击都要先经过钩子，
/// 跟界面放在一起的话，胶囊在画动画、卡一下的那几十毫秒，全电脑的键盘鼠标都要排队等（打游戏会觉得卡），
/// 卡太久 Windows 还会把钩子悄悄拔掉。这条线程只收钩子的回调，平时一直睡着，不占 CPU。
/// </summary>
internal static class HookThread
{
    /// <summary>在新线程上装好钩子；返回的 Action 拆掉钩子并结束线程。</summary>
    public static Action Start(string name, Func<IntPtr> install)
    {
        uint id = 0;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            id = GetCurrentThreadId();
            var handle = install();
            if (handle == IntPtr.Zero) Log.Info($"{name} 装不上（错误 {Marshal.GetLastWin32Error()}）");
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } // 钩子回调就在这个等消息的过程中被调用
            if (handle != IntPtr.Zero) UnhookWindowsHookEx(handle);
        }) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
        thread.Start();
        ready.Wait();
        ready.Dispose();
        return () => PostThreadMessage(id, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}

/// <summary>全局低级鼠标钩子，只用来识别「双击桌面空白处」。</summary>
internal sealed class DesktopDoubleClickHook : IDisposable
{
    readonly LowLevelMouseProc proc;
    readonly Action stop;
    uint lastTime;
    POINT lastPoint;

    /// <summary>在钩子线程上触发，用的人要自己切回界面线程。</summary>
    public event Action? DoubleClick;

    public DesktopDoubleClickHook()
    {
        proc = OnMouse; // 必须持有委托引用，否则会被 GC 回收
        stop = HookThread.Start("GlassBox mouse hook", () => SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0));
    }

    IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam == WM_LBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (DesktopShell.IsDesktopBackground(WindowFromPoint(info.pt)))
            {
                bool near = Math.Abs(info.pt.X - lastPoint.X) <= GetSystemMetrics(SM_CXDOUBLECLK) / 2
                         && Math.Abs(info.pt.Y - lastPoint.Y) <= GetSystemMetrics(SM_CYDOUBLECLK) / 2;
                if (lastTime != 0 && info.time - lastTime <= GetDoubleClickTime() && near)
                {
                    lastTime = 0;
                    DoubleClick?.Invoke();
                }
                else
                {
                    lastTime = info.time;
                    lastPoint = info.pt;
                }
            }
            else lastTime = 0;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose() => stop();
}

/// <summary>
/// 全局低级键盘钩子，只认“连按两下 Ctrl”：每下都是短按（不到 0.3 秒），两下间隔不到 0.4 秒，
/// 中间没按别的键（Ctrl+C、Ctrl+V 这种组合键不会触发）。别的按键原样放过，不记录任何内容。
/// </summary>
internal sealed class DoubleCtrlHook : IDisposable
{
    const uint VK_CONTROL = 0x11, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    const uint TapMax = 300, GapMax = 400;
    readonly LowLevelKeyboardProc proc;
    readonly Action stop;
    bool down;
    uint downAt, lastTap;
    int taps;

    /// <summary>在钩子线程上触发，用的人要自己切回界面线程。</summary>
    public event Action? Triggered;

    public DoubleCtrlHook()
    {
        proc = OnKey; // 必须持有委托引用，否则会被 GC 回收
        stop = HookThread.Start("GlassBox key hook", () => SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0));
    }

    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int message = (int)wParam;
            bool pressed = message is WM_KEYDOWN or WM_SYSKEYDOWN, released = message is WM_KEYUP or WM_SYSKEYUP;
            if (info.vkCode is VK_CONTROL or VK_LCONTROL or VK_RCONTROL)
            {
                if (pressed && !down) // 按住不放会一直重复发按下，只认第一下
                {
                    down = true;
                    downAt = info.time;
                    if (info.time - lastTap > GapMax) taps = 0;
                }
                else if (released && down)
                {
                    down = false;
                    if (info.time - downAt <= TapMax)
                    {
                        taps++;
                        lastTap = info.time;
                        if (taps >= 2)
                        {
                            taps = 0;
                            Triggered?.Invoke();
                        }
                    }
                    else taps = 0;
                }
            }
            else if (pressed) taps = 0;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose() => stop();
}

/// <summary>毛玻璃：默认用 Win11 系统亚克力（DWM 负责渲染，几乎不占 CPU），兼容模式用旧的 accent 接口。</summary>
internal static class Glass
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34,
        DWMWA_SYSTEMBACKDROP_TYPE = 38;
    const int DWMWCP_ROUND = 2, DWMSBT_NONE = 1, DWMSBT_TRANSIENTWINDOW = 3;
    const int WCA_ACCENT_POLICY = 19, ACCENT_DISABLED = 0, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    public static void Apply(IntPtr hwnd, Theme theme, string mode)
    {
        Set(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, theme.Dark ? 1 : 0);
        Set(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        Set(hwnd, DWMWA_BORDER_COLOR, unchecked((int)0xFFFFFFFE)); // 去掉系统细边框，边框高光自己画

        bool accent = mode == "accent";
        var margins = accent ? new MARGINS() : new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        Set(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, accent ? DWMSBT_NONE : DWMSBT_TRANSIENTWINDOW);
        SetAccent(hwnd, accent ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_DISABLED, theme.Dark ? 0x401C1A18u : 0x30FFFFFFu);
    }

    static void Set(IntPtr hwnd, int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

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

/// <summary>
/// 开机自启用“任务计划程序”的登录触发器：Win11 会把注册表 Run 项和启动文件夹里的程序延后约 10 秒，
/// 登录任务不受这个延迟影响，登录后马上就能启动。不需要管理员权限。
/// </summary>
internal static class AutoStart
{
    const string TaskName = "GlassBox";
    const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static bool? cached;

    static string Exe => Environment.ProcessPath!;

    public static bool Enabled
    {
        // 只比对文件名：schtasks 输出用控制台代码页，路径里的中文读回来可能是乱码
        get => cached ??= Schtasks($"/Query /TN {TaskName} /XML", out var xml) == 0 && xml.Contains(Path.GetFileName(Exe), StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value) CreateTask();
            else
            {
                Schtasks($"/Delete /TN {TaskName} /F", out _);
                try { File.Delete(RegisteredPathFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            cached = value;
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            key?.DeleteValue("GlassBox", false); // 清掉旧版本写的 Run 项
        }
    }

    static void CreateTask()
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>玻璃收纳：登录后立即启动</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(Exe)}</Command><Arguments>--autostart</Arguments></Exec></Actions>
            </Task>
            """;
        string path = Path.Combine(Path.GetTempPath(), $"GlassBox.task.{Guid.NewGuid():N}.xml"); // 每次随机名：别的程序没法事先放一份等着被注册
        File.WriteAllText(path, xml, Encoding.Unicode);
        try
        {
            if (Schtasks($"/Create /TN {TaskName} /XML \"{path}\" /F", out var output) != 0)
                throw new InvalidOperationException(output.Trim());
        }
        finally { File.Delete(path); }
        try
        {
            Directory.CreateDirectory(ConfigStore.Dir);
            File.WriteAllText(RegisteredPathFile, Exe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>开机任务里记的是哪个 exe（任务计划读回来的中文路径可能是乱码，自己记一份）。</summary>
    static string RegisteredPathFile => Path.Combine(ConfigStore.Dir, "autostart-path");

    /// <summary>
    /// 程序文件夹被挪了位置：开机任务还指着旧路径，开机就起不来了。启动时看一眼，路径变了就按新位置重建。
    /// 平时路径没变时只读一个小文件，不启动 schtasks。
    /// </summary>
    public static void Repair()
    {
        try
        {
            if (!File.Exists(RegisteredPathFile)) return;
            string registered = File.ReadAllText(RegisteredPathFile).Trim();
            if (string.Equals(registered, Exe, StringComparison.OrdinalIgnoreCase)) return;
            if (Schtasks($"/Query /TN {TaskName}", out _) != 0) // 已经关掉了自启
            {
                File.Delete(RegisteredPathFile);
                return;
            }
            CreateTask();
            cached = true;
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    static int Schtasks(string arguments, out string output)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
