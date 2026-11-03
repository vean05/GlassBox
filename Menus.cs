using System.Runtime.InteropServices;
using static GlassBox.Native;

namespace GlassBox;

/// <summary>原生 Win32 弹出菜单（跟系统右键菜单一个样子，比 WinForms 菜单轻）。</summary>
internal sealed class PopupMenu : IDisposable
{
    const int FirstId = 0x7000; // 0x0001–0x6FFF 留给系统右键菜单
    readonly Dictionary<int, Action> actions;
    readonly PopupMenu? root;
    int nextId = FirstId;

    public IntPtr Handle { get; } = CreatePopupMenu();
    public int Count { get; private set; }

    public PopupMenu() => actions = new();

    PopupMenu(PopupMenu root)
    {
        this.root = root;
        actions = root.actions;
    }

    public void Add(string text, Action action, bool check = false, bool enabled = true)
    {
        int id = (root ?? this).nextId++;
        actions[id] = action;
        AppendMenu(Handle, MF_STRING | (check ? MF_CHECKED : 0) | (enabled ? 0 : MF_GRAYED), (UIntPtr)id, text);
        Count++;
    }

    public PopupMenu Sub(string text, bool enabled = true)
    {
        var sub = new PopupMenu(root ?? this);
        AppendMenu(Handle, MF_POPUP | (enabled ? 0 : MF_GRAYED), (UIntPtr)(ulong)sub.Handle, text);
        Count++;
        return sub;
    }

    public void Separator()
    {
        AppendMenu(Handle, MF_SEPARATOR, UIntPtr.Zero, null);
        Count++;
    }

    public bool TryRun(int id)
    {
        if (!actions.TryGetValue(id, out var action)) return false;
        action();
        return true;
    }

    public void Show(Point at, IntPtr owner)
    {
        int id = Track(Handle, at, owner);
        if (id != 0) TryRun(id);
    }

    public static int Track(IntPtr menu, Point at, IntPtr owner)
    {
        SetForegroundWindow(owner); // 不这样做的话点菜单外面菜单不会消失
        int id = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, at.X, at.Y, owner, IntPtr.Zero);
        PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        return id;
    }

    public void Dispose()
    {
        if (root == null) DestroyMenu(Handle); // 子菜单随父菜单一起销毁
    }
}

/// <summary>隐藏的顶层窗口：做菜单的宿主、转发外壳菜单消息、接收 explorer 重启和系统设置变化。</summary>
internal sealed class MenuHost : NativeWindow, IDisposable
{
    readonly int taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    IContextMenu2? menu2;
    IContextMenu3? menu3;

    public event Action? ExplorerRestarted;
    public event Action? SettingsChanged;
    public event Action? ExitRequested;
    public event Action? MusicRequested;
    public event Action? SessionEnding;

    public MenuHost() => CreateHandle(new CreateParams { Caption = "GlassBox.Host", Style = WS_POPUP, ExStyle = WS_EX_TOOLWINDOW });

    public void SetActiveMenu(IContextMenu? menu)
    {
        menu3 = menu as IContextMenu3;
        menu2 = menu3 == null ? menu as IContextMenu2 : null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == taskbarCreated) ExplorerRestarted?.Invoke();
        else if (m.Msg == WM_ENDSESSION && m.WParam != IntPtr.Zero) SessionEnding?.Invoke();
        else if (m.Msg == WM_CLOSE)
        {
            ExitRequested?.Invoke(); // GlassBox.exe --exit
            return;
        }
        else if (m.Msg == WM_SHOW_MUSIC) MusicRequested?.Invoke(); // GlassBox.exe --music
        else if (m.Msg is WM_SETTINGCHANGE or WM_DISPLAYCHANGE) SettingsChanged?.Invoke();
        else if (m.Msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
        {
            // “打开方式”“发送到”这类子菜单是外壳扩展自己画的，消息要转给它
            if (menu3 != null && menu3.HandleMenuMsg2((uint)m.Msg, m.WParam, m.LParam, out var result) == 0)
            {
                m.Result = result;
                return;
            }
            if (menu2 != null && menu2.HandleMenuMsg((uint)m.Msg, m.WParam, m.LParam) == 0)
            {
                m.Result = IntPtr.Zero;
                return;
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}

/// <summary>在自己的菜单项下面接上 Windows 原生的文件右键菜单。</summary>
internal static class ShellMenu
{
    const uint FirstShellId = 1, LastShellId = 0x6FFF;
    const uint CMF_NORMAL = 0x0, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    const uint GCS_VERBW = 0x4;
    const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000;
    static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");
    static readonly Guid IID_IShellItem = typeof(IShellItem).GUID;
    static readonly Guid IID_IContextMenu = typeof(IContextMenu).GUID;

    public static void Show(MenuHost host, string path, Point at, Action<PopupMenu> addOwnItems, Func<string, bool> interceptVerb)
    {
        using var menu = new PopupMenu();
        addOwnItems(menu);
        IContextMenu? shell = null;
        try
        {
            shell = Create(path);
            if (shell != null)
            {
                menu.Separator();
                uint flags = CMF_NORMAL | CMF_CANRENAME | ((Control.ModifierKeys & Keys.Shift) != 0 ? CMF_EXTENDEDVERBS : 0);
                shell.QueryContextMenu(menu.Handle, (uint)menu.Count, FirstShellId, LastShellId, flags);
                host.SetActiveMenu(shell);
            }

            int id = PopupMenu.Track(menu.Handle, at, host.Handle);
            host.SetActiveMenu(null);
            if (id == 0 || menu.TryRun(id) || shell == null || id > LastShellId) return;

            uint offset = (uint)id - FirstShellId;
            string? verb = GetVerb(shell, offset);
            if (verb != null && interceptVerb(verb)) return;
            Invoke(shell, offset, host.Handle, at, Path.GetDirectoryName(path));
        }
        catch (Exception ex) { Log.Error(ex); }
        finally
        {
            host.SetActiveMenu(null);
            if (shell != null) Marshal.ReleaseComObject(shell);
        }
    }

    static IContextMenu? Create(string path)
    {
        var iidItem = IID_IShellItem;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out var itemPtr) != 0 || itemPtr == IntPtr.Zero) return null;
        var item = (IShellItem)Marshal.GetObjectForIUnknown(itemPtr);
        Marshal.Release(itemPtr);
        try
        {
            var bhid = BHID_SFUIObject;
            var iid = IID_IContextMenu;
            item.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var menuPtr);
            var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
            Marshal.Release(menuPtr);
            return menu;
        }
        catch (Exception) { return null; }
        finally { Marshal.ReleaseComObject(item); }
    }

    static string? GetVerb(IContextMenu menu, uint offset)
    {
        IntPtr buffer = Marshal.AllocHGlobal(520);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return menu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buffer, 260) == 0 ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static void Invoke(IContextMenu menu, uint offset, IntPtr owner, Point at, string? directory)
    {
        IntPtr dir = directory == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(directory);
        try
        {
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                hwnd = owner,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                lpDirectoryW = dir,
                nShow = 1,
                ptInvoke = new POINT { X = at.X, Y = at.Y },
            };
            menu.InvokeCommand(ref info);
        }
        finally { if (dir != IntPtr.Zero) Marshal.FreeHGlobal(dir); }
    }
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler(IntPtr bindContext, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem parent);
    void GetDisplayName(uint sigdn, out IntPtr name);
    void GetAttributes(uint mask, out uint attributes);
    void Compare(IShellItem other, uint hint, out int order);
}

[StructLayout(LayoutKind.Sequential)]
internal struct CMINVOKECOMMANDINFOEX
{
    public int cbSize;
    public uint fMask;
    public IntPtr hwnd, lpVerb, lpParameters, lpDirectory;
    public int nShow;
    public uint dwHotKey;
    public IntPtr hIcon, lpTitle, lpVerbW, lpParametersW, lpDirectoryW, lpTitleW;
    public POINT ptInvoke;
}

[ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint firstId, uint lastId, uint flags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
    [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
}

[ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2
{
    [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint firstId, uint lastId, uint flags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
    [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
    [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
}

[ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu3
{
    [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint firstId, uint lastId, uint flags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
    [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
    [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
}

internal static class InputDialog
{
    public static string? Show(string title, string initial, Point near, int selectLength = -1)
    {
        using var form = new Form();
        var box = new TextBox();
        var ok = new Button();
        var cancel = new Button();
        form.SuspendLayout();
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = new Font("Microsoft YaHei UI", 9F);
        form.Text = title;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MinimizeBox = form.MaximizeBox = false;
        form.ShowInTaskbar = false;
        form.TopMost = true;
        form.StartPosition = FormStartPosition.Manual;
        form.ClientSize = new Size(320, 92);
        box.SetBounds(12, 14, 296, 26);
        box.Text = initial;
        ok.Text = "确定";
        ok.DialogResult = DialogResult.OK;
        ok.SetBounds(148, 52, 76, 28);
        cancel.Text = "取消";
        cancel.DialogResult = DialogResult.Cancel;
        cancel.SetBounds(232, 52, 76, 28);
        form.Controls.AddRange(new Control[] { box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.ResumeLayout(false);

        var area = Screen.FromPoint(near).WorkingArea;
        form.Location = new Point(Math.Clamp(near.X, area.Left, area.Right - form.Width), Math.Clamp(near.Y, area.Top, area.Bottom - form.Height));
        form.Shown += (_, _) =>
        {
            form.Activate();
            box.Focus();
            box.Select(0, selectLength < 0 ? box.TextLength : selectLength);
        };
        return form.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
    }
}
