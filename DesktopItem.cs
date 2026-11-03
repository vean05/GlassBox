namespace GlassBox;

internal sealed class DesktopItem
{
    static readonly HashSet<string> HiddenExtensions = new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".appref-ms" };

    public required string Path { get; init; }
    public required string Name { get; init; }
    public bool IsDir { get; init; }
    public string Ext { get; init; } = "";
    public DateTime Modified { get; init; }
    /// <summary>出现在这里的时间（复制、下载进来的文件就是那一刻）。</summary>
    public DateTime Created { get; init; }
    /// <summary>不在桌面文件夹里、是从别处拖进盒子的引用。</summary>
    public bool External { get; init; }

    /// <summary>跟资源管理器一样，快捷方式不显示扩展名。</summary>
    public bool HidesExtension => !IsDir && HiddenExtensions.Contains(Ext);

    public static DesktopItem From(FileSystemInfo info, bool external = false)
    {
        bool dir = (info.Attributes & FileAttributes.Directory) != 0;
        string ext = dir ? "" : info.Extension.ToLowerInvariant();
        return new DesktopItem
        {
            Path = info.FullName,
            IsDir = dir,
            Ext = ext,
            Modified = info.LastWriteTime,
            Created = info.CreationTime,
            External = external,
            Name = !dir && HiddenExtensions.Contains(ext) ? System.IO.Path.GetFileNameWithoutExtension(info.Name) : info.Name,
        };
    }

    public static DesktopItem? FromPath(string path, bool external)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists ? From(info, external) : null;
    }
}

/// <summary>按类型分类的规则，对应默认的几个盒子。</summary>
internal static class Rules
{
    public static readonly (string Key, string Name)[] Categories =
    {
        ("shortcut", "快捷方式"),
        ("folder", "文件夹"),
        ("document", "文档"),
        ("installer", "程序和安装包"),
        ("archive", "压缩包"),
        ("media", "图片影音"),
        ("other", "其他"),
    };

    static readonly Dictionary<string, string> ByExtension = Build(new()
    {
        ["shortcut"] = ".lnk .url .appref-ms",
        ["document"] = ".pdf .doc .docx .rtf .txt .md .odt .wps .xls .xlsx .xlsm .csv .ods .et .ppt .pptx .odp .dps .html .htm .drawio .xmind",
        ["installer"] = ".exe .msi .msix .msixbundle .appx .appxbundle .jar .bat .cmd .ps1",
        ["archive"] = ".zip .rar .7z .tar .gz .tgz .bz2 .xz .iso .cab",
        ["media"] = ".png .jpg .jpeg .gif .bmp .webp .svg .ico .heic .tif .tiff .mp4 .mkv .mov .avi .wmv .flv .webm .mp3 .wav .flac .ogg .m4a .aac",
    });

    static Dictionary<string, string> Build(Dictionary<string, string> groups)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, list) in groups)
            foreach (var ext in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                map[ext] = category;
        return map;
    }

    public static string Categorize(DesktopItem item) =>
        item.IsDir ? "folder" : ByExtension.GetValueOrDefault(item.Ext, "other");

    public static readonly HashSet<string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase) { ".crdownload", ".part", ".partial", ".download", ".opdownload" };

    /// <summary>浏览器还没下载完的临时文件。</summary>
    public static bool IsPartialDownload(DesktopItem item) => !item.IsDir && PartialExtensions.Contains(item.Ext);

    public static string NameOf(string key) => Categories.FirstOrDefault(c => c.Key == key).Name ?? key;
}
