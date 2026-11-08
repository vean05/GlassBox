using System.ComponentModel;
using System.Diagnostics;
using static GlassBox.Native;

namespace GlassBox;

/// <summary>
/// 程序主体：扫描桌面、把文件分到各个分类、监视变化、托盘菜单。
/// 只读取桌面文件，不移动不删除；分类里放的是“引用”，布局存在 %APPDATA%\GlassBox\config.json。
/// 两种显示方式共用同一份数据：流光胶囊（每个分类是胶囊里的一页），或者桌面上的玻璃盒子。
/// </summary>
internal sealed class GlassBoxApp : ApplicationContext
{
    static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    static readonly Comparer<string> Logical = Comparer<string>.Create(StrCmpLogicalW);

    readonly Control ui = new();
    readonly List<FenceWindow> fences = new();
    readonly Dictionary<string, DesktopItem> items = new(PathComparer);
    readonly List<FileSystemWatcher> watchers = new();
    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 800 };
    readonly System.Windows.Forms.Timer rescanTimer = new() { Interval = 400 };
    readonly System.Windows.Forms.Timer iconTimer = new() { Interval = 120 };
    readonly System.Windows.Forms.Timer settingsTimer = new() { Interval = 500 };
    readonly System.Windows.Forms.Timer downloadTimer = new() { Interval = 3000 };
    readonly System.Windows.Forms.Timer downloadsFolderTimer = new() { Interval = 400 };
    readonly System.Windows.Forms.Timer screenshotsTimer = new() { Interval = 700 }; // 截图工具先建文件再写，等它写完
    readonly string[] desktopDirs;
    readonly string? downloadsDir; // 浏览器默认的“下载”文件夹（Chrome 存这里）；跟桌面是同一个就不单独看
    List<DesktopItem>? recentDownloads, screenshots;
    readonly string? screenshotsDir; // 系统的“屏幕截图”文件夹（Win+Shift+S、Win+PrtSc 存这里）
    readonly HashSet<string> newShots = new(PathComparer);
    readonly List<DesktopItem> downloadPartials = new();
    readonly Dictionary<string, bool> fromInternet = new(PathComparer);
    readonly HashSet<string> finishing = new(PathComparer); // 下载文件夹里刚从 .crdownload 改名过来的
    DoubleCtrlHook? keyHook;
    MediaWatcher? media;
    readonly MenuHost menuHost = new();
    readonly NotifyIcon tray;
    CapsuleWindow? capsule;
    DesktopDoubleClickHook? hook;
    volatile bool iconsDirty;
    bool hidden, exiting;

    public AppConfig Config { get; }
    public Theme Theme { get; private set; } = Theme.Detect();
    public GlassTone Tone { get; }
    public IconCache Icons { get; }
    public Surface Surface { get; } = new();
    public IReadOnlyList<FenceWindow> Fences => fences;
    public string? DraggingPath { get; set; }
    bool CapsuleMode => Config.Layout != "fences";
    float UiScale => Icons.Size / 40f;

    public GlassBoxApp()
    {
        _ = ui.Handle; // 建好句柄，后台线程才能 BeginInvoke 回来
        desktopDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            }
            .Where(d => d.Length > 0 && Directory.Exists(d))
            .Distinct(PathComparer)
            .ToArray();
        downloadsDir = KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B")); // FOLDERID_Downloads
        screenshotsDir = KnownFolder(new Guid("B7BEDE81-DF94-4682-A7D8-57A52620B86F")); // FOLDERID_Screenshots
        if (screenshotsDir != null && (!Directory.Exists(screenshotsDir) || IsDesktopDir(screenshotsDir))) screenshotsDir = null;
        if (downloadsDir != null && (!Directory.Exists(downloadsDir) || IsDesktopDir(downloadsDir))) downloadsDir = null;

        int iconSize;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) iconSize = (int)MathF.Round(40 * g.DpiX / 96f);
        Icons = new IconCache(iconSize, () => iconsDirty = true, () => ui.BeginInvoke(new Action(OnIconsIdle)));
        Tone = new GlassTone(ui);

        saveTimer.Tick += (_, _) => SaveNow();
        rescanTimer.Tick += (_, _) => { rescanTimer.Stop(); Rescan(); };
        iconTimer.Tick += (_, _) => { if (iconsDirty) { iconsDirty = false; InvalidateAll(); } };
        settingsTimer.Tick += (_, _) => { settingsTimer.Stop(); RefreshTheme(); };
        downloadTimer.Tick += (_, _) => CheckDownloads();
        downloadsFolderTimer.Tick += (_, _) => { downloadsFolderTimer.Stop(); OnDownloadsFolderChanged(); };
        screenshotsTimer.Tick += (_, _) => { screenshotsTimer.Stop(); OnScreenshotsChanged(); };

        var loaded = ConfigStore.Load();
        Config = loaded ?? new AppConfig();
        if (Config.Language is not ("zh" or "en" or "auto")) Config.Language = loaded == null ? "auto" : "zh";
        L.Use(Config.Language);
        var scanned = Scan();
        if (loaded == null) CreateDefaultFences(scanned.Values);
        EnsureAnyFence();
        if (L.RenameDefaults(Config.Fences)) ScheduleSave();
        PruneUsage();
        Reconcile(scanned, sortByCategory: loaded == null || Config.AutoSortNewFiles);

        ShowViews();
        if (Config.TaskbarStyle != TaskbarStyle.Normal) ApplyTaskbarStyle(quiet: true, retries: 10);
        else TaskbarStyle.Revert(); // 上次被强制结束时底板的效果可能还留着（背景层 GlassTap 自己已经还原了）
        if (Config.HideSystemIcons) DesktopShell.SetIconsVisible(false);
        else if (DesktopShell.HidByUs) DesktopShell.SetIconsVisible(true); // 上次是被强制结束的、图标还藏着：还回来
        tray = CreateTray();
        EnableHook(Config.DoubleClickToHide);
        UpdateKeyHook();
        StartWatchers();
        Tone.Changed += OnToneChanged;
        Tone.Reload(); // 后台读壁纸，读好之前胶囊先用默认颜色
        menuHost.ExplorerRestarted += () => After(1500, ReattachAll);
        menuHost.SettingsChanged += () => { settingsTimer.Stop(); settingsTimer.Start(); };
        menuHost.ExitRequested += Exit;
        menuHost.MusicRequested += () => capsule?.ShowMusic();
        // 关机/注销时只保存布局，不还原系统图标：下次登录桌面直接是干净的，不会先闪一下满屏图标
        menuHost.SessionEnding += SaveNow;
        iconTimer.Start();
        CheckDownloads();
        if (loaded == null) SaveNow();
        After(4000, TrimMemory); // 胶囊模式下图标要展开才加载，不能只靠“图标加载完”那次整理内存
        After(2500, StartMedia); // 正在播放：不急，等启动高峰过了再开
        After(8000, () => ThreadPool.QueueUserWorkItem(_ => AutoStart.Repair())); // 程序文件夹挪过位置：开机任务跟着改
    }

    /// <summary>启动高峰过去后（或胶囊收起后）把用不到的内存页还给系统。</summary>
    internal static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    // ---------- 桌面文件 ----------

    Dictionary<string, DesktopItem> Scan()
    {
        var result = new Dictionary<string, DesktopItem>(PathComparer);
        foreach (var dir in desktopDirs)
        {
            try
            {
                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    // 跟资源管理器默认一样：不显示隐藏文件和系统文件（包括 Office 的 ~$ 临时文件）
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (info.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    result[info.FullName] = DesktopItem.From(info);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error(ex); }
        }
        return result;
    }

    bool IsInDesktopDir(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        return parent != null && IsDesktopDir(parent);
    }

    bool IsDesktopDir(string dir) =>
        desktopDirs.Any(d => PathComparer.Equals(Path.TrimEndingDirectorySeparator(d), Path.TrimEndingDirectorySeparator(dir)));

    static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var ptr) != 0) return null;
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr); }
        finally { CoTaskMemFree(ptr); }
    }

    /// <summary>把扫描结果和分类对上：去掉已经不在的，新出现的放进对应分类。</summary>
    void Reconcile(Dictionary<string, DesktopItem> scanned, bool sortByCategory)
    {
        foreach (var fence in Config.Fences)
            foreach (var path in fence.Items)
                if (!scanned.ContainsKey(path) && !IsInDesktopDir(path) && DesktopItem.FromPath(path, external: true) is { } external)
                    scanned[path] = external;

        var placed = new HashSet<string>(PathComparer);
        foreach (var fence in Config.Fences)
            fence.Items = fence.Items.Where(p => scanned.ContainsKey(p) && placed.Add(p)).ToList();

        foreach (var item in scanned.Values.Where(i => !placed.Contains(i.Path)).OrderBy(i => i.Name, Logical))
            TargetFor(item, sortByCategory).Items.Add(item.Path);

        items.Clear();
        foreach (var (path, item) in scanned) items[path] = item;
    }

    void Rescan()
    {
        if (exiting) return;
        var before = new HashSet<string>(items.Keys, PathComparer);
        Reconcile(Scan(), Config.AutoSortNewFiles);
        recentDownloads = null;
        screenshots = null;
        RefreshViews();
        ScheduleSave();

        // 新出现在桌面上的文件（保存、复制进来的）：胶囊上提示一下收进了哪个分类
        var added = items.Values.Where(i => !before.Contains(i.Path) && !i.External && !Rules.IsPartialDownload(i))
            .OrderByDescending(i => i.Created).ToList();
        if (added.Count > 0 && FenceOf(added[0].Path) is { } fence)
            capsule?.ShowFiled(added[0], fence, added.Count > 1 ? L.T($"{added.Count} 个新文件已收进", $"{added.Count} new files filed into") : L.T("新文件已收进", "New file filed into"));
        CheckDownloads();
    }

    void RenamePath(string oldPath, string newPath)
    {
        if (!items.Remove(oldPath, out var oldItem)) return;
        Icons.Forget(oldPath);
        var item = DesktopItem.FromPath(newPath, oldItem.External);
        if (item == null) return;
        items[newPath] = item;

        FenceConfig? into = null;
        foreach (var fence in Config.Fences)
        {
            int i = fence.Items.FindIndex(p => PathComparer.Equals(p, oldPath));
            if (i < 0) continue;
            // 比如下载完成 .crdownload → .pdf：类型变了就挪到对应的分类
            if (Config.AutoSortNewFiles && Rules.Categorize(oldItem) != Rules.Categorize(item))
            {
                fence.Items.RemoveAt(i);
                into = TargetFor(item, true);
                into.Items.Add(newPath);
            }
            else
            {
                fence.Items[i] = newPath;
                into = fence;
            }
            break;
        }
        recentDownloads = null;
        RefreshViews();
        ScheduleSave();

        if (Rules.IsPartialDownload(oldItem) && !Rules.IsPartialDownload(item) && into != null)
        {
            CheckDownloads();
            capsule?.ShowFiled(item, into, L.T("下载完成，已收进", "Downloaded, filed into"));
        }
    }

    /// <summary>桌面上有浏览器正在写的临时文件，就在胶囊上显示“正在下载”。</summary>
    void CheckDownloads()
    {
        var now = DateTime.Now;
        DesktopItem? active = null;
        bool any = false;
        _ = RecentDownloads; // 顺便更新“下载”文件夹里的半截文件
        foreach (var item in items.Values.Concat(downloadPartials))
        {
            // 很久以前留下的半截文件不算（比如取消了没删掉的）
            if (!Rules.IsPartialDownload(item) || now - item.Created > TimeSpan.FromHours(6)) continue;
            if (item.External && !downloadPartials.Contains(item)) continue;
            any = true;
            DateTime written;
            try { written = File.GetLastWriteTime(item.Path); } // 这个接口读到的是正在写的文件的最新时间
            catch (IOException) { continue; }
            if (now - written < TimeSpan.FromSeconds(60) && (active == null || item.Created > active.Created)) active = item;
        }
        capsule?.SetDownloading(active == null ? null : Path.GetFileNameWithoutExtension(active.Name));
        if (any) downloadTimer.Start();
        else downloadTimer.Stop();
    }

    void StartWatchers()
    {
        foreach (var dir in desktopDirs)
        {
            try
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
                };
                watcher.Created += (_, _) => RequestRescan();
                watcher.Deleted += (_, _) => RequestRescan();
                watcher.Changed += (_, _) => RequestRescan();
                watcher.Error += (_, _) => RequestRescan();
                watcher.Renamed += (_, e) => ui.BeginInvoke(new Action(() =>
                {
                    RenamePath(e.OldFullPath, e.FullPath);
                    RestartRescan();
                }));
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        if (screenshotsDir != null)
        {
            try
            {
                // “屏幕截图”文件夹也只看不收：给「图像」页用
                var watcher = new FileSystemWatcher(screenshotsDir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
                void Changed(string? created) => ui.BeginInvoke(new Action(() =>
                {
                    if (created != null) newShots.Add(created);
                    screenshotsTimer.Stop();
                    screenshotsTimer.Start();
                }));
                watcher.Created += (_, e) => Changed(e.FullPath);
                watcher.Renamed += (_, e) => Changed(e.FullPath);
                watcher.Deleted += (_, _) => Changed(null);
                watcher.Error += (_, _) => Changed(null);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        if (downloadsDir == null) return;
        try
        {
            // “下载”文件夹只看不收：不放进任何分类，只给“最近下载”和“正在下载”用
            var watcher = new FileSystemWatcher(downloadsDir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
            void Changed() => ui.BeginInvoke(new Action(() => { downloadsFolderTimer.Stop(); downloadsFolderTimer.Start(); }));
            watcher.Created += (_, _) => Changed();
            watcher.Deleted += (_, _) => Changed();
            watcher.Error += (_, _) => Changed();
            watcher.Renamed += (_, e) =>
            {
                if (Rules.PartialExtensions.Contains(Path.GetExtension(e.OldFullPath)) && !Rules.PartialExtensions.Contains(Path.GetExtension(e.FullPath)))
                    lock (finishing) finishing.Add(e.FullPath);
                Changed();
            };
            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    void OnDownloadsFolderChanged()
    {
        if (exiting) return;
        recentDownloads = null;
        capsule?.RefreshItems();
        CheckDownloads();
        string[] done;
        lock (finishing)
        {
            done = finishing.ToArray();
            finishing.Clear();
        }
        // 下载完成：胶囊上提示一下，点它直接看到这个文件
        foreach (var path in done)
            if (RecentDownloads.FirstOrDefault(d => PathComparer.Equals(d.Path, path)) is { } item)
                capsule?.ShowDownloaded(item);
    }

    void OnScreenshotsChanged()
    {
        if (exiting) return;
        screenshots = null;
        capsule?.RefreshItems();
        var fresh = newShots.ToArray();
        newShots.Clear();
        if (!Config.ScreenshotPage) return;
        // 刚截的图：胶囊上提示一下，点它直接看到这张
        var shot = Screenshots.Where(s => fresh.Contains(s.Path, PathComparer)).OrderByDescending(s => s.Created).FirstOrDefault();
        if (shot != null) capsule?.ShowScreenshot(shot);
    }

    void RequestRescan() => ui.BeginInvoke(new Action(RestartRescan));

    void RestartRescan()
    {
        if (exiting) return;
        rescanTimer.Stop();
        rescanTimer.Start();
    }

    public bool TryGetItem(string path, out DesktopItem item) => items.TryGetValue(path, out item!);

    // ---------- 给胶囊用的查询 ----------

    public IEnumerable<DesktopItem> AllItems => items.Values;
    public int TotalCount => items.Count;
    public int TodayCount => items.Values.Count(i => !i.External && i.Created.Date == DateTime.Today);

    public List<DesktopItem> ItemsOf(FenceConfig fence)
    {
        var list = new List<DesktopItem>(fence.Items.Count);
        foreach (var path in fence.Items)
            if (items.TryGetValue(path, out var item)) list.Add(item);
        return list;
    }

    public int CountOf(FenceConfig fence) => fence.Items.Count;

    // ---------- 最近下载 ----------

    const int RecentDays = 30, RecentMax = 60;

    /// <summary>
    /// 最近 30 天下载的东西，新的在前：桌面上带“来自网络”标记的文件（Edge 下到桌面），
    /// 加上“下载”文件夹里的（Chrome 下到这里）。都只是引用，文件留在原地。
    /// </summary>
    public IReadOnlyList<DesktopItem> RecentDownloads => recentDownloads ??= FindRecentDownloads();

    List<DesktopItem> FindRecentDownloads()
    {
        var since = DateTime.Now.AddDays(-RecentDays);
        var list = new List<DesktopItem>();
        foreach (var item in items.Values)
            if (!item.IsDir && !item.External && item.Created >= since && !Rules.IsPartialDownload(item) && FromInternet(item.Path))
                list.Add(item);

        downloadPartials.Clear();
        if (downloadsDir != null)
        {
            try
            {
                foreach (var info in new DirectoryInfo(downloadsDir).EnumerateFileSystemInfos())
                {
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || info.CreationTime < since) continue;
                    var item = items.TryGetValue(info.FullName, out var known) ? known : DesktopItem.From(info, external: true);
                    if (Rules.IsPartialDownload(item)) downloadPartials.Add(item);
                    else list.Add(item);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error(ex); }
        }
        list.Sort((a, b) => b.Created.CompareTo(a.Created));
        if (list.Count > RecentMax) list.RemoveRange(RecentMax, list.Count - RecentMax);
        return list;
    }

    // ---------- 截图 ----------

    const int ShotsMax = 200;
    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".jxr" };
    static readonly System.Text.RegularExpressions.Regex ShotName =
        new(@"(?i)screenshot|screen shot|屏幕截图|屏幕快照|截图|截屏|snipaste|snip|微信图片|qq截图|wechat");

    /// <summary>
    /// 「图像」页：系统“屏幕截图”文件夹里的图，加上桌面上名字像截图的图（Snipaste、微信、QQ 截图），新的在前。
    /// 都只是引用，文件留在原地。
    /// </summary>
    public IReadOnlyList<DesktopItem> Screenshots => screenshots ??= FindScreenshots();

    List<DesktopItem> FindScreenshots()
    {
        var list = new List<DesktopItem>();
        foreach (var item in items.Values)
            if (!item.IsDir && !item.External && ImageExtensions.Contains(Path.GetExtension(item.Path)) && ShotName.IsMatch(item.Name))
                list.Add(item);
        if (screenshotsDir != null)
        {
            try
            {
                foreach (var info in new DirectoryInfo(screenshotsDir).EnumerateFiles())
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && ImageExtensions.Contains(info.Extension))
                        list.Add(DesktopItem.From(info, external: true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error(ex); }
        }
        list.Sort((a, b) => b.Created.CompareTo(a.Created));
        if (list.Count > ShotsMax) list.RemoveRange(ShotsMax, list.Count - ShotsMax);
        return list;
    }

    /// <summary>
    /// 浏览器下载的文件带一个 Zone.Identifier 数据流（“此文件来自其他计算机”的那个标记），里面记着下载网址。
    /// 从 zip 里解压出来的文件也会带这个标记，但记的是 zip 的本地路径，不算下载。
    /// </summary>
    bool FromInternet(string path)
    {
        if (fromInternet.TryGetValue(path, out bool known)) return known;
        if (fromInternet.Count > 4000) fromInternet.Clear(); // 只是个缓存，别让它一直长
        bool marked;
        try
        {
            // 有的下载只记了 ZoneId（没有网址）也算；只把“没有 HostUrl、ReferrerUrl 是本地 zip 路径”的排除掉
            string zone = File.ReadAllText(path + ":Zone.Identifier");
            var referrer = System.Text.RegularExpressions.Regex.Match(zone, @"(?im)^ReferrerUrl=(.*)$");
            bool unzipped = !zone.Contains("HostUrl=", StringComparison.OrdinalIgnoreCase) && referrer.Success
                            && !referrer.Groups[1].Value.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase);
            marked = !unzipped;
        }
        catch (Exception) { marked = false; }
        return fromInternet[path] = marked;
    }

    // ---------- 常用 ----------

    const int UsageMax = 400;

    /// <summary>
    /// “常用”的打开记录不让它一直长：文件已经不在、而且两个月没打开过的忘掉；
    /// 总数超过 400 条只留分数最高的。启动时做一次。
    /// </summary>
    void PruneUsage()
    {
        var stale = DateTime.Now.AddDays(-60);
        foreach (var (path, use) in Config.Usage.ToList())
            if (use.Last < stale && !File.Exists(path) && !Directory.Exists(path)) Config.Usage.Remove(path);
        if (Config.Usage.Count > UsageMax)
            foreach (var path in Config.Usage.Keys.OrderByDescending(UseScore).Skip(UsageMax).ToList())
                Config.Usage.Remove(path);
    }

    /// <summary>打开得越多、越近越靠前；两周前打开的分量减半。</summary>
    public double UseScore(string path) =>
        Config.Usage.TryGetValue(path, out var use) ? use.Count / (1 + (DateTime.Now - use.Last).TotalDays / 14) : 0;

    public List<DesktopItem> FrequentItems(int max) => Config.Usage
        .Select(pair => (Item: Lookup(pair.Key), Score: UseScore(pair.Key)))
        .Where(x => x.Item != null)
        .OrderByDescending(x => x.Score)
        .Take(max)
        .Select(x => x.Item!)
        .ToList();

    DesktopItem? Lookup(string path) =>
        items.TryGetValue(path, out var item) ? item
        : RecentDownloads.FirstOrDefault(d => PathComparer.Equals(d.Path, path)) ?? Screenshots.FirstOrDefault(d => PathComparer.Equals(d.Path, path));

    void RecordUse(string path)
    {
        var use = Config.Usage.GetValueOrDefault(path) ?? new UsageEntry();
        use.Count++;
        use.Last = DateTime.Now;
        Config.Usage[path] = use;
        if (Config.Usage.Count > 300) // 只留分数最高的一批，配置文件别越存越大
            foreach (var stale in Config.Usage.Keys.OrderBy(UseScore).Take(Config.Usage.Count - 240).ToList())
                Config.Usage.Remove(stale);
        ScheduleSave();
    }

    public FenceConfig? FenceOf(string path) => Config.Fences.FirstOrDefault(f => f.Items.Contains(path, PathComparer));

    /// <summary>这个文件拖进来会被收进哪个分类（显示用）。</summary>
    public string CategoryNameFor(string path)
    {
        var item = items.TryGetValue(path, out var known) ? known : DesktopItem.FromPath(path, external: true);
        return item == null ? L.T("对应分类", "its category") : TargetFor(item, true).Name;
    }

    /// <summary>拖到胶囊上的文件：按类型收进对应分类（或者指定的分类）。只记引用，不动文件。</summary>
    public List<(DesktopItem Item, FenceConfig Fence)> Absorb(string[] paths, FenceConfig? into)
    {
        var placed = new List<(DesktopItem, FenceConfig)>();
        foreach (var path in paths)
        {
            if (!items.TryGetValue(path, out var item))
            {
                var found = DesktopItem.FromPath(path, external: !IsInDesktopDir(path));
                if (found == null) continue;
                item = found;
                items[path] = item;
            }
            var target = into ?? TargetFor(item, true);
            MoveItem(path, target, -1, refresh: false);
            placed.Add((item, target));
        }
        RefreshViews();
        ScheduleSave();
        return placed;
    }

    // ---------- 分类 / 盒子 ----------

    void CreateDefaultFences(IEnumerable<DesktopItem> all)
    {
        var used = all.Select(Rules.Categorize).ToHashSet();
        used.Add("other");
        var categories = Rules.Categories.Where(c => used.Contains(c.Key)).ToList();

        var area = Screen.PrimaryScreen!.WorkingArea;
        float s = UiScale;
        int rows = 3, gap = (int)(14 * s), margin = (int)(18 * s), w = (int)(340 * s);
        int h = Math.Clamp((area.Height - margin * 2 - gap * (rows - 1)) / rows, (int)(170 * s), (int)(380 * s));
        for (int i = 0; i < categories.Count; i++)
        {
            Config.Fences.Add(new FenceConfig
            {
                Name = categories[i].Name,
                Category = categories[i].Key,
                X = area.Left + margin + i / rows * (w + gap),
                Y = area.Top + margin + i % rows * (h + gap),
                W = w,
                H = h,
            });
        }
    }

    void EnsureAnyFence()
    {
        if (Config.Fences.Count > 0) return;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Config.Fences.Add(new FenceConfig
        {
            Name = L.T("桌面", "Desktop"), Category = "other",
            X = area.Left + 24, Y = area.Top + 24, W = (int)(360 * UiScale), H = (int)(300 * UiScale),
        });
    }

    FenceConfig TargetFor(DesktopItem item, bool byCategory)
    {
        if (byCategory)
        {
            string category = Rules.Categorize(item);
            var match = Config.Fences.FirstOrDefault(f => f.Category == category);
            if (match != null) return match;
        }
        return Config.Fences.FirstOrDefault(f => f.Category == "other") ?? Config.Fences[0];
    }

    /// <summary>按当前的显示方式建窗口：胶囊，或者每个分类一个盒子。</summary>
    void ShowViews()
    {
        if (CapsuleMode)
        {
            capsule = new CapsuleWindow(this);
            if (!hidden)
            {
                capsule.Show();
                capsule.SendToBottom();
            }
        }
        else
            foreach (var fence in Config.Fences) AddFenceWindow(fence);
    }

    void CloseViews()
    {
        foreach (var fence in fences) fence.Dispose();
        fences.Clear();
        capsule?.Dispose();
        capsule = null;
    }

    void SetLayout(string layout)
    {
        if (Config.Layout == layout) return;
        Config.Layout = layout;
        CloseViews();
        ShowViews();
        CheckDownloads();
        ScheduleSave();
    }

    void RefreshViews()
    {
        foreach (var fence in fences) fence.RefreshItems();
        capsule?.RefreshItems();
    }

    FenceWindow AddFenceWindow(FenceConfig config)
    {
        // 换了显示器/分辨率后盒子可能跑到屏幕外，拉回来
        var bounds = new Rectangle(config.X, config.Y, config.W, Math.Max(40, config.H));
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            config.X = area.Left + 24;
            config.Y = area.Top + 24;
        }
        var fence = new FenceWindow(this, config);
        fences.Add(fence);
        fence.RefreshItems();
        if (!hidden)
        {
            fence.Show();
            fence.SendToBottom();
        }
        return fence;
    }

    public void MoveItem(string path, FenceConfig target, int index)
    {
        // 从「图像」「最近下载」拖过来的不在桌面上（截图文件夹、下载文件夹），先记成外部引用，
        // 不然分类里多了一条路径、数字也加了，却因为不认识这个文件而不显示
        if (!items.ContainsKey(path) && DesktopItem.FromPath(path, external: !IsInDesktopDir(path)) is { } item) items[path] = item;
        MoveItem(path, target, index, refresh: true);
    }

    void MoveItem(string path, FenceConfig target, int index, bool refresh)
    {
        foreach (var fence in Config.Fences)
        {
            int i = fence.Items.FindIndex(p => PathComparer.Equals(p, path));
            if (i < 0) continue;
            fence.Items.RemoveAt(i); // index 已经是“拿掉它之后”的序号，不用再调整
            break;
        }
        if (index < 0 || index > target.Items.Count) index = target.Items.Count;
        target.Items.Insert(index, path);
        if (!refresh) return;
        RefreshViews();
        ScheduleSave();
    }

    public void AddPaths(string[] paths, FenceConfig target, int index)
    {
        foreach (var path in paths)
        {
            if (!items.ContainsKey(path))
            {
                var item = DesktopItem.FromPath(path, external: !IsInDesktopDir(path));
                if (item == null) continue;
                items[path] = item;
            }
            MoveItem(path, target, index);
            index = target.Items.FindIndex(p => PathComparer.Equals(p, path)) + 1;
        }
    }

    void RemoveReference(string path)
    {
        foreach (var fence in Config.Fences) fence.Items.RemoveAll(p => PathComparer.Equals(p, path));
        items.Remove(path);
        RefreshViews();
        ScheduleSave();
    }

    void NewFence(Point at)
    {
        var area = Screen.FromPoint(at).WorkingArea;
        int w = (int)(320 * UiScale), h = (int)(240 * UiScale);
        var config = new FenceConfig
        {
            Name = CapsuleMode ? L.T("新建分类", "New category") : L.T("新建盒子", "New box"),
            X = Math.Clamp(at.X - w / 2, area.Left, area.Right - w),
            Y = Math.Clamp(at.Y - (int)(16 * UiScale), area.Top, area.Bottom - h),
            W = w,
            H = h,
        };
        Config.Fences.Add(config);
        if (!CapsuleMode) AddFenceWindow(config);
        RefreshViews();
        ScheduleSave();
        RenameFence(config, at);
    }

    void RenameFence(FenceConfig fence, Point near)
    {
        var name = InputDialog.Show(CapsuleMode ? L.T("重命名分类", "Rename category") : L.T("重命名盒子", "Rename box"), fence.Name, near);
        if (string.IsNullOrWhiteSpace(name)) return;
        fence.Name = name;
        RefreshViews();
        ScheduleSave();
    }

    void DeleteFence(FenceConfig fence)
    {
        var target = Config.Fences.FirstOrDefault(f => f != fence && f.Category == "other") ?? Config.Fences.FirstOrDefault(f => f != fence);
        if (target == null) return;
        string kind = CapsuleMode ? L.T("分类", "category") : L.T("盒子", "box");
        var answer = MessageBox.Show(L.T($"删除{kind}“{fence.Name}”？\n\n里面的文件不会被删除，会移到“{target.Name}”。", $"Delete {kind} “{fence.Name}”?\n\nThe files inside won't be deleted, they'll move to “{target.Name}”."),
            L.T("玻璃收纳", "GlassBox"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;

        target.Items.AddRange(fence.Items);
        if (fence.Category == "other") target.Category ??= "other";
        Config.Fences.Remove(fence);
        var window = fences.FirstOrDefault(f => f.Config == fence);
        if (window != null)
        {
            fences.Remove(window);
            window.Dispose();
        }
        RefreshViews();
        ScheduleSave();
    }

    void SortFence(FenceConfig fence, string by)
    {
        var list = ItemsOf(fence);
        IEnumerable<DesktopItem> sorted = by switch
        {
            "type" => list.OrderBy(i => i.IsDir ? 0 : 1).ThenBy(i => i.Ext).ThenBy(i => i.Name, Logical),
            "date" => list.OrderByDescending(i => i.Modified),
            _ => list.OrderBy(i => i.IsDir ? 0 : 1).ThenBy(i => i.Name, Logical),
        };
        fence.Items = sorted.Select(i => i.Path).ToList();
        RefreshViews();
        ScheduleSave();
    }

    void SetCategory(FenceConfig fence, string? category)
    {
        if (category != null)
            foreach (var other in Config.Fences)
                if (other.Category == category) other.Category = null;
        fence.Category = category;
        ScheduleSave();
    }

    void ReorganizeAll()
    {
        var answer = MessageBox.Show(L.T("把所有文件按类型重新分到对应的分类？\n\n你手动调整过的位置会被覆盖（文件本身不会动）。", "Re-sort every file into categories by type?\n\nYour manual arrangement will be replaced (the files themselves won't move)."),
            L.T("玻璃收纳", "GlassBox"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;
        foreach (var fence in Config.Fences) fence.Items.Clear();
        foreach (var item in items.Values.OrderBy(i => i.Name, Logical))
            TargetFor(item, true).Items.Add(item.Path);
        RefreshViews();
        ScheduleSave();
    }

    void ReattachAll()
    {
        // explorer 重启后桌面窗口是新的：重新挂上；被连带销毁的窗口重建
        for (int i = 0; i < fences.Count; i++)
        {
            var fence = fences[i];
            if (fence.IsDisposed || !fence.IsHandleCreated)
            {
                if (!fence.IsDisposed) fence.Dispose();
                var replacement = new FenceWindow(this, fence.Config);
                fences[i] = replacement;
                replacement.RefreshItems();
                if (!hidden)
                {
                    replacement.Show();
                    replacement.SendToBottom();
                }
            }
            else
            {
                fence.AttachToDesktop();
                fence.SendToBottom();
            }
        }
        if (capsule != null)
        {
            if (capsule.IsDisposed || !capsule.IsHandleCreated)
            {
                if (!capsule.IsDisposed) capsule.Dispose();
                capsule = null;
                ShowViews();
            }
            else
            {
                capsule.AttachToDesktop();
                capsule.SendToBottom();
            }
        }
        if (Config.HideSystemIcons) DesktopShell.SetIconsVisible(false);
        if (Config.TaskbarStyle != TaskbarStyle.Normal) ApplyTaskbarStyle(quiet: true, retries: 10); // 新的 explorer 里没有 GlassTap，重新载入
    }

    public void SetHidden(bool value)
    {
        hidden = value;
        foreach (var fence in fences)
        {
            if (value) fence.Hide();
            else
            {
                fence.Show();
                fence.SendToBottom();
            }
        }
        if (capsule != null)
        {
            if (value) capsule.Hide();
            else
            {
                capsule.Show();
                capsule.SendToBottom();
            }
        }
    }

    // ---------- 打开 / 菜单 ----------

    public void Open(DesktopItem item)
    {
        RecordUse(item.Path);
        try
        {
            Process.Start(new ProcessStartInfo(item.Path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(item.Path) ?? "",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { } // 用户取消了 UAC
        catch (Exception ex)
        {
            MessageBox.Show(L.T($"打不开“{item.Name}”：\n{ex.Message}", $"Couldn't open “{item.Name}”:\n{ex.Message}"), L.T("玻璃收纳", "GlassBox"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void ShowItemMenu(FenceConfig? fence, DesktopItem item, Point at)
    {
        ShellMenu.Show(menuHost, item.Path, at, menu =>
        {
            // “下载”文件夹里的文件还不在任何分类里：这里就是“收进分类”
            var move = menu.Sub(fence == null ? L.T("收进分类", "File into category") : CapsuleMode ? L.T("移到分类", "Move to category") : L.T("移动到盒子", "Move to box"), Config.Fences.Count > (fence == null ? 0 : 1));
            foreach (var other in Config.Fences.Where(f => f != fence).ToList())
                move.Add(other.Name, () => AddPaths(new[] { item.Path }, other, -1));
            menu.Add(L.T("打开文件所在位置", "Open file location"), () => Process.Start("explorer.exe", $"/select,\"{item.Path}\""));
            if (item.External && fence != null) menu.Add(CapsuleMode ? L.T("从收纳中移除（不删除文件）", "Remove from GlassBox (file is kept)") : L.T("从盒子中移除（不删除文件）", "Remove from box (file is kept)"), () => RemoveReference(item.Path));
        }, verb =>
        {
            if (!verb.Equals("rename", StringComparison.OrdinalIgnoreCase)) return false;
            RenameItem(item, at); // 外壳的“重命名”要靠资源管理器窗口，这里自己做
            return true;
        });
    }

    void RenameItem(DesktopItem item, Point at)
    {
        string fileName = Path.GetFileName(item.Path);
        string shown = item.HidesExtension ? Path.GetFileNameWithoutExtension(fileName) : fileName;
        int stem = item.IsDir || item.HidesExtension ? -1 : Path.GetFileNameWithoutExtension(fileName).Length;
        var name = InputDialog.Show(L.T("重命名", "Rename"), shown, at, stem);
        if (string.IsNullOrWhiteSpace(name) || name == shown) return;
        if (item.HidesExtension) name += item.Ext;

        string target = Path.Combine(Path.GetDirectoryName(item.Path)!, name);
        try
        {
            if (item.IsDir) Directory.Move(item.Path, target);
            else File.Move(item.Path, target);
            RenamePath(item.Path, target);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.T($"重命名失败：\n{ex.Message}", $"Rename failed:\n{ex.Message}"), L.T("玻璃收纳", "GlassBox"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void ShowFenceMenu(FenceWindow window, Point at)
    {
        using var menu = new PopupMenu();
        menu.Add(L.T("新建盒子", "New box"), () => NewFence(at));
        AddFenceItems(menu, window.Config, at);
        menu.Add(window.Config.Collapsed ? L.T("展开", "Expand") : L.T("折叠", "Collapse"), window.ToggleCollapse);
        menu.Add(L.T("删除盒子…", "Delete box…"), () => DeleteFence(window.Config), enabled: Config.Fences.Count > 1);
        menu.Separator();
        AddCommonItems(menu, at);
        menu.Show(at, menuHost.Handle);
    }

    /// <summary>胶囊上的右键菜单；在某个分类上点的话，带上这个分类的操作。</summary>
    /// <summary>胶囊自己的菜单（音乐卡片右键）也挂在这个隐藏窗口上，点外面才会消失。</summary>
    public IntPtr MenuOwner => menuHost.Handle;

    public void ShowCapsuleMenu(FenceConfig? fence, Point at)
    {
        using var menu = new PopupMenu();
        menu.Add(L.T("新建分类", "New category"), () => NewFence(at));
        if (fence != null)
        {
            menu.Separator();
            AddFenceItems(menu, fence, at);
            menu.Add(L.T($"删除分类“{fence.Name}”…", $"Delete category “{fence.Name}”…"), () => DeleteFence(fence), enabled: Config.Fences.Count > 1);
        }
        menu.Separator();
        AddCommonItems(menu, at);
        menu.Show(at, menuHost.Handle);
    }

    void AddFenceItems(PopupMenu menu, FenceConfig fence, Point at)
    {
        menu.Add(CapsuleMode ? L.T($"重命名分类“{fence.Name}”…", $"Rename category “{fence.Name}”…") : L.T("重命名盒子…", "Rename box…"), () => RenameFence(fence, at));
        var sort = menu.Sub(L.T("排序", "Sort"), fence.Items.Count > 1);
        sort.Add(L.T("按名称", "By name"), () => SortFence(fence, "name"));
        sort.Add(L.T("按类型", "By type"), () => SortFence(fence, "type"));
        sort.Add(L.T("按修改时间", "By date modified"), () => SortFence(fence, "date"));
        var auto = menu.Sub(L.T("新文件自动收纳到这里", "Auto-file new files here"));
        auto.Add(L.T("不自动收纳", "Don't auto-file"), () => SetCategory(fence, null), check: fence.Category == null);
        foreach (var (key, name) in Rules.Categories)
            auto.Add(name, () => SetCategory(fence, key), check: fence.Category == key);
    }

    void AddCommonItems(PopupMenu menu, Point at)
    {
        string what = CapsuleMode ? L.T("胶囊", "capsule") : L.T("所有盒子", "all boxes");
        menu.Add(hidden ? L.T($"显示{what}", $"Show {what}") : L.T($"隐藏{what}", $"Hide {what}"), () => SetHidden(!hidden));
        menu.Add(L.T("按类型重新整理全部…", "Re-sort everything by type…"), ReorganizeAll);
        var layout = menu.Sub(L.T("显示方式", "Layout"));
        layout.Add(L.T("流光胶囊（屏幕顶部）", "Glow capsule (top of screen)"), () => SetLayout("capsule"), check: CapsuleMode);
        layout.Add(L.T("玻璃盒子（摆在桌面上）", "Glass boxes (on the desktop)"), () => SetLayout("fences"), check: !CapsuleMode);
        var settings = menu.Sub(L.T("设置", "Settings"));
        settings.Add(L.T("开机自动启动", "Start with Windows"), ToggleAutoStart, check: AutoStart.Enabled);
        var language = settings.Sub("语言 / Language");
        language.Add("中文", () => SetLanguage("zh"), check: !L.En);
        language.Add("English", () => SetLanguage("en"), check: L.En);
        var taskbar = settings.Sub(L.T("Windows 任务栏效果", "Windows taskbar effect"));
        taskbar.Add(L.T("原样", "Normal"), () => SetTaskbarStyle(TaskbarStyle.Normal), check: Config.TaskbarStyle == TaskbarStyle.Normal);
        taskbar.Add(L.T("透明", "Clear"), () => SetTaskbarStyle(TaskbarStyle.Clear), check: Config.TaskbarStyle == TaskbarStyle.Clear);
        taskbar.Add(L.T("磨砂", "Frosted"), () => SetTaskbarStyle(TaskbarStyle.Frost), check: Config.TaskbarStyle == TaskbarStyle.Frost);
        if (CapsuleMode) settings.Add(L.T("顶部胶囊自动隐藏", "Auto-hide top capsule"), () => SetCapsuleAutoHide(!Config.CapsuleAutoHide), check: Config.CapsuleAutoHide);
        settings.Add(L.T("双击桌面空白处隐藏/显示", "Double-click desktop to hide/show"), () => SetDoubleClickToHide(!Config.DoubleClickToHide), check: Config.DoubleClickToHide);
        if (CapsuleMode)
            settings.Add(L.T("连按两下 Ctrl 呼出胶囊", "Ctrl twice to summon the capsule"), () => SetDoubleCtrlToSummon(!Config.DoubleCtrlToSummon), check: Config.DoubleCtrlToSummon);
        if (!CapsuleMode)
            settings.Add(L.T("鼠标悬停时展开折叠的盒子", "Expand collapsed boxes on hover"), () =>
            {
                Config.HoverExpand = !Config.HoverExpand;
                ScheduleSave();
            }, check: Config.HoverExpand);
        settings.Add(L.T("新文件自动按类型收纳", "Auto-file new files by type"), () =>
        {
            Config.AutoSortNewFiles = !Config.AutoSortNewFiles;
            ScheduleSave();
        }, check: Config.AutoSortNewFiles);
        settings.Add(L.T("隐藏系统桌面图标", "Hide system desktop icons"), () => SetHideSystemIcons(!Config.HideSystemIcons), check: Config.HideSystemIcons);
        settings.Add(L.T("在桌面按 Ctrl+V 粘贴文件和图片", "Ctrl+V on desktop pastes files & images"), () => SetDesktopPaste(!Config.DesktopPaste), check: Config.DesktopPaste);
        if (!CapsuleMode)
            settings.Add(L.T("毛玻璃兼容模式（效果不对时再开）", "Glass compatibility mode (if blur looks wrong)"), () =>
            {
                Config.GlassMode = Config.GlassMode == "accent" ? "acrylic" : "accent";
                foreach (var fence in fences) fence.ApplyTheme();
                ScheduleSave();
            }, check: Config.GlassMode == "accent");
        settings.Separator();
        settings.Add(L.T("打开配置文件夹", "Open config folder"), () => Process.Start("explorer.exe", $"\"{ConfigStore.Dir}\""));
        settings.Add(L.T("卸载玻璃收纳…", "Uninstall GlassBox…"), () => Process.Start(Environment.ProcessPath!, "--uninstall"));
        menu.Separator();
        menu.Add(L.T("退出", "Exit"), Exit);
    }

    internal void ToggleAutoStart()
    {
        try { AutoStart.Enabled = !AutoStart.Enabled; }
        catch (Exception ex) { MessageBox.Show(L.T($"设置开机启动失败：\n{ex.Message}", $"Couldn't set start with Windows:\n{ex.Message}"), L.T("玻璃收纳", "GlassBox")); }
    }

    // 胶囊里的设置卡片用：跟托盘菜单里的开关是同一套
    internal void SetDoubleClickToHide(bool on)
    {
        Config.DoubleClickToHide = on;
        EnableHook(on);
        ScheduleSave();
    }

    internal void SetDoubleCtrlToSummon(bool on)
    {
        Config.DoubleCtrlToSummon = on;
        UpdateKeyHook();
        ScheduleSave();
    }

    internal void SetHideSystemIcons(bool on)
    {
        Config.HideSystemIcons = on;
        DesktopShell.SetIconsVisible(!on);
        ScheduleSave();
    }

    internal void SetCapsuleAutoHide(bool on)
    {
        Config.CapsuleAutoHide = on;
        capsule?.ApplyAutoHide();
        ScheduleSave();
    }

    internal void SetTaskbarStyle(int style)
    {
        if (Config.TaskbarStyle == style) return;
        Config.TaskbarStyle = style;
        ScheduleSave();
        ApplyTaskbarStyle();
    }

    /// <summary>
    /// 任务栏透明 / 磨砂：第一次要把 GlassTap 载进 explorer，可能等一两秒，放到后台做。
    /// 开机或 explorer 刚重启时（quiet）任务栏可能还没建好：每 3 秒悄悄再试，最多 30 秒，
    /// 不弹窗、不改设置（下次 explorer 重启还会再试）。只有你自己在设置里点的才会退回原样并说明原因。
    /// </summary>
    void ApplyTaskbarStyle(bool quiet = false, int retries = 0)
    {
        int style = Config.TaskbarStyle;
        Task.Run(() => TaskbarStyle.Apply(style)).ContinueWith(task =>
        {
            string? error = task.IsFaulted ? task.Exception!.GetBaseException().Message : task.Result;
            if (error == null || exiting || Config.TaskbarStyle != style) return;
            if (retries > 0)
            {
                After(3000, () => ApplyTaskbarStyle(quiet, retries - 1));
                return;
            }
            if (quiet)
            {
                Log.Info("任务栏效果没能打开：" + error);
                return;
            }
            Config.TaskbarStyle = TaskbarStyle.Normal;
            ScheduleSave();
            capsule?.Redraw();
            MessageBox.Show(L.T($"任务栏效果没能打开：\n{error}", $"Couldn't turn on the taskbar effect:\n{error}"), L.T("玻璃收纳", "GlassBox"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    static string TrayText => L.T("玻璃收纳（双击隐藏/显示）", "GlassBox (double-click to hide/show)");

    /// <summary>切换界面语言：自带的分类名跟着换，胶囊、托盘马上重画。</summary>
    internal void SetLanguage(string language)
    {
        Config.Language = language;
        L.Use(language);
        L.RenameDefaults(Config.Fences);
        tray.Text = TrayText;
        capsule?.LanguageChanged();
        RefreshViews();
        ScheduleSave();
    }

    NotifyIcon CreateTray()
    {
        var icon = new NotifyIcon { Icon = AppIcon.Create(), Text = TrayText, Visible = true };
        icon.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            using var menu = new PopupMenu();
            menu.Add(CapsuleMode ? L.T("新建分类", "New category") : L.T("新建盒子", "New box"), () => NewFence(Cursor.Position));
            AddCommonItems(menu, Cursor.Position);
            menu.Show(Cursor.Position, menuHost.Handle);
        };
        icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) SetHidden(!hidden); };
        return icon;
    }

    void EnableHook(bool on)
    {
        if (on && hook == null)
        {
            hook = new DesktopDoubleClickHook();
            hook.DoubleClick += () => ui.BeginInvoke(new Action(() => SetHidden(!hidden)));
        }
        else if (!on && hook != null)
        {
            hook.Dispose();
            hook = null;
        }
    }

    /// <summary>键盘钩子：连按两下 Ctrl、桌面粘贴，两个都关了就拆掉。</summary>
    void UpdateKeyHook()
    {
        bool summon = Config.DoubleCtrlToSummon, paste = Config.DesktopPaste;
        if ((summon || paste) && keyHook == null)
        {
            keyHook = new DoubleCtrlHook();
            // 正在玩全屏游戏、放幻灯片时不响应：游戏里也常按 Ctrl，抢走焦点会把游戏切出去
            keyHook.Triggered += () => ui.BeginInvoke(new Action(() => { if (!FullscreenBusy()) capsule?.Summon(); }));
            keyHook.Paste += () => ui.BeginInvoke(new Action(() => PasteFromClipboard(null)));
        }
        else if (!summon && !paste && keyHook != null)
        {
            keyHook.Dispose();
            keyHook = null;
        }
        if (keyHook != null)
        {
            keyHook.Summon = summon;
            keyHook.PasteHere = paste ? DesktopPasteWanted : null;
        }
    }

    internal void SetDesktopPaste(bool on)
    {
        Config.DesktopPaste = on;
        UpdateKeyHook();
        ScheduleSave();
    }

    // ---------- 粘贴到桌面 ----------

    const uint CF_BITMAP = 2, CF_DIB = 8, CF_HDROP = 15, CF_DIBV5 = 17;
    static readonly uint CF_PNG = RegisterClipboardFormat("PNG");

    /// <summary>
    /// 在钩子线程上调，要快：焦点在桌面上、剪贴板里有文件或图片时才接手。
    /// 系统图标显示着时，文件让资源管理器自己粘贴；图片资源管理器本来就粘贴不了，交给我们。
    /// </summary>
    static bool DesktopPasteWanted()
    {
        if (!DesktopShell.IsDesktopBackground(GetForegroundWindow())) return false;
        if (IsClipboardFormatAvailable(CF_HDROP)) return DesktopShell.IconsVisible() != true;
        return IsClipboardFormatAvailable(CF_DIB) || IsClipboardFormatAvailable(CF_DIBV5) || IsClipboardFormatAvailable(CF_BITMAP)
            || (CF_PNG != 0 && IsClipboardFormatAvailable(CF_PNG));
    }

    /// <summary>剪贴板里有能放到桌面的东西（文件、图片）吗。界面线程上调。</summary>
    internal static bool ClipboardHasPasteable()
    {
        try { return Clipboard.ContainsFileDropList() || (!Clipboard.ContainsText() && (Clipboard.ContainsImage() || Clipboard.ContainsData("PNG"))); }
        catch { return false; }
    }

    /// <summary>
    /// 把剪贴板里的文件复制一份、或者把图片存成 PNG，放到桌面上；into 不为空时收进那个分类（不然按类型自动收）。
    /// 只新建文件：重名就加“ - 副本”，绝不覆盖。剪切（Ctrl+X）也当复制，原来的文件不动。
    /// </summary>
    internal bool PasteFromClipboard(FenceConfig? into)
    {
        IDataObject? data;
        try { data = Clipboard.GetDataObject(); }
        catch (Exception ex) { Log.Error(ex); return false; }
        return data != null && PasteData(data, into, desktopDirs[0]);
    }

    internal bool PasteData(IDataObject data, FenceConfig? into, string desk)
    {

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            // 大文件、文件夹可能要复制一会儿：放后台，界面不卡
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var made = new List<string>();
                foreach (var source in files)
                {
                    try { if (CopyToFolder(source, desk) is { } path) made.Add(path); }
                    catch (Exception ex) { Log.Error(ex); }
                }
                Log.Info($"粘贴到桌面：{made.Count}/{files.Length} 个");
                if (made.Count > 0 && into != null) ui.BeginInvoke(new Action(() => FileInto(made, into)));
            });
            return true;
        }

        Image? image = null;
        try
        {
            // 有 PNG 格式就用它（透明背景不会变黑），不然用普通位图
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png) image = Image.FromStream(png);
            else if (data.GetDataPresent(DataFormats.Bitmap, true)) image = data.GetData(DataFormats.Bitmap, true) as Image;
        }
        catch (Exception ex) { Log.Error(ex); }
        if (image == null) return false;
        using (image)
        {
            string path = Unique(Path.Combine(desk, $"{L.T("粘贴的图片", "Pasted image")} {DateTime.Now:yyyy-MM-dd HHmmss}.png"));
            try { image.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
            catch (Exception ex) { Log.Error(ex); return false; }
            Log.Info("粘贴图片到桌面");
            if (into != null) FileInto(new List<string> { path }, into);
        }
        return true;
    }

    void FileInto(List<string> paths, FenceConfig into)
    {
        if (exiting || !Config.Fences.Contains(into)) return;
        var placed = Absorb(paths.ToArray(), into);
        if (placed.Count > 0)
            capsule?.ShowFiled(placed[0].Item, into, placed.Count > 1 ? L.T($"收纳了 {placed.Count} 个文件", $"Filed {placed.Count} items into") : L.T("已收进", "Filed into"));
    }

    static string? CopyToFolder(string source, string folder)
    {
        string name = Path.GetFileName(source.TrimEnd('\\'));
        if (name.Length == 0) return null;
        if (Directory.Exists(source))
        {
            string from = Path.GetFullPath(source).TrimEnd('\\') + "\\", into = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            if (into.StartsWith(from, StringComparison.OrdinalIgnoreCase)) return null; // 不能把文件夹复制进它自己里面
            string target = Unique(Path.Combine(folder, name));
            CopyDirectory(source, target);
            return target;
        }
        if (!File.Exists(source)) return null;
        string file = Unique(Path.Combine(folder, name));
        File.Copy(source, file, overwrite: false);
        return file;
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(from))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue; // 不跟进链接，免得绕圈
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    /// <summary>重名时像资源管理器一样加“ - 副本”“ - 副本 (2)”。</summary>
    static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string ext = Directory.Exists(path) ? "" : Path.GetExtension(path);
        string stem = Path.GetFileName(path)[..^ext.Length];
        string copy = L.T(" - 副本", " - Copy");
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, stem + copy + (i > 1 ? $" ({i})" : "") + ext);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    // ---------- 正在播放 ----------

    public MediaWatcher? Media => media;

    void StartMedia()
    {
        if (exiting || media != null) return;
        media = new MediaWatcher();
        media.Changed += playing => ui.BeginInvoke(new Action(() => capsule?.SetMedia(playing)));
        media.Start();
    }

    // ---------- 杂项 ----------

    public void InvalidateAll()
    {
        foreach (var fence in fences) fence.Invalidate();
        capsule?.Redraw();
    }

    void OnIconsIdle()
    {
        iconsDirty = false;
        InvalidateAll();
        After(1000, TrimMemory);
    }

    void OnToneChanged()
    {
        capsule?.RefreshItems(); // 字的深浅可能变了，重新排一下
        foreach (var fence in fences) fence.Invalidate();
    }

    void RefreshTheme()
    {
        Tone.Reload(); // 壁纸或分辨率可能变了（没变的话什么都不做）
        capsule?.Redraw();
        if (Config.TaskbarStyle == TaskbarStyle.Frost) ApplyTaskbarStyle(quiet: true); // 磨砂的底色跟着 Windows 深浅色
        var theme = Theme.Detect();
        if (theme.Dark == Theme.Dark) return;
        Theme = theme;
        foreach (var fence in fences) fence.ApplyTheme();
    }

    static void After(int ms, Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = ms };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            action();
        };
        timer.Start();
    }

    public void ScheduleSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    void SaveNow()
    {
        saveTimer.Stop();
        try { ConfigStore.Save(Config); }
        catch (Exception ex) { Log.Error(ex); }
    }

    void Exit()
    {
        exiting = true;
        SaveNow();
        EnableHook(false);
        keyHook?.Dispose();
        keyHook = null;
        media?.Dispose();
        downloadsFolderTimer.Dispose();
        foreach (var watcher in watchers) watcher.Dispose();
        DesktopShell.SetIconsVisible(true); // 退出后把系统桌面图标还原
        if (Config.TaskbarStyle != TaskbarStyle.Normal) TaskbarStyle.Revert(); // 不发也会还原（GlassTap 盯着这个进程），发了更快
        tray.Visible = false;
        tray.Dispose();
        downloadTimer.Dispose();
        CloseViews();
        Tone.Dispose();
        Icons.Dispose();
        menuHost.Dispose();
        Surface.Dispose();
        ExitThread();
    }
}
