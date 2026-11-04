using System.Globalization;

namespace GlassBox;

/// <summary>
/// 界面语言：中文 / English。每处界面文字写成 L.T("中文", "English")，画的时候才挑，切换后下一帧就变。
/// 日志、缓存格式、匹配网页用的文字不翻译。
/// </summary>
internal static class L
{
    public static volatile bool En;

    public static string T(string zh, string en) => En ? en : zh;

    /// <summary>"zh" / "en"；"auto"（新装）跟 Windows 的显示语言走。</summary>
    public static void Use(string? language) =>
        En = language == "en" || (language != "zh" && !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 自带的分类名（建默认分类时写进了配置）：只有名字跟某个自带名字一模一样的才跟着换语言，自己改过名的不碰。
    /// 有改动返回 true。
    /// </summary>
    public static bool RenameDefaults(IEnumerable<FenceConfig> fences)
    {
        bool changed = false;
        foreach (var fence in fences)
            foreach (var (zh, en) in DefaultNames)
                if (fence.Name == (En ? zh : en)) { fence.Name = En ? en : zh; changed = true; break; }
        return changed;
    }

    public static readonly (string Zh, string En)[] DefaultNames =
    {
        ("快捷方式", "Shortcuts"),
        ("文件夹", "Folders"),
        ("文档", "Documents"),
        ("程序和安装包", "Apps & installers"),
        ("压缩包", "Archives"),
        ("图片影音", "Media"),
        ("其他", "Other"),
        ("桌面", "Desktop"),
        ("新建盒子", "New box"),
        ("新建分类", "New category"),
    };

    /// <summary>歌词来源（缓存里记的是中文名）。</summary>
    public static string Source(string source) => En && source == "网易云" ? "NetEase" : source;
}
