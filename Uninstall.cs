using System.Diagnostics;
using static GlassBox.Native;

namespace GlassBox;

/// <summary>
/// GlassBox.exe --uninstall（托盘菜单 设置 → 卸载玻璃收纳… 也是它）：
/// 关掉正在运行的那个，还原桌面图标和任务栏，删掉开机自启，问要不要连设置一起删。
/// 玻璃收纳从来不移动、不删除用户的文件，卸载也一样不碰。做完以后直接删掉程序文件夹就干净了。
/// </summary>
internal static class Uninstaller
{
    public static int Run()
    {
        var answer = MessageBox.Show(
            L.T("卸载玻璃收纳？\n\n会还原桌面图标和任务栏、关掉开机自动启动。\n你的文件一个都不会动。", "Uninstall GlassBox?\n\nThis restores your desktop icons and taskbar and turns off start with Windows.\nNone of your files will be touched."),
            L.T("玻璃收纳", "GlassBox"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return 1;

        StopRunning();
        // 正常退出时已经还原过了；被强制结束过的话，看记号再还原一次
        if (DesktopShell.HidByUs) DesktopShell.SetIconsVisible(true);
        TaskbarStyle.Revert();
        SystemTaskbar.RecoverIfNeeded();
        try { AutoStart.Enabled = false; }
        catch (Exception ex) { Log.Error(ex); }

        bool wipe = MessageBox.Show(
            L.T("同时删除玻璃收纳的设置和缓存吗？\n\n（分类、顺序、常用记录、歌词缓存。选“否”会留着，以后重新打开还能接着用）", "Also delete GlassBox's settings and cache?\n\n(Categories, order, usage history, lyrics cache. Choose No to keep them for next time.)"),
            L.T("玻璃收纳", "GlassBox"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        if (wipe)
        {
            foreach (var dir in new[]
            {
                ConfigStore.Dir,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBox"),
            })
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch (Exception) { } // 删不掉的（被占用）留着也无害
            }
        }

        MessageBox.Show(L.T("已经卸载好了。\n\n现在可以直接删除玻璃收纳所在的文件夹。", "Uninstalled.\n\nYou can now delete the GlassBox folder."), L.T("玻璃收纳", "GlassBox"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    /// <summary>让正在运行的那个正常退出（会存好布局、还原图标），最多等 8 秒。</summary>
    static void StopRunning()
    {
        var host = FindWindow(null, "GlassBox.Host");
        if (host != IntPtr.Zero) PostMessage(host, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        foreach (var process in Process.GetProcessesByName("GlassBox"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (!process.WaitForExit(8000)) process.Kill();
                }
                catch (Exception) { }
            }
        }
    }
}
