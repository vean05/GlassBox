using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassBox;

/// <summary>
/// 系统的音频（Core Audio）：读当前输出设备的音量峰值给胶囊的音柱跳；读/改某个程序自己的音量（音量合成器里那一条）。
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
        if (enumerator == IntPtr.Zero)
        {
            var clsid = CLSID_MMDeviceEnumerator;
            var eid = IID_IMMDeviceEnumerator;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref eid, out enumerator) < 0) enumerator = IntPtr.Zero;
            if (enumerator == IntPtr.Zero) return IntPtr.Zero;
        }
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

    static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    static void Release(ref IntPtr ptr)
    {
        if (ptr != IntPtr.Zero) Marshal.Release(ptr);
        ptr = IntPtr.Zero;
    }

    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr obj);
}
