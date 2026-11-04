using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassBox;

/// <summary>
/// 系统的音频（Core Audio）：读当前输出设备的音量峰值给胶囊的音柱跳；读/改某个程序自己的音量（音量合成器里那一条）；
/// 现在从哪个设备出声、换到别的设备。
/// 跟 Media.cs 一样直接按虚表调用，不引用额外的库。只在界面线程上、要用时才调，平时什么都不做。
/// </summary>
internal static unsafe class Audio
{
    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    static readonly Guid IID_IAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
    static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    // 虚表位置（前 3 个是 IUnknown 的）
    const int EnumeratorGetDefault = 4, DeviceActivate = 3, MeterGetPeak = 3;
    const int ManagerGetSessionEnumerator = 5, SessionsGetCount = 3, SessionsGetSession = 4;
    const int ControlGetProcessId = 14, SimpleSetVolume = 3, SimpleGetVolume = 4;
    const int EndpointSetScalar = 7, EndpointGetScalar = 9;
    const int CLSCTX_ALL = 23, eRender = 0, eMultimedia = 1;

    static IntPtr enumerator, meter;
    static long meterSince;

    /// <summary>现在输出的声音有多响（0..1）。默认输出设备换了（插耳机）会自己跟过去。</summary>
    public static float Peak()
    {
        try
        {
            if (meter == IntPtr.Zero || Environment.TickCount64 - meterSince > 3000)
            {
                Release(ref meter);
                meter = Activate(IID_IAudioMeterInformation);
                meterSince = Environment.TickCount64;
            }
            if (meter == IntPtr.Zero) return 0;
            float peak = 0;
            if (((delegate* unmanaged<IntPtr, float*, int>)Slot(meter, MeterGetPeak))(meter, &peak) < 0)
            {
                Release(ref meter);
                return 0;
            }
            return Math.Clamp(peak, 0, 1);
        }
        catch (Exception) { return 0; }
    }

    /// <summary>
    /// 这个播放来源（Windows 媒体控制报的程序 id，比如 “chrome”“Spotify.exe”）现在的音量。
    /// 找不到它自己的音频（有的程序声音是别的进程放的）就给系统总音量，PerApp = false。
    /// </summary>
    public static (float Volume, bool PerApp)? GetVolume(string source)
    {
        try
        {
            float? own = null;
            ForEachSession(source, simple =>
            {
                float v = 0;
                if (((delegate* unmanaged<IntPtr, float*, int>)Slot(simple, SimpleGetVolume))(simple, &v) >= 0) own = Math.Max(own ?? 0, v);
            });
            if (own != null) return (own.Value, true);
            IntPtr endpoint = Activate(IID_IAudioEndpointVolume);
            if (endpoint == IntPtr.Zero) return null;
            try
            {
                float v = 0;
                return ((delegate* unmanaged<IntPtr, float*, int>)Slot(endpoint, EndpointGetScalar))(endpoint, &v) < 0 ? null : (v, false);
            }
            finally { Release(ref endpoint); }
        }
        catch (Exception) { return null; }
    }

    public static void SetVolume(string source, float volume, bool perApp)
    {
        volume = Math.Clamp(volume, 0, 1);
        try
        {
            if (perApp)
            {
                ForEachSession(source, simple => ((delegate* unmanaged<IntPtr, float, Guid*, int>)Slot(simple, SimpleSetVolume))(simple, volume, null));
                return;
            }
            IntPtr endpoint = Activate(IID_IAudioEndpointVolume);
            if (endpoint == IntPtr.Zero) return;
            try { ((delegate* unmanaged<IntPtr, float, Guid*, int>)Slot(endpoint, EndpointSetScalar))(endpoint, volume, null); }
            finally { Release(ref endpoint); }
        }
        catch (Exception) { }
    }

    /// <summary>默认输出设备上，进程名对得上这个来源的每一路声音（Chrome 常常有好几路）。</summary>
    static void ForEachSession(string source, Action<IntPtr> use)
    {
        string id = source.ToLowerInvariant();
        if (id == "308046b0af4a39cb") id = "firefox"; // Firefox 报的是一串固定的编号
        IntPtr manager = Activate(IID_IAudioSessionManager2);
        if (manager == IntPtr.Zero) return;
        IntPtr sessions = IntPtr.Zero;
        try
        {
            if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(manager, ManagerGetSessionEnumerator))(manager, &sessions) < 0 || sessions == IntPtr.Zero) return;
            int count = 0;
            ((delegate* unmanaged<IntPtr, int*, int>)Slot(sessions, SessionsGetCount))(sessions, &count);
            for (int i = 0; i < count; i++)
            {
                IntPtr control = IntPtr.Zero;
                if (((delegate* unmanaged<IntPtr, int, IntPtr*, int>)Slot(sessions, SessionsGetSession))(sessions, i, &control) < 0 || control == IntPtr.Zero) continue;
                try
                {
                    var iid = IID_IAudioSessionControl2;
                    if (Marshal.QueryInterface(control, ref iid, out var control2) < 0) continue;
                    uint pid = 0;
                    try { ((delegate* unmanaged<IntPtr, uint*, int>)Slot(control2, ControlGetProcessId))(control2, &pid); }
                    finally { Marshal.Release(control2); }
                    if (pid == 0) continue; // 系统声音
                    string name = ProcessName(pid);
                    if (name.Length < 3 || !id.Contains(name)) continue;
                    var sid = IID_ISimpleAudioVolume;
                    if (Marshal.QueryInterface(control, ref sid, out var simple) < 0) continue;
                    try { use(simple); }
                    finally { Marshal.Release(simple); }
                }
                finally { Marshal.Release(control); }
            }
        }
        finally
        {
            Release(ref sessions);
            Release(ref manager);
        }
    }

    static string ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant();
        }
        catch (Exception) { return ""; }
    }

    /// <summary>在默认输出设备上取一个接口。</summary>
    static IntPtr Activate(Guid iid)
    {
        if (!EnsureEnumerator()) return IntPtr.Zero;
        IntPtr device = IntPtr.Zero, result = IntPtr.Zero;
        if (((delegate* unmanaged<IntPtr, int, int, IntPtr*, int>)Slot(enumerator, EnumeratorGetDefault))(enumerator, eRender, eMultimedia, &device) < 0 || device == IntPtr.Zero)
            return IntPtr.Zero; // 没有输出设备
        try
        {
            ((delegate* unmanaged<IntPtr, Guid*, int, IntPtr, IntPtr*, int>)Slot(device, DeviceActivate))(device, &iid, CLSCTX_ALL, IntPtr.Zero, &result);
            return result;
        }
        finally { Release(ref device); }
    }

    // ---------- 输出设备：现在从哪里出声、换到哪里 ----------

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;
        [FieldOffset(8)] public uint uintVal;
    }

    const int EnumeratorEnumEndpoints = 3, DeviceOpenPropertyStore = 4, DeviceGetId = 5;
    const int CollectionGetCount = 3, CollectionItem = 4, StoreGetValue = 5;
    const int DEVICE_STATE_ACTIVE = 1, STGM_READ = 0, VT_LPWSTR = 31, VT_UI4 = 19;
    const uint FormFactorHeadphones = 3, FormFactorHeadset = 5;
    static readonly PROPERTYKEY FriendlyNameKey = new() { fmtid = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
    static readonly PROPERTYKEY FormFactorKey = new() { fmtid = new("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), pid = 0 };
    // 设置默认设备没有公开的接口，“声音”设置和所有切换小工具用的都是这个（Windows 7 到 11 一直在）
    static readonly Guid CLSID_PolicyConfig = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    static readonly Guid IID_IPolicyConfig = new("F8679F50-850A-41CF-9C72-430F290290C8");
    const int PolicySetDefaultEndpoint = 13;

    /// <summary>一个输出设备：系统里的 id、名字（“耳机 (WH-1000XM5)”）、是不是耳机。</summary>
    public readonly record struct Output(string Id, string Name, bool Headphones);

    /// <summary>现在默认的输出设备（声音从这里出来）。没有就是 null。</summary>
    public static Output? DefaultOutput()
    {
        try
        {
            if (!EnsureEnumerator()) return null;
            IntPtr device = IntPtr.Zero;
            if (((delegate* unmanaged<IntPtr, int, int, IntPtr*, int>)Slot(enumerator, EnumeratorGetDefault))(enumerator, eRender, eMultimedia, &device) < 0 || device == IntPtr.Zero) return null;
            try { return Describe(device); }
            finally { Release(ref device); }
        }
        catch (Exception) { return null; }
    }

    /// <summary>所有能用的输出设备（插着的、连着的）。</summary>
    public static List<Output> Outputs()
    {
        var list = new List<Output>();
        try
        {
            if (!EnsureEnumerator()) return list;
            IntPtr collection = IntPtr.Zero;
            if (((delegate* unmanaged<IntPtr, int, int, IntPtr*, int>)Slot(enumerator, EnumeratorEnumEndpoints))(enumerator, eRender, DEVICE_STATE_ACTIVE, &collection) < 0 || collection == IntPtr.Zero) return list;
            try
            {
                uint count = 0;
                ((delegate* unmanaged<IntPtr, uint*, int>)Slot(collection, CollectionGetCount))(collection, &count);
                for (uint i = 0; i < count; i++)
                {
                    IntPtr device = IntPtr.Zero;
                    if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(collection, CollectionItem))(collection, i, &device) < 0 || device == IntPtr.Zero) continue;
                    try { if (Describe(device) is { } output) list.Add(output); }
                    finally { Release(ref device); }
                }
            }
            finally { Release(ref collection); }
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>
    /// 把声音换到这个设备（跟在 Windows“声音”设置里选输出设备一样，三种用途一起换）。
    /// 只在用户自己点了的时候调。
    /// </summary>
    public static bool SetDefaultOutput(string id)
    {
        IntPtr policy = IntPtr.Zero, text = IntPtr.Zero;
        try
        {
            var clsid = CLSID_PolicyConfig;
            var iid = IID_IPolicyConfig;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out policy) < 0 || policy == IntPtr.Zero) return false;
            text = Marshal.StringToCoTaskMemUni(id);
            bool ok = true;
            for (int role = 0; role < 3; role++) // 控制台、多媒体、通讯
                ok &= ((delegate* unmanaged<IntPtr, IntPtr, int, int>)Slot(policy, PolicySetDefaultEndpoint))(policy, text, role) >= 0;
            Release(ref meter); // 音柱跟着换过去
            return ok;
        }
        catch (Exception) { return false; }
        finally
        {
            if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text);
            Release(ref policy);
        }
    }

    static Output? Describe(IntPtr device)
    {
        IntPtr idText = IntPtr.Zero, store = IntPtr.Zero;
        try
        {
            if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(device, DeviceGetId))(device, &idText) < 0 || idText == IntPtr.Zero) return null;
            string id = Marshal.PtrToStringUni(idText) ?? "";
            if (((delegate* unmanaged<IntPtr, int, IntPtr*, int>)Slot(device, DeviceOpenPropertyStore))(device, STGM_READ, &store) < 0 || store == IntPtr.Zero) return new Output(id, L.T("输出设备", "Output device"), false);
            string name = L.T("输出设备", "Output device");
            bool headphones = false;
            var key = FriendlyNameKey;
            PROPVARIANT value = default;
            if (((delegate* unmanaged<IntPtr, PROPERTYKEY*, PROPVARIANT*, int>)Slot(store, StoreGetValue))(store, &key, &value) >= 0)
            {
                if (value.vt == VT_LPWSTR && value.ptr != IntPtr.Zero) name = Marshal.PtrToStringUni(value.ptr) ?? name;
                PropVariantClear(ref value);
            }
            key = FormFactorKey;
            value = default;
            if (((delegate* unmanaged<IntPtr, PROPERTYKEY*, PROPVARIANT*, int>)Slot(store, StoreGetValue))(store, &key, &value) >= 0)
            {
                headphones = value.vt == VT_UI4 && value.uintVal is FormFactorHeadphones or FormFactorHeadset;
                PropVariantClear(ref value);
            }
            return new Output(id, name, headphones);
        }
        finally
        {
            if (idText != IntPtr.Zero) Marshal.FreeCoTaskMem(idText);
            Release(ref store);
        }
    }

    static bool EnsureEnumerator()
    {
        if (enumerator != IntPtr.Zero) return true;
        var clsid = CLSID_MMDeviceEnumerator;
        var eid = IID_IMMDeviceEnumerator;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref eid, out enumerator) < 0) enumerator = IntPtr.Zero;
        return enumerator != IntPtr.Zero;
    }

    static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    static void Release(ref IntPtr ptr)
    {
        if (ptr != IntPtr.Zero) Marshal.Release(ptr);
        ptr = IntPtr.Zero;
    }

    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr obj);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT value);
}
