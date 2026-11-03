using static GlassBox.Native;

namespace GlassBox;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // 任务栏透明的载入助手：调完 XAML 诊断接口就退出（见 TaskbarStyle.Inject）
        if (args.Length == 3 && args[0] == "--tap" && uint.TryParse(args[1], out uint explorer))
            return TaskbarStyle.InjectHere(explorer, args[2]);

        if (args.Contains("--uninstall"))
        {
            ApplicationConfiguration.Initialize();
            return Uninstaller.Run();
        }

        using var mutex = new Mutex(true, @"Local\GlassBox.SingleInstance", out bool first);
        if (!first || args.Contains("--exit"))
        {
            // 已经在运行：--exit 让它正常退出（会还原桌面图标），--music 打开音乐卡片，否则什么都不做
            if (args.Contains("--exit")) PostMessage(FindWindow(null, "GlassBox.Host"), WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            else if (args.Contains("--music")) PostMessage(FindWindow(null, "GlassBox.Host"), WM_SHOW_MUSIC, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }

        // 开机自启时可能比 explorer 还快，桌面图标层还没建好；等它出现（最多 30 秒）
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (DesktopShell.FindDefView() == IntPtr.Zero && wait.ElapsedMilliseconds < 30000) Thread.Sleep(150);

        // 旧版本“隐藏任务栏”被强制结束时留下的：先把任务栏还回来
        SystemTaskbar.RecoverIfNeeded();

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error(e.ExceptionObject as Exception);
            DesktopShell.SetIconsVisible(true); // 万一崩溃，至少把桌面图标和任务栏还给用户
            TaskbarStyle.Revert();
        };

        try
        {
            SetPreferredAppMode(1); // AllowDark：菜单跟随系统深色/浅色
            FlushMenuThemes();
        }
        catch { }

        Application.Run(new GlassBoxApp());
        return 0;
    }
}
