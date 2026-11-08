using System.Runtime.InteropServices;

namespace GlassBox;

internal static class Native
{
    public const int WM_NULL = 0x0000, WM_SETTINGCHANGE = 0x001A, WM_MOUSEACTIVATE = 0x0021, WM_DRAWITEM = 0x002B,
        WM_MEASUREITEM = 0x002C, WM_WINDOWPOSCHANGING = 0x0046, WM_DISPLAYCHANGE = 0x007E, WM_NCCALCSIZE = 0x0083,
        WM_NCHITTEST = 0x0084, WM_NCACTIVATE = 0x0086, WM_NCLBUTTONDBLCLK = 0x00A3, WM_COMMAND = 0x0111,
        WM_SYSCOMMAND = 0x0112, WM_INITMENUPOPUP = 0x0117, WM_MENUCHAR = 0x0120, WM_LBUTTONDOWN = 0x0201,
        WM_EXITSIZEMOVE = 0x0232;

    public const int MA_NOACTIVATE = 3;
    public const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
        HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    public const int SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030;

    public const int WS_POPUP = unchecked((int)0x80000000), WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000,
        WS_CLIPCHILDREN = 0x02000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000, WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_CLOSE = 0x0010, WM_ENDSESSION = 0x0016, WM_GETMINMAXINFO = 0x0024;
    /// <summary>GlassBox.exe --music 发给正在运行的那个：打开音乐卡片。</summary>
    public const int WM_SHOW_MUSIC = 0x8000 + 0x4D;
    public const int WM_ACTIVATE = 0x0006, WM_GETOBJECT = 0x003D, WM_IME_STARTCOMPOSITION = 0x010D, MA_ACTIVATE = 1;
    public const int WS_EX_LAYERED = 0x00080000, WS_EX_TOPMOST = 0x00000008;
    public const int WM_POWERBROADCAST = 0x0218, PBT_APMPOWERSTATUSCHANGE = 0x000A;
    public const int ULW_ALPHA = 2;
    public const byte AC_SRC_ALPHA = 1;
    public const uint SPI_GETDESKWALLPAPER = 0x0073, SPI_GETCLIENTAREAANIMATION = 0x1042;
    public const int COLOR_DESKTOP = 1, CFS_POINT = 2, HALFTONE = 4;
    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public const int GWLP_HWNDPARENT = -8, GWL_EXSTYLE = -20, GWL_STYLE = -16;

    public const int WH_MOUSE_LL = 14, SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37;

    public const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;

    public const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO { public BITMAPINFOHEADER Header; public uint Mask0, Mask1, Mask2; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ACCENT_POLICY { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWCOMPOSITIONATTRIBDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct COMPOSITIONFORM { public int dwStyle; public POINT ptCurrentPos; public RECT rcArea; }

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    // 全局键盘钩子（只看 Ctrl 有没有被连按两下）、把胶囊临时提到最前面
    public const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    public const uint WM_QUIT = 0x0012;
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("shell32.dll")] public static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr ptr);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc proc, IntPtr module, uint threadId);
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern unsafe int WideCharToMultiByte(uint codePage, uint flags, char* wide, int wideLength, byte* multi, int multiLength, IntPtr defaultChar, IntPtr usedDefault);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, char[] buffer, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
    public const int WS_EX_TRANSPARENT = 0x00000020, GA_ROOT = 2;
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] public static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("user32.dll")] public static extern int GetSysColor(int index);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, char[] value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] public static extern bool SystemParametersInfo(uint action, uint param, out int value, uint flags);

    [DllImport("imm32.dll")] public static extern IntPtr ImmGetContext(IntPtr hwnd);
    [DllImport("imm32.dll")] public static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr context);
    [DllImport("imm32.dll")] public static extern bool ImmSetCompositionWindow(IntPtr context, ref COMPOSITIONFORM form);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BITMAPINFO info, uint usage);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] public static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IntPtr ppv);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int StrCmpLogicalW(string a, string b);
    [DllImport("ole32.dll")] public static extern void CoFreeUnusedLibraries();

    // 未公开但被广泛使用：让 Win32 弹出菜单跟随系统深色模式
    [DllImport("uxtheme.dll", EntryPoint = "#135")] public static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] public static extern void FlushMenuThemes();

    // 还原旧版本藏起来的任务栏；看前台是不是全屏
    public const int SW_SHOWNA = 8;
    public const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

    /// <summary>前台是全屏游戏、全屏视频或者在放幻灯片（系统这时也不弹通知）。</summary>
    public static bool FullscreenBusy()
    {
        if (SHQueryUserNotificationState(out int state) != 0 || state is not (QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)) return false;
        // 系统说“全屏”也可能只是最大化的浏览器在放视频（任务栏自动隐藏时最大化也盖满整个屏幕）：
        // 真全屏的窗口没有标题栏，而且正好盖住它所在的整块屏幕
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !GetWindowRect(fg, out var r)) return true;
        // 桌面本身（点了桌面、焦点还给了桌面）也盖满屏幕、没有标题栏，但不是全屏程序；自己的窗口也不算
        if (ClassOf(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == Environment.ProcessId) return false;
        if (((long)GetWindowLongPtr(fg, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return false;
        var screen = Screen.FromHandle(fg).Bounds;
        return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }

    // 丢进回收站（能撤销的删除）
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>
    /// 把一个文件放进回收站。进不了回收站（太大、网络盘）时系统会先问要不要彻底删除，
    /// 不会不声不响地删掉。
    /// </summary>
    public static bool Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0", // 要两个 \0 结尾，封送会再补一个
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    public static string ClassOf(IntPtr hwnd)
    {
        var buffer = new char[64];
        int n = GetClassName(hwnd, buffer, buffer.Length);
        return n > 0 ? new string(buffer, 0, n) : "";
    }
}
