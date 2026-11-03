using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace GlassBox;

internal sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public List<FenceConfig> Fences { get; set; } = new();
    public bool DoubleClickToHide { get; set; } = true;
    public bool HoverExpand { get; set; } = true;
    public bool AutoSortNewFiles { get; set; } = true;
    /// <summary>胶囊里的「图像」页：所有截图（系统“屏幕截图”文件夹 + 桌面上的截图）自动出现在这里，文件留在原地。</summary>
    public bool ScreenshotPage { get; set; } = true;
    /// <summary>「图像」排在第几个分类的位置（0 = 所有分类前面），跟分类一起拖着调顺序。</summary>
    public int ScreenshotSlot { get; set; }
    public bool HideSystemIcons { get; set; } = true;
    /// <summary>面板里把图标拖到右下角的“口”上（或选中按 Delete）：胶囊一口吞掉，丢进回收站，5 秒内可以撤销。文件夹不行。默认关。</summary>
    public bool TrashDrop { get; set; }
    public string GlassMode { get; set; } = "acrylic"; // acrylic：系统亚克力；accent：兼容模式
    /// <summary>capsule：顶部的流光胶囊；fences：桌面上的玻璃盒子。每个盒子在胶囊里就是一个分类。</summary>
    public string Layout { get; set; } = "capsule";
    /// <summary>连按两下 Ctrl，在任何窗口上面呼出胶囊。</summary>
    public bool DoubleCtrlToSummon { get; set; } = true;
    /// <summary>胶囊玻璃的透明度 0..100：50 是默认的样子，越大越透、越小越像磨砂实心。</summary>
    public int GlassClarity { get; set; } = 50;
    /// <summary>悬停放大：0 关、1 小、2 中、3 大。</summary>
    public int HoverZoom { get; set; } = 2;
    /// <summary>胶囊透出正在放的歌的封面颜色。</summary>
    public bool SongColor { get; set; } = true;
    /// <summary>收起时胶囊上显示正在播放的歌名。</summary>
    public bool ShowNowPlaying { get; set; } = true;
    /// <summary>在放歌时鼠标停到胶囊上：打开音乐卡片（大封面、进度、歌词），而不是文件面板。</summary>
    public bool MusicCard { get; set; } = true;
    /// <summary>收起时封面像唱片一样转、旁边的音柱跟着声音跳（只在放歌、胶囊露在外面时动）。</summary>
    public bool MusicMotion { get; set; } = true;
    /// <summary>音乐卡片旁边显示同步歌词：联网查 lrclib.net，只发歌名、歌手和时长。默认关。</summary>
    public bool Lyrics { get; set; }
    /// <summary>顶部胶囊自动隐藏：平时缩到屏幕顶上，鼠标碰到顶边才滑下来，浮在所有窗口上面。</summary>
    public bool CapsuleAutoHide { get; set; }
    /// <summary>Windows 任务栏的样子：0 原样，1 透明，2 磨砂（见 TaskbarStyle）。</summary>
    public int TaskbarStyle { get; set; }
    /// <summary>胶囊展开后的宽度和最大高度（按 100% 缩放的像素，屏幕放不下时自动缩小）。</summary>
    public int OpenWidth { get; set; } = 780;
    public int OpenHeight { get; set; } = 490;
    /// <summary>胶囊离屏幕顶边多远。</summary>
    public int CapsuleTopGap { get; set; } = 12;
    /// <summary>怎么展开：0 悬停快、1 悬停（默认）、2 悬停慢、3 只有点击才展开。</summary>
    public int HoverOpen { get; set; } = 1;
    /// <summary>鼠标离开后多快收起：0 快、1 默认、2 慢。</summary>
    public int CloseSpeed { get; set; } = 1;
    /// <summary>每个文件打开过几次、最后一次什么时候，给“常用”和搜索排序用。</summary>
    public Dictionary<string, UsageEntry> Usage { get; set; } = new();
}

internal sealed class UsageEntry
{
    public int Count { get; set; }
    public DateTime Last { get; set; }
}

internal sealed class FenceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新建盒子";
    /// <summary>新文件按类型自动收纳到这个盒子；null 表示不自动收纳。</summary>
    public string? Category { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Collapsed { get; set; }
    public List<string> Items { get; set; } = new();
}

[JsonSerializable(typeof(AppConfig))]
internal partial class ConfigJson : JsonSerializerContext;

internal static class ConfigStore
{
    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassBox");
    static string FilePath => Path.Combine(Dir, "config.json");

    static readonly ConfigJson Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    });

    public static AppConfig? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), Json.AppConfig);
        }
        catch (Exception ex)
        {
            // 配置坏了就留一份备份再重新开始，别把用户的布局悄悄覆盖掉
            Log.Error(ex);
            File.Copy(FilePath, Path.Combine(Dir, $"config.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
            return null;
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Dir);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Json.AppConfig));
        File.Move(temp, FilePath, true);
    }
}

internal static class Log
{
    public static void Error(Exception? ex) => Info($"{ex}");

    public static void Info(string message)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.Dir);
            string path = Path.Combine(ConfigStore.Dir, "glassbox.log");
            // 超过 1MB 就换一份新的，旧的留一份 glassbox.old.log，日志不会一直长下去
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 1024 * 1024) File.Move(path, Path.Combine(ConfigStore.Dir, "glassbox.old.log"), true);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
        }
        catch { }
    }
}
