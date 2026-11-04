using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GlassBox;

/// <summary>
/// 蓝牙耳机的电量：Windows“设置 → 蓝牙和其他设备”里显示的就是这个设备属性（耳机自己报上来的）。
/// 只读，不连接、不配对、不改任何设置；只在音乐卡片开着时一分钟读一次。
/// </summary>
internal static class BluetoothBattery
{
    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
        public DEVPROPKEY(string guid, uint id) { fmtid = new Guid(guid); pid = id; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    static readonly IntPtr Invalid = new(-1);
    static readonly DEVPROPKEY Battery = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    static readonly DEVPROPKEY Connected = new("83DA6326-97A6-4088-9453-A1923F573B29", 15);
    static readonly DEVPROPKEY FriendlyName = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
    // 同一副耳机会有好几个节点（免提、音频、遥控），名字后面带着这些尾巴
    static readonly Regex ProfileSuffix = new(@"\s+(Hands-Free( AG)?( Audio)?|Avrcp Transport|Stereo|A2DP( SNK)?|LE)$", RegexOptions.IgnoreCase);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, in DEVPROPKEY key, out uint type, byte[]? buffer, uint size, out uint required, uint flags);

    /// <summary>现在连着的、报了电量的蓝牙设备（名字、0..100）。没有就是空的。</summary>
    public static List<(string Name, int Percent)> Read()
    {
        var found = new List<(string Name, int Percent)>();
        foreach (var enumerator in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE" })
        {
            var set = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == Invalid || set == IntPtr.Zero) continue;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    var level = Get(set, ref data, Battery);
                    if (level is not { Length: >= 1 } || level[0] > 100) continue;
                    // 配对过但没连着的也还在列表里，电量是上次的：跳过
                    if (Get(set, ref data, Connected) is { Length: >= 1 } connected && connected[0] == 0) continue;
                    var raw = Get(set, ref data, FriendlyName);
                    string name = raw == null ? L.T("蓝牙耳机", "Bluetooth headphones") : ProfileSuffix.Replace(System.Text.Encoding.Unicode.GetString(raw).TrimEnd('\0').Trim(), "");
                    if (found.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                    found.Add((name.Length > 0 ? name : L.T("蓝牙耳机", "Bluetooth headphones"), level[0]));
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        return found;
    }

    static byte[]? Get(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        SetupDiGetDevicePropertyW(set, ref data, key, out _, null, 0, out uint size, 0);
        if (size == 0 || size > 4096) return null;
        var buffer = new byte[size];
        return SetupDiGetDevicePropertyW(set, ref data, key, out _, buffer, size, out _, 0) ? buffer : null;
    }
}
