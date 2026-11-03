// GlassTap：玻璃收纳的“任务栏透明”。
//
// 玻璃收纳用系统自带的 XAML 诊断接口（InitializeXamlDiagnosticsEx，Visual Studio 的“实时可视化树”也用它）
// 把这个 DLL 载进 explorer。它只做一件事：找到任务栏背景的两层矩形（BackgroundFill / BackgroundStroke），
// 把不透明度调成 0；关掉时还原。不改文件、不改注册表，explorer 一重启就什么都不剩。
//
// 跟玻璃收纳通讯靠一个只收消息的窗口 "GlassBox.Tap"：
//   WM_APP+1  wParam = 模式（0 原样，1 透明），lParam = 玻璃收纳的进程号；玻璃收纳一退出（包括崩溃）就自动还原
//   WM_APP+2  返回找到了几层背景（0 说明这个 Windows 版本的任务栏长得不一样，没适配）
//
// 规矩：所有回调都吞掉异常，绝不能让 explorer 因为这里崩掉；DLL 载入后钉住不卸载，线程一直安全。

#include <windows.h>
#include <unknwn.h>
#include <ocidl.h>
#include <xamlom.h>
#undef GetCurrentTime // windows.h 的宏跟 XAML 动画里的同名方法撞了
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.UI.Xaml.h>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <set>
#include <vector>

namespace wux = winrt::Windows::UI::Xaml;
using winrt::Windows::System::DispatcherQueue;
using winrt::Windows::UI::Core::CoreDispatcher;

// {5C2D7E61-3B1A-4E7D-9C2F-6A1B8E0D4F31}，玻璃收纳那边写的是同一个
static constexpr CLSID CLSID_GlassTap = { 0x5c2d7e61, 0x3b1a, 0x4e7d, { 0x9c, 0x2f, 0x6a, 0x1b, 0x8e, 0x0d, 0x4f, 0x31 } };
static constexpr UINT WM_TAP_MODE = WM_APP + 1, WM_TAP_COUNT = WM_APP + 2;

static HMODULE g_module;
static winrt::com_ptr<IXamlDiagnostics> g_diag;
static std::atomic<int> g_mode{ 0 };

// 找到的背景层：记住它原来是不是本地设过不透明度，还原时原样还回去
struct Layer
{
    winrt::weak_ref<wux::UIElement> element;
    double original;
    bool hadLocal;
    DWORD thread;
};
// 任务栏所在的界面线程：改 XAML 只能回到它自己的线程上做
struct UiThread
{
    DWORD id;
    DispatcherQueue queue{ nullptr };
    CoreDispatcher dispatcher{ nullptr };
};
static std::mutex g_lock;
static std::vector<Layer> g_layers;
static std::vector<UiThread> g_threads;
static std::set<InstanceHandle> g_background; // TaskbarBackground 和它下面的元素

// ---------- 日志（%LOCALAPPDATA%\GlassBox\tap.log；有 tap-debug 文件时把整棵树也记下来） ----------

static wchar_t g_logPath[MAX_PATH];
static bool g_verbose;
static int g_treeLines;

static void InitLog()
{
    wchar_t dir[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", dir, MAX_PATH);
    if (n == 0 || n >= MAX_PATH - 40) return;
    wcscat_s(dir, L"\\GlassBox");
    CreateDirectoryW(dir, nullptr);
    wchar_t flag[MAX_PATH];
    swprintf_s(flag, L"%s\\tap-debug", dir);
    g_verbose = GetFileAttributesW(flag) != INVALID_FILE_ATTRIBUTES;
    swprintf_s(g_logPath, L"%s\\tap.log", dir);
    FILE* f;
    if (_wfopen_s(&f, g_logPath, L"w, ccs=UTF-8") == 0) fclose(f);
}

static void Log(const wchar_t* format, ...)
{
    if (!*g_logPath) return;
    static std::mutex logLock;
    std::lock_guard guard(logLock);
    FILE* f;
    if (_wfopen_s(&f, g_logPath, L"a, ccs=UTF-8") != 0) return;
    SYSTEMTIME t;
    GetLocalTime(&t);
    fwprintf(f, L"%02d:%02d:%02d.%03d  ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list args;
    va_start(args, format);
    vfwprintf(f, format, args);
    va_end(args);
    fputwc(L'\n', f);
    fclose(f);
}

// ---------- 改背景 ----------

static void ApplyHere() noexcept
{
    try
    {
        std::vector<Layer> snapshot;
        {
            std::lock_guard guard(g_lock);
            DWORD me = GetCurrentThreadId();
            for (auto it = g_layers.begin(); it != g_layers.end();)
            {
                if (it->thread != me) { ++it; continue; }
                auto element = it->element.get();
                if (!element) { it = g_layers.erase(it); continue; } // 任务栏重建过，旧的已经没了
                snapshot.push_back(*it);
                ++it;
            }
        }
        bool clear = g_mode != 0;
        for (auto& layer : snapshot)
        {
            try
            {
                auto element = layer.element.get();
                if (!element) continue;
                if (clear) element.Opacity(0.0);
                else if (layer.hadLocal) element.Opacity(layer.original);
                else element.ClearValue(wux::UIElement::OpacityProperty());
            }
            catch (...) {}
        }
    }
    catch (...) {}
}

static void ApplyEverywhere()
{
    std::vector<UiThread> threads;
    {
        std::lock_guard guard(g_lock);
        threads = g_threads;
    }
    for (auto& thread : threads)
    {
        try
        {
            if (thread.queue) thread.queue.TryEnqueue([] { ApplyHere(); });
            else if (thread.dispatcher) thread.dispatcher.RunAsync(winrt::Windows::UI::Core::CoreDispatcherPriority::Normal, [] { ApplyHere(); });
        }
        catch (...) {}
    }
}

static void SetMode(int mode)
{
    g_mode = mode;
    Log(L"模式 → %d", mode);
    ApplyEverywhere();
}

static void Adopt(InstanceHandle handle, const wchar_t* name)
{
    winrt::com_ptr<::IInspectable> raw;
    if (FAILED(g_diag->GetIInspectableFromHandle(handle, raw.put())) || !raw) return;
    winrt::Windows::Foundation::IInspectable object;
    winrt::copy_from_abi(object, raw.get());
    auto element = object.try_as<wux::UIElement>();
    if (!element) return;

    auto local = element.ReadLocalValue(wux::UIElement::OpacityProperty());
    bool hadLocal = local != wux::DependencyProperty::UnsetValue();
    double original = element.Opacity();
    DWORD me = GetCurrentThreadId();

    UiThread thread{ me };
    try { thread.queue = DispatcherQueue::GetForCurrentThread(); } catch (...) {}
    if (!thread.queue) try { thread.dispatcher = element.Dispatcher(); } catch (...) {}

    {
        std::lock_guard guard(g_lock);
        g_layers.push_back({ winrt::make_weak(element), original, hadLocal, me });
        bool known = false;
        for (auto& t : g_threads) known |= t.id == me;
        if (!known) g_threads.push_back(thread);
    }

    // explorer 换主题、切换显示器时可能把不透明度改回去：透明模式下再改回 0
    element.RegisterPropertyChangedCallback(wux::UIElement::OpacityProperty(), [](wux::DependencyObject const& sender, wux::DependencyProperty const&)
    {
        try
        {
            if (!g_mode) return;
            auto e = sender.as<wux::UIElement>();
            if (e.Opacity() != 0.0) e.Opacity(0.0);
        }
        catch (...) {}
    });
    if (g_mode) element.Opacity(0.0);
    Log(L"找到 %s（线程 %u，原不透明度 %.2f%s，%s）", name, me, original, hadLocal ? L"，本地值" : L"",
        thread.queue ? L"DispatcherQueue" : thread.dispatcher ? L"CoreDispatcher" : L"没有调度器");
}

// ---------- XAML 诊断回调 ----------

struct Watcher : winrt::implements<Watcher, IVisualTreeServiceCallback2>
{
    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation relation, VisualElement element, VisualMutationType mutation) noexcept override
    {
        try
        {
            if (mutation != Add) return S_OK;
            const wchar_t* type = element.Type ? element.Type : L"";
            const wchar_t* name = element.Name ? element.Name : L"";
            if (g_verbose && g_treeLines < 5000)
            {
                ++g_treeLines;
                Log(L"  %llu ← %llu  %s  \"%s\"", element.Handle, relation.Parent, type, name);
            }

            bool inside;
            {
                std::lock_guard guard(g_lock);
                inside = wcscmp(type, L"Taskbar.TaskbarBackground") == 0 || g_background.count(relation.Parent) > 0;
                if (inside) g_background.insert(element.Handle);
            }
            if (inside && (wcscmp(name, L"BackgroundFill") == 0 || wcscmp(name, L"BackgroundStroke") == 0))
                Adopt(element.Handle, name);
        }
        catch (...) {}
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) noexcept override { return S_OK; }
};

// ---------- 跟玻璃收纳通讯 ----------

static LRESULT CALLBACK ControlProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_TAP_COUNT)
    {
        std::lock_guard guard(g_lock);
        return (LRESULT)g_layers.size();
    }
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

static DWORD WINAPI ControlThread(void*)
{
    WNDCLASSW wc{};
    wc.lpfnWndProc = ControlProc;
    wc.hInstance = g_module;
    wc.lpszClassName = L"GlassBox.Tap";
    RegisterClassW(&wc);
    HWND hwnd = CreateWindowExW(0, wc.lpszClassName, L"GlassBox.Tap", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, g_module, nullptr);
    if (!hwnd) { Log(L"建通讯窗口失败 %u", GetLastError()); return 0; }

    HANDLE owner = nullptr; // 玻璃收纳的进程：它没了就还原
    for (;;)
    {
        DWORD r = MsgWaitForMultipleObjects(owner ? 1 : 0, &owner, FALSE, INFINITE, QS_ALLINPUT);
        if (owner && r == WAIT_OBJECT_0)
        {
            CloseHandle(owner);
            owner = nullptr;
            Log(L"玻璃收纳已退出，还原任务栏");
            SetMode(0);
            continue;
        }
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_TAP_MODE)
            {
                if (owner) CloseHandle(owner);
                owner = msg.lParam ? OpenProcess(SYNCHRONIZE, FALSE, (DWORD)msg.lParam) : nullptr;
                SetMode((int)msg.wParam);
                continue;
            }
            DispatchMessageW(&msg);
        }
    }
}

// ---------- COM：XAML 诊断按 CLSID 创建这个对象，再 SetSite 把诊断接口交给我们 ----------

struct Tap : winrt::implements<Tap, IObjectWithSite>
{
    winrt::com_ptr<IUnknown> site;

    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* value) noexcept override
    {
        site.copy_from(value);
        if (!value) return S_OK;
        try
        {
            static std::once_flag once;
            std::call_once(once, []
            {
                HMODULE pinned;
                GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)&g_module, &pinned);
                InitLog();
                Log(L"已载入 explorer（进程 %u）", GetCurrentProcessId());
                if (HANDLE t = CreateThread(nullptr, 0, ControlThread, nullptr, 0, nullptr)) CloseHandle(t);
            });
            g_diag = site.as<IXamlDiagnostics>();

            // 在当前线程上直接订阅会卡住（它要回到界面线程上跑），换个线程订阅
            auto watcher = winrt::make_self<Watcher>();
            HANDLE t = CreateThread(nullptr, 0, [](void* p) -> DWORD
            {
                winrt::com_ptr<Watcher> w;
                w.attach(static_cast<Watcher*>(p));
                try
                {
                    HRESULT hr = g_diag.as<IVisualTreeService3>()->AdviseVisualTreeChange(w.get());
                    Log(L"订阅可视化树 0x%08X", hr);
                }
                catch (...) { Log(L"订阅可视化树失败"); }
                return 0;
            }, watcher.get(), 0, nullptr);
            if (t)
            {
                watcher.detach();
                CloseHandle(t);
            }
        }
        catch (...) { return E_FAIL; }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** result) noexcept override
    {
        if (!result) return E_POINTER;
        *result = nullptr;
        return site ? site->QueryInterface(riid, result) : E_FAIL;
    }
};

struct Factory : winrt::implements<Factory, IClassFactory>
{
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** result) noexcept override
    {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        try { return winrt::make_self<Tap>()->QueryInterface(riid, result); }
        catch (...) { return winrt::to_hresult(); }
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL) noexcept override { return S_OK; }
};

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** result)
{
    if (!result) return E_POINTER;
    *result = nullptr;
    if (clsid != CLSID_GlassTap) return CLASS_E_CLASSNOTAVAILABLE;
    try { return winrt::make_self<Factory>()->QueryInterface(riid, result); }
    catch (...) { return winrt::to_hresult(); }
}

STDAPI DllCanUnloadNow() { return S_FALSE; } // 有线程在里面跑，永远不卸载

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
