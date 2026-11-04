using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using static GlassBox.Native;

namespace GlassBox;

/// <summary>
/// 流光胶囊：屏幕顶部正中的一颗玻璃胶囊，平时只显示今天新增了几个文件、一共多少个。
/// 鼠标靠近时它会微微探向鼠标，停上去就像液体一样展开成面板（分类、图标、搜索），离开又缩回去。
/// 文件拖到胶囊上会按类型自动收纳；桌面上有文件在下载、下载完成，胶囊也会提示。
/// 用分层窗口自己画玻璃（底下是模糊过的壁纸），不靠 DWM，形状和变形动画才能随意控制。
/// 跟盒子一样贴在桌面层、不抢焦点；只有展开后点进去才接收键盘输入。
/// </summary>
internal sealed class CapsuleWindow : Form
{
    enum Mode { Idle, Open, Receive, Notify, Music }
    enum Seg { Dot, Text, Bold, Muted, Sep, Chip, Bar, Note, Wave, Battery, Ring, Count, Lyric }
    enum Pending { None, Open, Close, EndNote }
    enum PageKind { Frequent, Downloads, Screenshots, Fence }

    struct Spring { public float X, V; }

    /// <summary>面板上的一页：前两页“常用”“最近下载”是自动生成的，后面每个分类一页。</summary>
    readonly record struct Page(PageKind Kind, string Name, FenceConfig? Fence, int Count);

    // 尺寸都按 96 DPI 写，画的时候乘 S
    const float Rim = 30;      // 胶囊四周留给阴影和涟漪的边
    const float CellW = 92, CellH = 104, PanelChrome = 150;
    const int FrequentRows = 5;
    int FrequentMax => FrequentRows * Columns(OpenW);

    static readonly StringFormat Tight = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
    };
    static readonly StringFormat LabelFormat = new(StringFormatFlags.LineLimit)
    {
        Alignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
    };
    static readonly StringFormat CenterFormat = new(StringFormatFlags.NoWrap)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
    };

    readonly GlassBoxApp app;
    readonly Surface surface = new();
    readonly System.Windows.Forms.Timer frame = new() { Interval = 15 };
    readonly System.Windows.Forms.Timer watch = new() { Interval = 250 };   // 看鼠标离胶囊多远
    readonly System.Windows.Forms.Timer later = new();                      // 悬停展开、离开收起、提醒结束
    readonly System.Windows.Forms.Timer caret = new() { Interval = 530 };
    readonly System.Windows.Forms.Timer trim = new() { Interval = 15000 };
    readonly Bitmap measureBitmap = new(1, 1);
    readonly Graphics measure;
    readonly bool animate = AnimationsEnabled();
    readonly List<(Seg Kind, string Text, float Width, float Gap)> pill = new();
    readonly List<DesktopItem> shown = new();
    readonly List<RectangleF> chipRects = new();

    Spring sw = new() { X = 228 }, sh = new() { X = 40 }, sr = new() { X = 20 }, sx, sy;
    long lastFrame;
    bool dirty;
    Mode mode;
    Pending pending;
    float prox;
    PointF lean;
    Point cursor = new(-9999, -9999);
    bool cursorOverDesktop;
    RectangleF capsuleOnScreen;

    long lastSample = -100000;
    float pillWidth;
    string pillKey = "";
    long pillSwap = -1, ripple = -1, freshSince = -1;
    string? freshPath;

    // 提醒
    string noteLead = "", noteName = "";
    FenceConfig? noteFence;
    string? notePath, downloadName;
    string[] dropFiles = Array.Empty<string>();
    PageKind? notePage; // 提示点进去是“最近下载”“图像”这种自动页（null 就是 noteFence 那个分类）

    // 面板
    readonly List<Page> pages = new();
    int tab = -1, hover = -1, hoverChip = -1, selected = -1, pressed = -1, dropChip = -1, columns = 1;
    string query = "";
    bool focused, caretOn, menuOpen, dragging, hoverSearch, keyboardPicked, chipFollow;
    float scroll, scrollTarget, gridLeft, chipScroll, chipRowWidth;
    RectangleF searchRect, gridRect, chipRow;

    // 分类那一排：按住拖 = 左右滚；在分类上按住不动一下（0.35 秒）再拖 = 调顺序（排得下不用滚时直接拖就行）
    readonly System.Windows.Forms.Timer chipHold = new() { Interval = 350 };
    bool chipPressed, chipScrolling;
    int chipPress = -1, chipLift = -1, chipInsert = -1;
    Point chipPressAt;
    float chipPressScroll, chipLiftX, chipGrabDx;
    long chipPressSince = -1, chipLiftSince = -1;

    // 丢进回收站：先从面板上拿掉，5 秒后才真的放进回收站，这期间可以撤销
    readonly HashSet<string> trashing = new(StringComparer.OrdinalIgnoreCase); // 不显示的：等着丢的 + 刚丢完、等重新扫描的
    readonly List<string> trashQueue = new();
    readonly System.Windows.Forms.Timer trashTimer = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer mediaGap = new() { Interval = 4000 }; // 切歌时播放器短暂报“没在放”：等这么久还是没有才收起
    DesktopItem? trashItem;
    FenceConfig? trashFence;
    PageKind? trashPage;
    RectangleF trashFrom, trashUndoRect;
    long trashSince = -1, trashToastUntil;
    string trashToast = "";
    bool trashHot, trashKicked, noteUndo;

    // 拖动排序：别的图标滑开让位，松手的地方空出一格
    int dropIndex = -1;
    // 在面板里拖：图标自己画、跟着鼠标走（每帧都画，不用系统拖放那张一卡一卡的拖动图）；拖出胶囊才交给系统拖放
    string? liftPath;
    PointF liftGrab;
    long liftSince = -1;
    readonly Dictionary<string, PointF> tilePos = new(StringComparer.OrdinalIgnoreCase);
    bool layoutMoving;
    float frameDt = 1 / 60f;

    // 悬停放大：鼠标附近的图标像程序坞一样放大，旁边的轻轻让开
    static readonly float[] ZoomLevels = { 0, .12f, .22f, .32f };
    float ZoomMax => ZoomLevels[Math.Clamp(app.Config.HoverZoom, 0, ZoomLevels.Length - 1)];
    float ZoomT(float zoom) => ZoomMax > 0 ? Math.Clamp((zoom - 1) / ZoomMax, 0, 1) : 0;
    readonly Dictionary<string, float> tileZoom = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(int Index, RectangleF Rect, float Zoom)> tileDraws = new();

    // 跟随歌曲变色：从封面取主色，胶囊里透出一团这个颜色的光；换歌时慢慢过渡，暂停时淡掉
    const int AccentMs = 900;
    Color accentFrom = Color.White, accentTo = Color.White;
    float accentAmountFrom, accentAmountTo;
    long accentSince = -1;
    Bitmap? accentArt;
    Color? accentArtColor;

    // 空格预览
    DesktopItem? previewItem;
    Bitmap? previewImage;
    string? previewText;
    string previewInfo = "";
    long previewSince = -1;
    int previewToken;

    // 设置卡片：搜索框右边的齿轮打开，改了马上生效
    bool settingsOpen, autoStartOn;
    long settingsSince = -1;
    RectangleF gearRect, settingsCard;
    string? sliderDrag;
    int settingsTab;
    readonly Dictionary<string, RectangleF> sliderTracks = new();
    readonly List<(RectangleF Rect, string Key, int Value)> settingHits = new();

    // 自动隐藏：平时缩到屏幕顶上只露一条边，鼠标在顶边中间停一下才滑下来，浮在所有窗口上面
    bool revealed, fullscreenBusy;
    long edgeSince = -1, leaveSince = -1, lastBusyCheck;
    bool Floating => app.Config.CapsuleAutoHide;
    /// <summary>现在该缩上去：平时没被叫出来；全屏游戏/视频时连通知也不往下滑（除非 Ctrl 呼出）。</summary>
    bool Tucked => Floating && (mode == Mode.Idle && !revealed || fullscreenBusy && mode != Mode.Open);

    // 连按两下 Ctrl 呼出：临时浮到所有窗口上面，收起后再沉回桌面
    bool summoned, sinkPending;
    IntPtr previousForeground;

    // 背后的样子：被窗口挡住又露出来时要重新拍
    bool uncovered;
    long uncoveredSince = -1;

    // 正在播放
    NowPlaying? media;
    readonly RectangleF[] mediaButtons = new RectangleF[3];
    RectangleF playerInfo; // 面板底下迷你播放器的封面和歌名：点它打开音乐卡片
    long playerDrawn;
    Point pressPoint;
    string clockShown = "";
    Font pillFont = null!, boldFont = null!, chipFont = null!, smallFont = null!, labelFont = null!, searchFont = null!, clockFont = null!;
    Font titleFont = null!, lyricFont = null!, lyricNextFont = null!;

    // 音乐卡片：左边大封面，中间歌名、进度、按钮，右边歌词，底下一排音量、定时、切换来源
    readonly List<(RectangleF Rect, string Key, int Value)> musicHits = new();
    RectangleF seekTrack, volumeTrack;
    bool seekDrag, volumeDrag, panelLast, musicLast;
    float seekValue, volume = -1;
    bool volumePerApp;
    long volumeRead;
    int sleepStep;
    SongLyrics? lyrics;
    string lyricsKey = "";
    bool lyricsLoading, lyricsOffline;
    CancellationTokenSource? lyricsCancel;
    int lyricIndex = -1;
    long lyricSince = -1, musicHoldUntil;
    readonly float[] barLevels = new float[4];
    float peakAvg;
    (string, string) prettyFor;
    (string Title, string Artist) pretty;
    readonly Dictionary<(string, Font, int), string> fitCache = new();

    // 锁住音乐卡片：鼠标走开也不收起（去看一眼文件面板，收起时也回到音乐卡片）。
    // 双击卡片、点右上角的小锁、或者放歌时连按两下 Ctrl（开了的话）；锁着时卡片边上有一圈慢慢呼吸的光
    bool musicPinned, musicRaised;
    long lastCardClick = -1;
    Point lastCardClickAt;
    RectangleF lockRect;
    float drift; // 只在放歌时往前走的时钟（秒）：背景漂、锁住的光呼吸，暂停时停在原地不跳

    // 在卡片上滚滚轮调音量：封面上浮出一个大数字和一圈进度
    long volumeShownSince = -1;
    const int VolumeShowMs = 1100;
    Font volumeFont = null!;

    // 现在从哪个设备出声（耳机还是音箱），和读音量一起两秒读一次
    Audio.Output? output;
    // 模糊封面背景：卡片和桌面小播放器各一张（大小不一样）
    readonly Backdrop cardBackdrop = new(), floatBackdrop = new();
    // 画在自己的深色背景上时（模糊封面、桌面小播放器）：字一律用白的
    bool lightInk;
    bool DarkInk => app.Tone.DarkInk && !lightInk;

    // 拖出来的小播放器：在卡片空白处按住往下拖
    MusicFloat? floatWin;
    bool cardPressed, detaching;
    Point cardPressAt, detachGrab;
    readonly List<(RectangleF Rect, string Key, int Value)> floatHits = new();

    // YouTube 广告：“跳过”按钮出来时胶囊上提示，点一下就跳
    AdSkipper? ads;   // 老办法：从外面读网页（YouTube 要是当前标签页、窗口没最小化）
    AdBridge? adBridge; // 自带的 Edge 扩展来报（后台标签页也行）
    bool adBridgeFailed, adFromUia, adFromExt, adOutdatedShown;
    bool adSkippable, adNote, adNoteChip;
    string adNoteText = "";

    // 蓝牙耳机电量：卡片开着时一分钟读一次（后台线程，读系统里已有的值）
    List<(string Name, int Percent)> btLevels = new();
    long btRead = -100000;
    bool btReading;

    // 歌词：每句的字形（按字号缓存，换句的那几帧字号在变才现做）
    readonly Dictionary<(int Line, int Size, int Width), GraphicsPath> lyricPaths = new();
    SongLyrics? lyricPathsFor;
    int lyricRollFrom = -1;

    // 收起时滚歌词：当前那句，换句时旧的往上退、新的从下面浮上来
    int pillLyricIndex = -1;
    string pillLyric = "", pillLyricPrev = "";
    long pillLyricSince = -1;

    // 充电：插上充电器那一下，电池一格格涨上去、一道绿光从左扫到右
    PowerLineStatus lastPower = SystemInformation.PowerStatus.PowerLineStatus;
    bool chargeNote;
    float chargePercent;
    long chargeSince = -1;
    const int ChargeMs = 1500;

    // 番茄钟：专注 N 分钟 → 休息 5 分钟。专注时胶囊只显示倒计时，提示先攒着，唱片和音柱停下来
    readonly System.Windows.Forms.Timer focusTick = new() { Interval = 1000 };
    DateTime? focusEnd; // UTC
    TimeSpan focusLength;
    bool focusBreak;
    int focusMissed;
    RectangleF focusRect;
    const int BreakMinutes = 5;
    /// <summary>正在专注（不算休息）：安静模式。</summary>
    bool Focusing => focusEnd != null && !focusBreak;

    public CapsuleWindow(GlassBoxApp app)
    {
        this.app = app;
        measure = Graphics.FromImage(measureBitmap);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        AllowDrop = true;
        Text = L.T("玻璃收纳", "GlassBox");
        var area = Screen.PrimaryScreen!.WorkingArea;
        Bounds = new Rectangle(area.Left + area.Width / 2 - 150, area.Top, 300, 100); // 先放到主屏上，DPI 才按主屏算
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        CreateFonts();
        frame.Tick += (_, _) => OnFrame();
        watch.Tick += (_, _) => OnWatch();
        later.Tick += (_, _) => OnLater();
        chipHold.Tick += (_, _) =>
        {
            chipHold.Stop();
            if (chipPressed && !chipScrolling && chipPress >= FixedPages && chipLift < 0 && mode == Mode.Open) BeginChipLift(PointToClient(Cursor.Position).X);
        };
        trashTimer.Tick += (_, _) => CommitTrash();
        mediaGap.Tick += (_, _) =>
        {
            mediaGap.Stop();
            SetMedia(app.Media?.Current, settled: true);
        };
        focusTick.Tick += (_, _) => OnFocusTick();
        caret.Tick += (_, _) => { caretOn = !caretOn; Redraw(); };
        trim.Tick += (_, _) =>
        {
            trim.Stop();
            if (mode != Mode.Idle || Animating) return;
            surface.Trim(); // 展开时的大画布用完就还
            cardBackdrop.Clear(); // 模糊封面背景也是（几 MB），下次打开卡片再做
            GlassBoxApp.TrimMemory();
        };
        media = app.Media?.Current;
        BuildPages();
        BuildPill();
        watch.Start();
    }

    float S => DeviceDpi / 96f;
    // 设置里可以调的尺寸和手感（都按 96 DPI）
    float TopGap => Math.Clamp(app.Config.CapsuleTopGap, 0, 80);       // 胶囊离工作区顶部
    float OpenW => Math.Min(Math.Clamp(app.Config.OpenWidth, 560, 1400), Area.Width / S - 24);
    float OpenMaxH => Math.Clamp(app.Config.OpenHeight, 300, 900);
    int HoverOpenMs => app.Config.HoverOpen switch { 0 => 40, 2 => 350, _ => 120 }; // 3 = 只有点击才展开
    int CloseMs => app.Config.CloseSpeed switch { 0 => 180, 2 => 900, _ => 380 };
    float Px(float v) => v * S;
    static Rectangle Area => Screen.PrimaryScreen!.WorkingArea;
    Page? CurrentPage => tab >= 0 && tab < pages.Count ? pages[tab] : null;
    FenceConfig? CurrentFence => query.Length == 0 ? CurrentPage?.Fence : null;
    /// <summary>当前这页能不能拖动排序：分类页可以，“常用”“最近下载”和搜索结果是自动排的。</summary>
    bool CanReorder => CurrentFence != null;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = WS_POPUP;
            cp.ExStyle = (cp.ExStyle & ~WS_EX_APPWINDOW) | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            cp.Parent = DesktopShell.Host;
            return cp;
        }
    }

    static bool AnimationsEnabled() => !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out int on, 0) || on != 0;

    // ---------- 对外 ----------

    public void AttachToDesktop()
    {
        var host = DesktopShell.Host;
        if (host != IntPtr.Zero) SetWindowLongPtr(Handle, GWLP_HWNDPARENT, host);
    }

    /// <summary>平时钉在桌面层最底下；开了自动隐藏就一直浮在最上面。</summary>
    public void SendToBottom() => SetWindowPos(Handle, Floating ? HWND_TOPMOST : HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>自动隐藏的开关变了。</summary>
    public void ApplyAutoHide()
    {
        revealed = false;
        edgeSince = leaveSince = -1;
        if (IsHandleCreated) SendToBottom();
        Kick();
    }

    /// <summary>文件或分类变了。</summary>
    public void RefreshItems()
    {
        BuildPages();
        if (mode == Mode.Open) RefreshShown(keepScroll: true);
        BuildPill();
        Redraw();
    }

    /// <summary>重画（图标加载完、玻璃颜色变了、屏幕变了都走这里）。</summary>
    public void Redraw()
    {
        dirty = true;
        Kick();
    }

    /// <summary>一个文件被收进了某个分类：胶囊上提示一下，点它可以直接看到这个文件。</summary>
    public void ShowFiled(DesktopItem item, FenceConfig fence, string lead)
    {
        Notify(item, fence, lead, page: null);
    }

    /// <summary>“下载”文件夹里有东西下载完了。</summary>
    public void ShowDownloaded(DesktopItem item) => Notify(item, null, L.T("下载完成", "Downloaded"), PageKind.Downloads);

    /// <summary>刚截了一张图（进了「图像」页）。</summary>
    public void ShowScreenshot(DesktopItem item)
    {
        // 开了“截图飞进胶囊”：先定格、飞进来，到了胶囊“咽”一下再提示
        if (app.Config.ShotFly && animate && !Focusing && Visible && IsHandleCreated && !fullscreenBusy && mode is not (Mode.Open or Mode.Music))
        {
            ShotFlight.Launch(this, item.Path, () => capsuleOnScreen, () =>
            {
                sh.V -= 120;
                sw.V += 90;
                Notify(item, null, L.T("截图已收进", "Screenshot saved"), PageKind.Screenshots);
                Kick();
            }, () => Notify(item, null, L.T("截图已收进", "Screenshot saved"), PageKind.Screenshots));
            return;
        }
        Notify(item, null, L.T("截图已收进", "Screenshot saved"), PageKind.Screenshots);
    }

    /// <param name="urgent">用户自己刚做的事（比如丢进回收站后的“撤销”）：专注时也要显示。</param>
    void Notify(DesktopItem item, FenceConfig? fence, string lead, PageKind? page, bool urgent = false)
    {
        freshPath = item.Path;
        freshSince = Now;
        if (mode == Mode.Music) return; // 正在看音乐卡片：不打断，展开面板时这个文件会亮一下
        if (Focusing && !urgent && mode != Mode.Open)
        {
            focusMissed++; // 专注时不弹：结束时一起告诉你收进了几个
            return;
        }
        chargeNote = adNote = false;
        if (mode == Mode.Open)
        {
            if (query.Length == 0) tab = Math.Max(0, page is { } kind ? IndexOfKind(kind) : IndexOfFence(fence));
            RefreshShown(keepScroll: false);
            Redraw();
            return;
        }
        noteLead = lead;
        noteUndo = false;
        noteName = item.Name;
        noteFence = fence;
        notePage = page;
        notePath = item.Path;
        downloadName = null;
        SetMode(Mode.Notify);
        ripple = Now;
        Schedule(Pending.EndNote, lead.StartsWith(L.T("下载", "Download")) ? 4200 : 2800);
    }

    /// <summary>正在播放的东西变了（null = 什么都没在放）。</summary>
    public void SetMedia(NowPlaying? next) => SetMedia(next, settled: false);

    void SetMedia(NowPlaying? next, bool settled)
    {
        // 连按下一首时，播放器在两首歌之间常常有一下报“什么都没放”（新歌名还没出来）。
        // 卡片开着、锁着或者在桌面上时先别收：原样留着等一会儿，下一首来了直接换上；过了几秒还是没有才算真的停了。
        if (next == null && !settled && media != null && (mode == Mode.Music || musicPinned || floatWin != null))
        {
            if (!mediaGap.Enabled) mediaGap.Start();
            return;
        }
        mediaGap.Stop();
        var old = media;
        media = next;
        if (old?.Art != null && old.Art != next?.Art) old.Art.Dispose(); // 封面换了，旧的这边负责释放
        if (old?.Cover != null && old.Cover != next?.Cover) old.Cover.Dispose();
        if (old?.Source != next?.Source) volumeRead = 0; // 换了程序：音量重新读
        UpdateAccent(next);
        BuildPill();
        if (next == null) musicPinned = false; // 什么都不放了：锁住也一起取消
        if (next == null || !next.Playing || !IsBrowser(next.Source)) adFromUia = false; // 扩展报的不看这里：广告可能在别的标签页
        adSkippable = (adFromUia || adFromExt) && app.Config.SkipAds;
        if (mode == Mode.Music)
        {
            if (next == null) Collapse();
            else EnsureLyrics();
        }
        else if (next != null && (app.Config.PillLyrics || floatWin != null)) EnsureLyrics(); // 收起时也要滚歌词、桌面上有小播放器：换歌就去找
        UpdateFloat();
        Redraw();
    }

    static bool IsBrowser(string source) => source.Contains("msedge", StringComparison.OrdinalIgnoreCase) || source.Contains("chrome", StringComparison.OrdinalIgnoreCase);

    void UpdateAccent(NowPlaying? np)
    {
        var color = accentTo;
        float amount = 0;
        if (np != null)
        {
            if (np.Art != accentArt)
            {
                accentArt = np.Art;
                accentArtColor = np.Art != null ? AlbumColor(np.Art) : null;
            }
            // 没有封面（或者封面是黑白的）：按歌名定一个颜色，同一首歌每次都一样
            color = accentArtColor ?? TitleColor(np.Title + np.Artist);
            amount = np.Playing && app.Config.SongColor ? 1 : 0;
        }
        if (color == accentTo && amount == accentAmountTo) return;
        var (now, nowAmount) = Accent();
        accentFrom = nowAmount < .02f ? color : now; // 从没有光开始：直接用新颜色亮起来，不从旧颜色过渡
        accentAmountFrom = nowAmount;
        accentTo = color;
        accentAmountTo = amount;
        accentSince = Now;
    }

    (Color Color, float Amount) Accent()
    {
        float t = !animate || accentSince < 0 ? 1 : Math.Clamp((Now - accentSince) / (float)AccentMs, 0, 1);
        float e = t * t * (3 - 2 * t);
        return (Mix(accentFrom, accentTo, e), accentAmountFrom + (accentAmountTo - accentAmountFrom) * e);
    }

    /// <summary>封面的主色：按色相分 24 格，越鲜艳越亮的像素票越多，取票最多的那格；整张图没什么颜色就返回 null。</summary>
    static unsafe Color? AlbumColor(Bitmap art)
    {
        var weight = new double[24];
        var sum = new double[24 * 3];
        int samples = 0;
        var data = art.LockBits(new Rectangle(0, 0, art.Width, art.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < data.Height; y += 2)
            {
                uint* row = (uint*)((byte*)data.Scan0 + y * data.Stride);
                for (int x = 0; x < data.Width; x += 2, samples++)
                {
                    uint px = row[x];
                    int r = (int)(px >> 16 & 255), g = (int)(px >> 8 & 255), b = (int)(px & 255);
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    if (max < 40 || max - min < 30) continue;
                    double sat = (max - min) / (double)max;
                    double hue = max == r ? (g - b) / (double)(max - min) : max == g ? 2 + (b - r) / (double)(max - min) : 4 + (r - g) / (double)(max - min);
                    int bucket = ((int)Math.Floor((hue + 6) * 4) % 24 + 24) % 24;
                    double wt = sat * sat * (max / 255.0);
                    weight[bucket] += wt;
                    sum[bucket * 3] += r * wt;
                    sum[bucket * 3 + 1] += g * wt;
                    sum[bucket * 3 + 2] += b * wt;
                }
            }
        }
        finally { art.UnlockBits(data); }

        // 相邻三格一起算，颜色落在两格交界也不会被拆散
        int best = -1;
        double bestWeight = 0;
        for (int i = 0; i < 24; i++)
        {
            double w = weight[i] + weight[(i + 23) % 24] * .5 + weight[(i + 1) % 24] * .5;
            if (w > bestWeight) { bestWeight = w; best = i; }
        }
        if (best < 0 || bestWeight < samples * .015) return null;
        double R = 0, G = 0, B = 0, W = 0;
        foreach (int i in new[] { (best + 23) % 24, best, (best + 1) % 24 })
        {
            R += sum[i * 3]; G += sum[i * 3 + 1]; B += sum[i * 3 + 2]; W += weight[i];
        }
        return Vivid(R / W, G / W, B / W);
    }

    static Color TitleColor(string text)
    {
        uint hash = 2166136261;
        foreach (char ch in text) hash = (hash ^ ch) * 16777619;
        double hue = hash % 360 / 60.0;
        double f = hue - Math.Floor(hue), v = 235, lo = v * .4, fall = v * (1 - .6 * f), rise = v * (1 - .6 * (1 - f));
        return (int)hue switch
        {
            0 => Vivid(v, rise, lo),
            1 => Vivid(fall, v, lo),
            2 => Vivid(lo, v, rise),
            3 => Vivid(lo, fall, v),
            4 => Vivid(rise, lo, v),
            _ => Vivid(v, lo, fall),
        };
    }

    /// <summary>提亮、加一点饱和度，让光在深色玻璃上看得出颜色。</summary>
    static Color Vivid(double r, double g, double b)
    {
        double gray = (r + g + b) / 3;
        r = gray + (r - gray) * 1.25; g = gray + (g - gray) * 1.25; b = gray + (b - gray) * 1.25;
        double peak = Math.Max(1, Math.Max(r, Math.Max(g, b)));
        double k = 240 / peak;
        return Color.FromArgb(Math.Clamp((int)(r * k), 0, 255), Math.Clamp((int)(g * k), 0, 255), Math.Clamp((int)(b * k), 0, 255));
    }

    /// <summary>
    /// 连按两下 Ctrl：不管桌面被什么挡着，胶囊都浮到最上面展开，可以直接打字搜索；
    /// 再按一次、按 Esc、点别处或者打开文件就收起，沉回桌面层。
    /// </summary>
    public void Summon()
    {
        if (!Visible || !IsHandleCreated) return;
        // 开了“放歌时连按两下 Ctrl 锁住卡片”：打开音乐卡片并锁住，浮到所有窗口上面；再按一次解开、收起
        if (app.Config.CtrlLocksMusic && media != null && app.Config.ShowNowPlaying && floatWin == null && mode != Mode.Open)
        {
            if (mode == Mode.Music && musicPinned)
            {
                musicPinned = false;
                Collapse();
                return;
            }
            ShowMusic();
            musicPinned = true;
            ripple = Now;
            musicRaised = true;
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            return;
        }
        if (mode == Mode.Open && summoned)
        {
            Collapse();
            return;
        }
        if (mode == Mode.Open) // 已经在桌面上展开了：只要把键盘拿过来
        {
            TakeForeground(GetForegroundWindow());
            return;
        }
        summoned = true;
        sinkPending = false;
        previousForeground = GetForegroundWindow();
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Cancel();
        SetMode(Mode.Open);
        SampleBackdrop(force: true); // 背后现在是别的窗口，玻璃要照它的样子
    }

    /// <summary>
    /// 把键盘焦点拿过来。别的程序在前台时 Windows 不让随便抢，要先跟前台窗口的线程“挂在一起”；
    /// 这一步会等对方响应（几十毫秒），放到后台线程做，展开动画不受影响。
    /// </summary>
    void TakeForeground(IntPtr current)
    {
        var handle = Handle;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            uint me = GetCurrentThreadId();
            uint them = current == IntPtr.Zero ? 0 : GetWindowThreadProcessId(current, out uint _);
            bool attached = them != 0 && them != me && AttachThreadInput(me, them, true);
            SetForegroundWindow(handle);
            if (attached) AttachThreadInput(me, them, false);
        });
    }

    /// <summary>
    /// 不用鼠标打开音乐卡片（GlassBox.exe --music，可以做成快捷方式）。鼠标不在旁边也先留 8 秒，
    /// 鼠标进来过再离开就照常收起。
    /// </summary>
    public void ShowMusic()
    {
        if (!Visible || !IsHandleCreated || media == null) return;
        Cancel();
        CloseFloat(); // 拖出来放在桌面上的：收回胶囊里
        revealed = true;
        musicHoldUntil = Now + 8000;
        SendToBottom();
        SetMode(Mode.Music);
        SampleBackdrop(force: true); // 跟悬停展开一样：先照一下背后现在的样子，字的深浅才对
    }

    /// <summary>桌面上正在下载的文件（null = 没有了）。</summary>
    public void SetDownloading(string? name)
    {
        if (name == downloadName) return;
        downloadName = name;
        if (name != null)
        {
            if (Focusing && mode == Mode.Idle) return; // 专注时不弹下载进度
            if (mode is Mode.Idle or Mode.Notify)
            {
                if (pending == Pending.EndNote) Cancel();
                SetMode(Mode.Notify);
                BuildPill();
            }
        }
        else if (mode == Mode.Notify && notePath == null) Schedule(Pending.EndNote, 600);
        else BuildPill();
        Redraw();
    }

    // ---------- 状态 ----------

    void SetMode(Mode next)
    {
        if (next == mode) { BuildPill(); Kick(); return; }
        var previous = mode;
        mode = next;
        if (previous == Mode.Open) LeavePanel();
        if (previous == Mode.Music) LeaveMusic();
        if (next == Mode.Open) EnterPanel();
        if (next == Mode.Music) EnterMusic();
        if (next == Mode.Idle)
        {
            notePath = null;
            noteFence = null;
            chargeNote = adNote = false;
            dropFiles = Array.Empty<string>();
            trim.Stop();
            trim.Start();
        }
        BuildPill();
        Kick();
    }

    void EnterPanel()
    {
        panelLast = true;
        musicLast = false;
        query = "";
        selected = hover = hoverChip = -1;
        keyboardPicked = false;
        scroll = scrollTarget = 0;
        BuildPages();
        chipFollow = true;
        RefreshShown(keepScroll: false);
        // 允许点进来（要打字搜索）；如果当前焦点就在桌面上（或者是 Ctrl 呼出的），直接接过来，展开后马上能打字。
        // 抢焦点要等资源管理器那边处理完，要 30–40ms，放在界面线程上展开第一下就会顿住，所以丢到后台去做。
        SetNoActivate(false);
        var fg = GetForegroundWindow();
        if (!summoned && fg != Handle) previousForeground = fg; // 浮在窗口上面时点进来打字：收起后焦点还给它
        if (summoned || fg == IntPtr.Zero || DesktopShell.IsDesktopBackground(fg)) TakeForeground(fg);
    }

    void LeavePanel()
    {
        if (chipPressed) ChipLog("panel closed while pressed");
        chipHold.Stop();
        chipPressed = chipScrolling = false;
        chipPress = chipLift = chipInsert = -1;
        query = "";
        hover = hoverChip = selected = pressed = dropChip = -1;
        dropIndex = -1;
        ClosePreview();
        CloseSettings();
        SetNoActivate(true);
        bool wasSummoned = summoned;
        if (summoned)
        {
            summoned = false;
            sinkPending = true; // 缩回去的动画播完再沉回桌面层
        }
        if (focused)
        {
            focused = false;
            caret.Stop();
            // 焦点还回去：呼出前（或点进来前）在用哪个窗口就还给哪个，不然还给桌面。
            // 是点了别的窗口才收起的，焦点已经在那边了，不要再抢回来
            var back = (wasSummoned || Floating) && previousForeground != IntPtr.Zero && IsWindow(previousForeground) ? previousForeground : DesktopShell.Progman;
            if (GetForegroundWindow() == Handle) ThreadPool.QueueUserWorkItem(_ => SetForegroundWindow(back));
        }
    }

    /// <summary>缩在顶上时完全不挡鼠标：那几个像素底下常常就是窗口的按钮（比如左右分屏时左边窗口的 ×）。</summary>
    void UpdateClickThrough()
    {
        if (!IsHandleCreated) return;
        long style = (long)GetWindowLongPtr(Handle, GWL_EXSTYLE);
        long next = Tucked ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        if (next != style) SetWindowLongPtr(Handle, GWL_EXSTYLE, (IntPtr)next);
    }

    /// <summary>
    /// 顶边底下是一个没最大化的窗口（分屏、拖小的窗口）时，要停多久才弹出来；在它的最小化/最大化/关闭按钮上（右边缘那一截）就不弹。
    /// 返回 -1 表示不弹。最大化窗口的按钮在屏幕右上角，不会在胶囊底下，照旧 150ms。
    /// </summary>
    int RevealDelay(Point pos)
    {
        var under = WindowFromPoint(new POINT { X = pos.X, Y = pos.Y }); // 缩着时胶囊不挡鼠标，拿到的是底下的窗口
        if (under == IntPtr.Zero || under == Handle || DesktopShell.IsDesktopBackground(under)) return 150;
        var root = GetAncestor(under, GA_ROOT);
        if (root == IntPtr.Zero || IsZoomed(root) || !GetWindowRect(root, out var r)) return 150;
        if (pos.X >= r.Right - 150 * S && pos.X <= r.Right) return -1;
        return 350; // 底下可能是标签页：停久一点，确定是想叫胶囊
    }

    void SetNoActivate(bool on)
    {
        if (!IsHandleCreated) return;
        long style = (long)GetWindowLongPtr(Handle, GWL_EXSTYLE);
        long next = on ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
        if (next != style) SetWindowLongPtr(Handle, GWL_EXSTYLE, (IntPtr)next);
    }

    void Collapse()
    {
        if (mode is not (Mode.Open or Mode.Music)) return;
        if (musicPinned && media != null)
        {
            // 锁住了：从文件面板收起时回到音乐卡片，音乐卡片本身不收
            if (mode == Mode.Open) SetMode(Mode.Music);
            return;
        }
        SetMode(downloadName != null ? Mode.Notify : Mode.Idle);
        // 刚丢的还能撤销：收起后胶囊上留着“已丢进回收站 · 撤销”，到点就消失
        if (mode == Mode.Idle && CanUndoTrash && Now < trashToastUntil)
        {
            Notify(trashItem!, trashFence, L.T("已丢进回收站", "Moved to Recycle Bin"), trashFence == null ? trashPage : null, urgent: true);
            noteUndo = true;
            BuildPill();
            Schedule(Pending.EndNote, (int)Math.Max(300, trashToastUntil - Now));
        }
    }

    void Schedule(Pending what, int ms)
    {
        later.Stop();
        pending = what;
        later.Interval = ms;
        later.Start();
    }

    void Cancel()
    {
        later.Stop();
        pending = Pending.None;
    }

    void OnLater()
    {
        var what = pending;
        Cancel();
        switch (what)
        {
            case Pending.Open when mode == Mode.Idle:
                SetMode(HoverTarget);
                break;
            case Pending.Close when mode is Mode.Open or Mode.Music:
                Collapse();
                break;
            case Pending.EndNote when mode == Mode.Notify:
                chargeNote = adNote = false;
                if (downloadName != null && !Focusing)
                {
                    notePath = null;
                    BuildPill();
                    Kick();
                }
                else SetMode(Mode.Idle);
                break;
        }
    }

    // ---------- 充电、番茄钟、收起时的歌词、天色光 ----------

    /// <summary>电源状态变了（插拔电源、电量变化都会来）：只在刚插上充电器那一下亮一下。</summary>
    void OnPowerChanged()
    {
        var status = SystemInformation.PowerStatus;
        var line = status.PowerLineStatus;
        bool plugged = line == PowerLineStatus.Online && lastPower == PowerLineStatus.Offline;
        lastPower = line;
        if (!plugged || !app.Config.ChargeEffect || Focusing || !Visible) return;
        if (status.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery) || status.BatteryLifePercent is < 0 or > 1) return;
        if (mode is Mode.Open or Mode.Music || mode == Mode.Notify && !chargeNote) return; // 正在用、正在提示别的：不打断
        chargePercent = status.BatteryLifePercent;
        chargeSince = Now;
        chargeNote = true;
        adNote = false;
        noteUndo = false;
        notePath = null;
        noteFence = null;
        notePage = null;
        SetMode(Mode.Notify);
        Schedule(Pending.EndNote, 3200);
    }

    void ToggleFocus()
    {
        if (focusEnd != null) StopFocus();
        else StartFocus(TimeSpan.FromMinutes(app.Config.FocusMinutes is 15 or 25 or 45 or 60 ? app.Config.FocusMinutes : 25), rest: false);
    }

    void StartFocus(TimeSpan length, bool rest)
    {
        focusLength = length;
        focusEnd = DateTime.UtcNow + length;
        focusBreak = rest;
        if (!rest) focusMissed = 0;
        focusTick.Start();
        if (!rest && mode == Mode.Notify && !noteUndo) SetMode(Mode.Idle); // 开始专注：正在弹的提示收掉
        BuildPill();
        Redraw();
    }

    void StopFocus()
    {
        focusEnd = null;
        focusBreak = false;
        focusTick.Stop();
        BuildPill();
        Redraw();
    }

    TimeSpan FocusLeft => focusEnd is { } end ? end - DateTime.UtcNow : TimeSpan.Zero;

    /// <summary>每秒一下：倒计时走一格；到点了就换成休息，休息完就结束。</summary>
    void OnFocusTick()
    {
        if (focusEnd == null)
        {
            focusTick.Stop();
            return;
        }
        if (FocusLeft > TimeSpan.Zero)
        {
            BuildPill();
            if (mode is Mode.Idle or Mode.Open) Redraw();
            return;
        }
        System.Media.SystemSounds.Asterisk.Play(); // 跟着系统的提示音和音量
        if (!focusBreak)
        {
            int missed = focusMissed;
            StartFocus(TimeSpan.FromMinutes(BreakMinutes), rest: true);
            FocusNote(L.T("专注完成", "Focus done"), missed > 0 ? L.T($"休息 {BreakMinutes} 分钟 · 期间收进 {missed} 个文件", $"{BreakMinutes} min break · {missed} files came in") : L.T($"休息 {BreakMinutes} 分钟", $"Take a {BreakMinutes} min break"));
        }
        else
        {
            StopFocus();
            FocusNote(L.T("休息结束", "Break over"), L.T("可以开始下一轮了", "Ready for the next round"));
        }
    }

    void FocusNote(string lead, string text)
    {
        if (mode is Mode.Open or Mode.Music) return; // 正在用：面板上的倒计时已经变了
        noteLead = lead;
        noteName = text;
        noteUndo = chargeNote = false;
        notePath = null;
        noteFence = null;
        notePage = null;
        SetMode(Mode.Notify);
        ripple = Now;
        Schedule(Pending.EndNote, 4200);
    }

    /// <summary>收起时要滚的那句歌词；没开、没找到、间奏里没有词时返回 null（胶囊照常显示歌名）。</summary>
    string? PillLyricLine()
    {
        if (!app.Config.PillLyrics || !app.Config.Lyrics || media is not { Playing: true } np || lyrics is not { Instrumental: false } found) return null;
        if (lyricsKey != np.Title + "\n" + np.Artist) return null;
        int index = found.IndexAt(np.CurrentPosition + TimeSpan.FromMilliseconds(250)); // 跟卡片里一样早一点点换
        string text = index >= 0 ? found.Lines[index].Text.Trim() : "";
        pillLyricIndex = index;
        return text.Length > 0 ? text : null;
    }

    /// <summary>OnWatch 里每次看一眼：唱到下一句了就重排胶囊上的字（四分之一秒一次，只比个下标）。</summary>
    void CheckPillLyric()
    {
        if (mode != Mode.Idle || !app.Config.PillLyrics || lyrics == null || media is not { Playing: true } np) return;
        if (lyrics.IndexAt(np.CurrentPosition + TimeSpan.FromMilliseconds(250)) != pillLyricIndex) BuildPill();
    }

    // 天色光的关键帧：时刻、天空的颜色、光有多浓
    static readonly (float Hour, Color Color, float Strength)[] SkyKeys =
    {
        (0, Color.FromArgb(80, 95, 235), .30f),
        (5, Color.FromArgb(100, 100, 225), .28f),
        (6, Color.FromArgb(255, 140, 175), .36f),   // 朝霞
        (7.5f, Color.FromArgb(255, 198, 120), .32f),
        (10, Color.FromArgb(125, 205, 255), .26f),  // 晴空
        (15.5f, Color.FromArgb(140, 210, 255), .24f),
        (17.5f, Color.FromArgb(255, 150, 70), .40f), // 夕阳
        (19, Color.FromArgb(225, 90, 170), .36f),
        (20.5f, Color.FromArgb(90, 95, 235), .32f),  // 入夜
        (24, Color.FromArgb(80, 95, 235), .30f),
    };
    static readonly PointF[] Stars = { new(.10f, .30f), new(.19f, .70f), new(.27f, .24f), new(.74f, .28f), new(.82f, .68f), new(.91f, .36f) };

    /// <summary>
    /// 天色光：一团天空颜色的光，白天像太阳从胶囊左边慢慢走到右边，夜里换成月光，边上亮几颗星。
    /// 全是静止的，颜色跟着玻璃每 5 分钟调一次色时一起变，不为它多画一帧。
    /// </summary>
    void DrawSky(Graphics g, RectangleF c, float a)
    {
        var now = DateTime.Now;
        float hour = now.Hour + now.Minute / 60f;
        int i = 0;
        while (i < SkyKeys.Length - 2 && hour >= SkyKeys[i + 1].Hour) i++;
        var (h0, c0, k0) = SkyKeys[i];
        var (h1, c1, k1) = SkyKeys[i + 1];
        float t = (hour - h0) / (h1 - h0);
        var color = Mix(c0, c1, t);
        float strength = k0 + (k1 - k0) * t;
        bool day = hour >= 6 && hour < 18.5f;
        float sun = day ? .1f + .8f * (hour - 6) / 12.5f : .1f + .8f * ((hour - 18.5f + 24) % 24) / 11.5f;
        float s = S;
        EllipseGlow(g, new PointF(c.X + c.Width * sun, c.Y + c.Height * .62f), c.Width * .5f, c.Height * 1.3f, Color.FromArgb(Byte(strength * a), color), c);
        float stars = hour >= 21 || hour < 4.5f ? 1 : hour >= 20 ? hour - 20 : hour < 5.5f ? 5.5f - hour : 0;
        if (stars <= 0) return;
        using var brush = new SolidBrush(Color.FromArgb(Byte(.85f * stars * a), 255, 255, 255));
        for (int k = 0; k < Stars.Length; k++)
        {
            float r = (k % 3 == 0 ? 1.6f : 1.15f) * s;
            g.FillEllipse(brush, c.X + c.Width * Stars[k].X - r, c.Y + c.Height * Stars[k].Y - r, r * 2, r * 2);
        }
    }

    /// <summary>胶囊上的小电池：电量从空涨到现在的样子，中间一道闪电。</summary>
    void DrawBattery(Graphics g, float x, float cy, float a)
    {
        float s = S;
        float t = chargeSince >= 0 ? Math.Clamp((Now - chargeSince) / 900f, 0, 1) : 1;
        float fill = chargePercent * (1 - (1 - t) * (1 - t) * (1 - t));
        var green = app.Tone.DarkInk ? Color.FromArgb(22, 163, 74) : Color.FromArgb(74, 222, 128);
        var body = new RectangleF(x, cy - 6 * s, 21 * s, 12 * s);
        using (var pen = new Pen(Ink(.85f * a), 1.4f * s)) using (var path = Shapes.RoundRect(body, 3 * s)) g.DrawPath(pen, path);
        FillRound(g, new RectangleF(body.Right + 1 * s, cy - 2.5f * s, 2 * s, 5 * s), 1 * s, Ink(.85f * a));
        var inner = RectangleF.Inflate(body, -2.2f * s, -2.2f * s);
        FillRound(g, new RectangleF(inner.X, inner.Y, Math.Max(2 * s, inner.Width * fill), inner.Height), 1.5f * s, Color.FromArgb(Byte(a), green));
        float bx = body.X + body.Width / 2, by = cy;
        PointF[] bolt =
        {
            new(bx + 1.2f * s, by - 5 * s), new(bx - 2.6f * s, by + .6f * s), new(bx - .2f * s, by + .6f * s),
            new(bx - 1.2f * s, by + 5 * s), new(bx + 2.6f * s, by - .6f * s), new(bx + .2f * s, by - .6f * s),
        };
        using var boltBrush = new SolidBrush(app.Tone.DarkInk ? Color.FromArgb(Byte(a), 255, 255, 255) : Color.FromArgb(Byte(a), 20, 40, 24));
        g.FillPolygon(boltBrush, bolt);
    }

    /// <summary>番茄钟的小圆环：走过的那段亮起来（专注是番茄红，休息是薄荷绿）。</summary>
    void DrawFocusRing(Graphics g, float cx, float cy, float r, float a)
    {
        float s = S;
        double total = Math.Max(1, focusLength.TotalSeconds);
        float done = (float)Math.Clamp(1 - FocusLeft.TotalSeconds / total, 0, 1);
        var color = focusBreak ? Color.FromArgb(94, 234, 212) : Color.FromArgb(255, 112, 88);
        using (var track = new Pen(Ink(.22f * a), 2.2f * s)) g.DrawEllipse(track, cx - r, cy - r, r * 2, r * 2);
        if (done > 0)
        {
            using var pen = new Pen(Color.FromArgb(Byte(a), color), 2.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -90, Math.Max(4, 360 * done));
        }
    }

    /// <summary>面板上搜索框右边的计时按钮：没在计时是一个小秒表，计时中是进度环 + 还剩几分钟。</summary>
    void DrawFocusButton(Graphics g, float a)
    {
        if (focusRect.IsEmpty) return;
        float s = S;
        bool hot = focusRect.Contains(PointToClient(Cursor.Position));
        if (focusEnd != null || hot) FillRound(g, focusRect, 12 * s, Ink((focusEnd != null ? .16f : .12f) * a));
        float cx = focusRect.X + focusRect.Width / 2, cy = focusRect.Y + focusRect.Height / 2;
        if (focusEnd != null)
        {
            DrawFocusRing(g, cx, cy, 10.5f * s, a);
            string minutes = Math.Max(1, (int)Math.Ceiling(FocusLeft.TotalMinutes)).ToString();
            float w = Measure(minutes, smallFont) * s;
            DrawTight(g, minutes, smallFont, Ink(a), cx - w / 2, cy);
            return;
        }
        using var pen = new Pen(Ink(.78f * a), 1.7f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(pen, cx - 6.5f * s, cy - 5 * s, 13 * s, 13 * s);
        g.DrawLine(pen, cx, cy + 1.5f * s, cx, cy - 2 * s);
        g.DrawLine(pen, cx, cy + 1.5f * s, cx + 2.6f * s, cy + 1.5f * s);
        g.DrawLine(pen, cx - 2 * s, cy - 8.4f * s, cx + 2 * s, cy - 8.4f * s);
    }

    // ---------- 鼠标在哪 ----------

    void OnWatch()
    {
        if (!Visible) return;
        var pos = Cursor.Position;
        var under = WindowFromPoint(new POINT { X = pos.X, Y = pos.Y });
        // 被别的窗口挡住时（鼠标下面不是桌面也不是胶囊）不做任何反应
        bool overDesktop = under == Handle || DesktopShell.IsDesktopBackground(under);
        // 自动隐藏时胶囊浮在所有窗口上面：滑下来了就照常跟着鼠标，缩着的时候什么都不理（只看顶边）
        if (Floating) overDesktop = revealed || mode != Mode.Idle;
        float s = S, d = Distance(pos, capsuleOnScreen);
        bool moved = pos != cursor || overDesktop != cursorOverDesktop;
        cursor = pos;
        cursorOverDesktop = overDesktop;

        float centerX = capsuleOnScreen.X + capsuleOnScreen.Width / 2;
        int screenTop = Screen.PrimaryScreen!.Bounds.Top;
        bool nearTop = Floating && Tucked && pos.Y <= screenTop + 40 * s && Math.Abs(pos.X - centerX) < capsuleOnScreen.Width / 2 + 80 * s;
        int interval = overDesktop && d < 260 * s || nearTop ? 16 : 250;
        if (watch.Interval != interval) watch.Interval = interval;

        if (Floating)
        {
            UpdateClickThrough();
            if (Now - lastBusyCheck > 250)
            {
                lastBusyCheck = Now;
                bool busy = FullscreenBusy();
                if (busy != fullscreenBusy)
                {
                    fullscreenBusy = busy;
                    if (busy) revealed = false;
                    Kick();
                }
            }
            if (mode == Mode.Idle && !revealed)
            {
                // 鼠标顶在屏幕最上沿、胶囊附近，停一会儿才出来：快速划过去点浏览器标签页不会误触；
                // 底下是分屏窗口的关闭按钮就不出来
                bool atEdge = pos.Y <= screenTop + 1 && Math.Abs(pos.X - centerX) < capsuleOnScreen.Width / 2 + 60 * s;
                int delay = atEdge && !fullscreenBusy ? RevealDelay(pos) : -1;
                if (delay >= 0)
                {
                    if (edgeSince < 0) edgeSince = Now;
                    else if (Now - edgeSince >= delay)
                    {
                        revealed = true;
                        edgeSince = -1;
                        SendToBottom(); // 别的置顶窗口可能压在上面，重新提到最前
                        Kick();
                    }
                }
                else edgeSince = -1;
            }
            else if (mode == Mode.Idle && revealed)
            {
                // 收起来以后鼠标走远了半秒：再缩回去
                if (d > 70 * s && pending != Pending.Open)
                {
                    if (leaveSince < 0) leaveSince = Now;
                    else if (Now - leaveSince >= 500)
                    {
                        revealed = false;
                        leaveSince = -1;
                        Kick();
                    }
                }
                else leaveSince = -1;
            }
        }
        // 胶囊被窗口挡着 → 露出来（比如 Win+D 回到桌面）：记下时间，稍后马上重拍背后，
        // 不然玻璃还是很久以前（甚至开机时壁纸文件）的颜色，展开后才突然变过来
        bool open = Visible && !capsuleOnScreen.IsEmpty && WindowFromPoint(new POINT
        {
            X = (int)(capsuleOnScreen.X + capsuleOnScreen.Width / 2),
            Y = (int)(capsuleOnScreen.Y + capsuleOnScreen.Height / 2),
        }) == Handle;
        if (open && !uncovered) uncoveredSince = Now;
        uncovered = open;
        SampleBackdrop(force: false);
        CheckPillLyric();

        switch (mode)
        {
            case Mode.Idle:
                float p = overDesktop ? Math.Max(0, 1 - d / (150 * s)) : 0;
                float cx = capsuleOnScreen.X + capsuleOnScreen.Width / 2, cy = capsuleOnScreen.Y + capsuleOnScreen.Height / 2;
                var nextLean = new PointF(Math.Clamp((pos.X - cx) * .05f / s, -10, 10) * p, Math.Clamp((pos.Y - cy) * .05f / s, 0, 7) * p);
                if (p != prox || nextLean != lean)
                {
                    prox = p;
                    lean = nextLean;
                    Kick();
                }
                bool near = overDesktop && pos.X > capsuleOnScreen.Left - 36 * s && pos.X < capsuleOnScreen.Right + 36 * s
                    && pos.Y < capsuleOnScreen.Bottom + 46 * s && pos.Y > Area.Top - 40 * s;
                if (near && pending != Pending.Open && app.Config.HoverOpen != 3 && !detaching && floatWin?.Dragging != true)
                {
                    Schedule(Pending.Open, HoverOpenMs);
                    // 趁等的这一下先把展开用的大画布备好；背后的样子太旧就现在重拍，展开时颜色就是对的
                    var full = BackdropRegion().Size;
                    surface.Reserve(full.Width, full.Height);
                    if (Now - lastSample > 2500) SampleBackdrop(force: true);
                }
                else if (!near && pending == Pending.Open) Cancel();
                break;

            case Mode.Open:
                // Ctrl 呼出的不跟着鼠标收起（鼠标可能本来就在别处），要按 Esc、点别处或再按一次 Ctrl
                bool keep = summoned || focused && query.Length > 0 || menuOpen || dragging || previewItem != null || sliderDrag != null || chipPressed;
                if (d > 46 * s && !keep)
                {
                    if (pending != Pending.Close) Schedule(Pending.Close, CloseMs);
                }
                else if (pending == Pending.Close) Cancel();
                string clock = DateTime.Now.ToString("HH:mm");
                if (clock != clockShown) dirty = true;
                if (trashToastUntil > 0 && Now >= trashToastUntil)
                {
                    trashToastUntil = 0; // 底下那句提示到点了
                    dirty = true;
                }
                if (media is { Playing: true } && Now - playerDrawn > 1000) dirty = true; // 进度条一秒走一下
                break;

            case Mode.Music:
                // 鼠标到了卡片上：等着的时间不用再等了。要等卡片缩到位再算——从文件面板点进来时，
                // 卡片还没缩完、鼠标还“在上面”，这时就取消的话，缩完鼠标落在外面马上就收了
                if (d <= 46 * s && musicHoldUntil > 0)
                {
                    var settle = Target();
                    if (Math.Abs(sw.X - settle.W) < 2 && Math.Abs(sh.X - settle.H) < 2) musicHoldUntil = 0;
                }
                if (app.Config.BluetoothBattery && Now - btRead > 60000) ReadBluetooth();
                if (d > 46 * s && !seekDrag && !volumeDrag && !menuOpen && !musicPinned && Now >= musicHoldUntil)
                {
                    if (pending != Pending.Close) Schedule(Pending.Close, CloseMs);
                }
                else if (pending == Pending.Close) Cancel();
                if (Now - playerDrawn > 1000) dirty = true; // 暂停时也一秒看一次（定时还剩多久）
                if (!volumeDrag && Now - volumeRead > 2000) ReadVolume(); // 别处改了音量也跟上
                break;
        }
        UpdateAds();
        if (moved && d < 220 * s) dirty = true; // 高光跟着鼠标走
        if (dirty) Kick();
    }

    /// <summary>
    /// 拍一下胶囊背后真实的样子（动态壁纸、桌面小部件都算）当玻璃的底。
    /// 只在胶囊没被别的窗口挡住时拍：展开时 1.5 秒一次，平时 10 秒一次。
    /// </summary>
    void SampleBackdrop(bool force)
    {
        if (!Visible || capsuleOnScreen.IsEmpty) return;
        bool exposed = uncoveredSince > lastSample && Now - uncoveredSince > 350; // 刚露出来，等窗口最小化的动画放完再拍
        if (!force && !exposed && Now - lastSample < (mode == Mode.Open ? 4000 : 10000)) return;
        if (Animating && !force && !exposed) return; // 正在变形时不拍，免得卡一下
        var center = new POINT { X = (int)(capsuleOnScreen.X + capsuleOnScreen.Width / 2), Y = (int)(capsuleOnScreen.Y + capsuleOnScreen.Height / 2) };
        if (WindowFromPoint(center) != Handle) return;
        lastSample = Now;
        app.Tone.Sample(BackdropRegion(), Handle);
        BakeGlass(); // 趁空闲先把玻璃烤好，展开第一帧就不用现做
        dirty = true;
    }

    static float Distance(Point p, RectangleF r)
    {
        float dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
        float dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ---------- 动画 ----------

    static long Now => Environment.TickCount64;

    (float W, float H, float R, float X, float Y) Target()
    {
        float maxW = Area.Width / S - 24;
        // 缩上去：只露出胶囊最下面 4px；全屏时整个藏起来（连阴影）
        if (Tucked) return (Math.Max(228, pillWidth + 36), 40, 20, 0, fullscreenBusy ? -(TopGap + 40 + 34) : -(TopGap + 40 - 4));
        return mode switch
        {
            // 设置卡片要排两列：开着设置时至少 780 宽
            Mode.Open => (settingsOpen ? Math.Min(Math.Max(OpenW, 780), maxW) : OpenW, OpenHeight(), 26, 0, 0),
            Mode.Receive => (Math.Min(maxW, Math.Max(344, pillWidth + 48)), 62, 31, 0, 4),
            Mode.Music => (MusicSize().W, MusicSize().H, 28, 0, 0),
            Mode.Notify => (Math.Clamp(pillWidth + 40, 260, Math.Min(480, maxW)), 52, 26, 0, 0),
            _ => (Math.Max(228, pillWidth + 36) + 22 * prox, 40 + 3 * prox, 20 + 1.5f * prox, lean.X, lean.Y),
        };
    }

    float OpenHeight()
    {
        float areaH = Area.Height / S;
        if (settingsOpen) return Math.Max(Math.Min(260, areaH - 24), Math.Min(Math.Max(490, OpenMaxH), areaH - 90)); // 设置卡片要排 6 行：面板拉高
        int cols = Columns(OpenW);
        int rows = Math.Max(1, (shown.Count + cols - 1) / cols);
        return Math.Max(Math.Min(260, areaH - 24), Math.Min(Math.Min(OpenMaxH, areaH - 90), PanelChrome + rows * CellH));
    }

    static int Columns(float width) => Math.Max(1, (int)((width - 32) / CellW));

    bool Animating => pacing || frame.Enabled;

    void Kick()
    {
        if (pacing || !IsHandleCreated) return;
        frame.Stop();
        lastFrame = Stopwatch.GetTimestamp();
        StartPacing();
    }

    // 出帧节拍：WinForms 计时器跟着系统 15.6ms 的时钟跳，帧间隔在 7/24ms 之间乱跳，高刷屏上就是“卡”。
    // 改成后台线程等 DWM 每次合成（也就是屏幕刷新）后再通知窗口画一帧；动画停了线程就睡着，不耗电。
    const int WM_FRAME = 0x8000 + 0x47; // WM_APP + 'G'
    Thread? pacer;
    readonly ManualResetEventSlim pacerWake = new(false);
    volatile bool pacing;
    int framePending;

    void StartPacing()
    {
        pacing = true;
        pacerWake.Set();
        if (pacer != null) return;
        var handle = Handle;
        pacer = new Thread(() => PacerLoop(handle)) { IsBackground = true, Name = "GlassBox pacer", Priority = ThreadPriority.AboveNormal };
        pacer.Start();
    }

    void StopPacing()
    {
        pacing = false;
        pacerWake.Reset();
    }

    void PacerLoop(IntPtr handle)
    {
        long last = Stopwatch.GetTimestamp();
        double interval = 1000 / 60.0;
        int skip = 0;
        while (true)
        {
            pacerWake.Wait();
            if (DwmFlush() != 0) Thread.Sleep(8);
            long now = Stopwatch.GetTimestamp();
            double ms = (now - last) * 1000.0 / Stopwatch.Frequency;
            last = now;
            if (ms < 1) { Thread.Sleep(4); continue; } // 屏幕没在合成，DwmFlush 立刻返回了
            if (ms < 30) interval = interval * .9 + ms * .1; // 刚睡醒的那一下不算
            // 高刷屏（>100Hz）上一帧要画 5–7ms，赶不上每次刷新；固定隔一次出一帧，节奏均匀比时快时慢顺
            int every = interval < 10 ? 2 : 1;
            if (pacerHalf) every = Math.Max(every, (int)Math.Round(1000 / 30.0 / Math.Max(4, interval))); // 唱片在转：约 30 帧
            if (++skip < every) continue;
            skip = 0;
            if (Interlocked.Exchange(ref framePending, 1) == 0) PostMessage(handle, WM_FRAME, IntPtr.Zero, IntPtr.Zero);
        }
    }

    void OnFrame()
    {
        long stamp = Stopwatch.GetTimestamp();
        // 偶尔一帧来晚了也只往前走一小步：宁可慢一点点，也不要突然跳一大截
        float dt = Math.Min(1 / 45f, (stamp - lastFrame) / (float)Stopwatch.Frequency);
        lastFrame = stamp;
        frameDt = dt;
        UpdateClickThrough(); // 被 Ctrl 叫出来、有通知时立刻能点

        var t = Target();
        bool moving = Step(ref sw, t.W, dt) | Step(ref sh, t.H, dt) | Step(ref sr, t.R, dt) | Step(ref sx, t.X, dt) | Step(ref sy, t.Y, dt);
        if (Math.Abs(scroll - scrollTarget) > 0.5f)
        {
            scroll += (scrollTarget - scroll) * (animate ? .3f : 1);
            moving = true;
        }
        else scroll = scrollTarget;

        long now = Now;
        bool effects = Active(pillSwap, 300) | Active(ripple, 900) | Active(freshSince, 1800) | Active(previewSince, 220) | Active(accentSince, AccentMs) | Active(settingsSince, 220) | Active(liftSince, 160) | layoutMoving
            | (mode == Mode.Music && Active(lyricSince, LyricRollMs)) | (mode == Mode.Music && Active(volumeShownSince, VolumeShowMs)) | chipLift >= 0 | Active(chipLiftSince, 160) | ChipHolding
            | Active(pillLyricSince, 320) | Active(chargeSince, ChargeMs);
        effects |= TrashAnimating;
        if (trashSince >= 0 && !trashKicked && now - trashSince >= TrashFly)
        {
            // 图标进了口：胶囊整个“咽”一下（缩一点再弹回来）
            trashKicked = true;
            sh.V -= 150;
            sw.V -= 70;
        }
        // 按住等拿起来也在帧里看一眼：一直在画的时候计时器的消息排在最后，可能迟迟轮不到
        if (ChipHolding && mode == Mode.Open && now - chipPressSince >= chipHold.Interval) BeginChipLift(PointToClient(Cursor.Position).X);
        bool fileOverChips = liftPath != null && NearChipRow(PointToClient(Cursor.Position));
        if (chipLift >= 0 || fileOverChips) AutoScrollChips(dt);
        effects |= fileOverChips; // 停着不动也要一直滚
        bool live = MusicLive;
        if (live) UpdateBars();
        bool ambient = downloadName != null && mode == Mode.Notify && notePath == null || live; // 下载中的进度条；放歌时的音柱、唱片、歌词
        if (moving || effects || ambient || dirty)
        {
            dirty = false;
            Draw();
        }
        if (sinkPending && !moving && mode != Mode.Open)
        {
            sinkPending = false;
            SendToBottom(); // Ctrl 呼出的胶囊缩回去了：沉回桌面层（放到最底下也就不再置顶）
        }
        if (moving || effects)
        {
            pacerHalf = false;
            frame.Stop();
            StartPacing();
        }
        else if (live && TurntableShown)
        {
            pacerHalf = true; // 唱片在转：跟着屏幕刷新出帧，节奏均匀才顺
            frame.Stop();
            StartPacing();
        }
        else if (ambient)
        {
            StopPacing(); // 只有进度条、音柱在动：20 帧就够，省电
            frame.Interval = 50;
            frame.Start();
        }
        else
        {
            StopPacing();
            frame.Stop();
        }

        bool Active(long since, int length) => since >= 0 && now - since < length + 30;
    }

    bool Step(ref Spring s, float target, float dt)
    {
        if (!animate)
        {
            s.X = target;
            s.V = 0;
            return false;
        }
        float force = -300 * (s.X - target) - 23 * s.V;
        s.V += force * dt;
        s.X += s.V * dt;
        if (Math.Abs(s.X - target) < 0.15f && Math.Abs(s.V) < 0.6f)
        {
            s.X = target;
            s.V = 0;
            return false;
        }
        return true;
    }

    // ---------- 画 ----------

    RectangleF CapsuleRect => new(Px(Rim), Px(Rim), Px(sw.X), Px(sh.X));

    void Draw()
    {
        if (!IsHandleCreated || !Visible) return;
        var capsule = CapsuleRect;
        float m = Px(Rim);
        int ww = (int)Math.Ceiling(capsule.Width + m * 2), wh = (int)Math.Ceiling(capsule.Height + m * 2);
        var area = Area;
        int left = (int)Math.Round(area.Left + area.Width / 2f - capsule.Width / 2 - m + Px(sx.X));
        int top = (int)Math.Round(area.Top + Px(TopGap - Rim) + Px(sy.X));
        capsuleOnScreen = new RectangleF(left + m, top + m, capsule.Width, capsule.Height);

        var g = surface.Begin(ww, wh);
        try { Render(g, capsule, new Point(left, top)); }
        catch (Exception ex) { Log.Error(ex); }
        g.Flush(FlushIntention.Sync);

        var dst = new POINT { X = left, Y = top };
        var size = new SIZE { cx = ww, cy = wh };
        var src = new POINT();
        var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, surface.Dc, ref src, 0, ref blend, ULW_ALPHA);
    }

    Color Ink(float alpha) => DarkInk
        ? Color.FromArgb(Byte(alpha), 30, 24, 28)
        : Color.FromArgb(Byte(alpha), 255, 255, 255);

    Color Deep(float alpha)
    {
        var t = app.Tone.Tint;
        return Color.FromArgb(Byte(alpha), t.R / 4, t.G / 4, t.B / 4);
    }

    static int Byte(float alpha) => Math.Clamp((int)(alpha * 255), 0, 255);

    static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    static void Fill(Graphics g, RectangleF r, Color color)
    {
        if (color.A == 0) return;
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, r);
    }

    static void FillRound(Graphics g, RectangleF r, float radius, Color color)
    {
        if (color.A == 0) return;
        using var path = Shapes.RoundRect(r, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    void Render(Graphics g, RectangleF c, Point origin)
    {
        var tone = app.Tone;
        float s = S;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float radius = Math.Min(Px(sr.X), Math.Min(c.Width, c.Height) / 2);

        DrawShadow(g, c, radius);

        using var shape = Shapes.RoundRect(c, radius);
        // 玻璃：事先烤好的“模糊背景 + 颜色”贴图，直接拷进胶囊的形状里（先把阴影画完）
        g.Flush(FlushIntention.Sync);
        FillGlass(c, radius, origin);
        var state = g.Save();
        g.SetClip(shape, CombineMode.Intersect);

        float pillAlpha = Math.Clamp(1 - (sh.X - 66) / 40f, 0, 1);
        float full = OpenHeight();
        float open = Math.Clamp((sh.X - 70) / Math.Max(1, full - 70), 0, 1);
        float panelAlpha = mode == Mode.Open ? open * open : panelLast && mode != Mode.Music ? Math.Max(0, open * open - .2f) : 0;
        float music = Math.Clamp((sh.X - 70) / Math.Max(1, MusicSize().H - 70), 0, 1);
        float musicAlpha = mode == Mode.Music ? music * music : musicLast && mode != Mode.Open ? Math.Max(0, music * music - .2f) : 0;

        // 模糊封面背景（开了的话）：盖在玻璃上，光和字在它上面
        var cardCover = media?.Cover ?? media?.Art;
        bool coverBack = app.Config.MusicBackdrop && cardCover != null && musicAlpha > .01f;
        float clock = musicAlpha > .01f ? Drift() : drift;
        if (coverBack)
        {
            var (mw, mh) = MusicSize();
            cardBackdrop.Draw(g, c, new SizeF(mw * s, mh * s), cardCover!, clock, musicAlpha);
        }

        // 歌的颜色：收起时整颗胶囊透出这团光，展开后聚在底下播放器那一角
        var (accent, accentAmount) = Accent();
        float calm = Focusing ? .45f : 1; // 专注时光都收一收
        // 天色光：没在放歌（或歌的光淡下去）时，收起的胶囊里透出天空的颜色
        if (app.Config.SkyGlow && pillAlpha > .01f && accentAmount < .99f) DrawSky(g, c, pillAlpha * (1 - accentAmount) * calm);
        if (accentAmount > .01f)
        {
            if (pillAlpha > .01f)
                EllipseGlow(g, new PointF(c.X + c.Width * .28f, c.Y + c.Height * .55f), c.Width * .62f, c.Height * 1.25f,
                    Color.FromArgb(Byte(.34f * accentAmount * pillAlpha * calm), accent), c);
            if (panelAlpha > .01f && media != null)
                EllipseGlow(g, new PointF(c.X + 40 * s, c.Bottom - 16 * s), 300 * s, 90 * s,
                    Color.FromArgb(Byte(.30f * accentAmount * panelAlpha), accent), c);
            if (musicAlpha > .01f) // 音乐卡片：光从封面后面透出来
                EllipseGlow(g, new PointF(c.X + (MusicPad + MusicArtW / 2) * s, c.Y + (MusicPad + MusicArtH / 2) * s), (MusicArtW + 160) * s, 190 * s,
                    Color.FromArgb(Byte(.42f * accentAmount * musicAlpha), accent), c);
        }

        // 跟着鼠标的柔光
        var local = new PointF(cursor.X - origin.X, cursor.Y - origin.Y);
        float near = cursorOverDesktop ? Math.Clamp(1 - Distance(Point.Round(local), c) / (180 * s), 0, 1) : 0;
        if (near > 0) Glow(g, local, 200 * s, Color.FromArgb(Byte(.13f * near), 255, 255, 255), c);

        if (ripple >= 0 && Now - ripple < 600)
        {
            float t = (Now - ripple) / 600f;
            Fill(g, c, Color.FromArgb(Byte(.16f * (1 - t)), 255, 255, 255)); // 吞下文件时亮一下
        }

        // 充电：一道绿光从左扫到右
        float charge = chargeSince >= 0 ? (Now - chargeSince) / (float)ChargeMs : 1;
        if (charge < 1)
        {
            float sweep = 1 - (1 - charge) * (1 - charge);
            var green = Color.FromArgb(Byte(.42f * MathF.Sqrt(1 - charge)), 74, 222, 128);
            EllipseGlow(g, new PointF(c.X - 60 * s + (c.Width + 120 * s) * sweep, c.Y + c.Height / 2), 70 * s, c.Height * 1.1f, green, c);
        }

        if (pillAlpha > .01f) DrawPill(g, c, pillAlpha);
        if (panelAlpha > .01f) DrawPanel(g, c, panelAlpha);
        if (musicAlpha > .01f)
        {
            lightInk = coverBack; // 模糊封面是深色的：字用白的
            try { DrawMusic(g, c, musicAlpha); }
            finally { lightInk = false; }
        }
        g.Restore(state);

        // 边：一圈很细的亮边，离鼠标近的地方更亮
        var edge = c;
        edge.Inflate(-.6f * s, -.6f * s);
        using (var outline = Shapes.RoundRect(edge, Math.Max(1, radius - .6f * s)))
        {
            using (var pen = new Pen(Color.FromArgb(tone.DarkInk ? 120 : 50, 255, 255, 255), 1.2f * s)) g.DrawPath(pen, outline);
            if (accentAmount > .01f)
            {
                using var pen = new Pen(Color.FromArgb(Byte(.45f * accentAmount), Mix(accent, Color.White, .3f)), 1.2f * s);
                g.DrawPath(pen, outline);
            }
            if (charge < 1)
            {
                using var pen = new Pen(Color.FromArgb(Byte(.7f * (1 - charge)), 134, 239, 172), 1.4f * s);
                g.DrawPath(pen, outline);
            }
            if (musicPinned && musicAlpha > .01f)
            {
                // 锁住了：边上一圈慢慢呼吸的光（跟着歌的颜色；暂停时停在原地）
                float pulse = .5f + .5f * MathF.Sin(clock * 2.4f);
                var glowColor = accentAmount > .05f ? Mix(Color.White, accent, .55f * accentAmount) : Color.White;
                using (var wide = new Pen(Color.FromArgb(Byte((.08f + .12f * pulse) * musicAlpha), glowColor), 6 * s)) g.DrawPath(wide, outline);
                using var thin = new Pen(Color.FromArgb(Byte((.45f + .35f * pulse) * musicAlpha), glowColor), 1.6f * s);
                g.DrawPath(thin, outline);
            }
            if (near > 0)
            {
                using var path = new GraphicsPath();
                float rr = 170 * s;
                path.AddEllipse(local.X - rr, local.Y - rr, rr * 2, rr * 2);
                using var brush = new PathGradientBrush(path)
                {
                    CenterPoint = local,
                    CenterColor = Color.FromArgb(Byte(.95f * near), 255, 255, 255),
                    SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) },
                };
                using var pen = new Pen(brush, 1.5f * s);
                g.DrawPath(pen, outline);
            }
        }

        // 涟漪：从胶囊边上往外扩散的光圈
        if (ripple >= 0 && Now - ripple < 900)
        {
            for (int k = 0; k < 2; k++)
            {
                float t = (Now - ripple - k * 140) / 760f;
                if (t <= 0 || t >= 1) continue;
                float ease = 1 - (1 - t) * (1 - t) * (1 - t);
                var ring = c;
                float grow = ease * (Rim - 3) * s;
                ring.Inflate(grow, grow * .8f);
                using var path = Shapes.RoundRect(ring, radius + grow);
                using var glow = new Pen(Color.FromArgb(Byte(.35f * (1 - t)), tone.Tint), 5 * s);
                using var pen = new Pen(Color.FromArgb(Byte(.75f * (1 - t)), 255, 255, 255), 1.5f * s);
                g.DrawPath(glow, path);
                g.DrawPath(pen, path);
            }
        }
    }

    // 阴影每层的透明度：外圈淡、里圈是几层叠起来的深度（跟一层层填满叠出来的效果一样，但只画一圈圈细环）
    static readonly int[] ShadowAlpha = BuildShadowAlpha();

    static int[] BuildShadowAlpha()
    {
        var result = new int[10]; // 下标 0..9 对应第 -2..7 圈
        for (int i = -2; i <= 7; i++)
        {
            double clear = 1;
            for (int j = Math.Max(i, 1); j <= 7; j++) clear *= 1 - (j > 5 ? 4 : 7) / 255.0;
            result[i + 2] = (int)Math.Round((1 - clear) * 255);
        }
        return result;
    }

    void DrawShadow(Graphics g, RectangleF c, float radius)
    {
        float s = S, step = 2.6f * s;
        for (int i = -2; i <= 7; i++)
        {
            float grow = (i - .5f) * step;
            var r = c;
            r.Inflate(grow, grow);
            r.Offset(0, 6 * s);
            if (r.Width <= 0 || r.Height <= 0) continue;
            using var path = Shapes.RoundRect(r, Math.Max(1, Math.Min(radius + grow, Math.Min(r.Width, r.Height) / 2)));
            using var pen = new Pen(Color.FromArgb(ShadowAlpha[i + 2], 0, 0, 0), step);
            g.DrawPath(pen, path);
        }
    }

    // 玻璃贴图：盒子可能展开到的整块区域，模糊背景和颜色层预先合成好；颜色或背景变了才重建
    // 像素数组固定在内存里，GDI+ 直接画进去，重烤时原地覆盖，不再每次新分配几 MB
    uint[]? glassPixels;
    System.Runtime.InteropServices.GCHandle glassPin;
    Bitmap? glassBitmap;
    Rectangle glassScreen;
    int glassVersion = -1, glassClarity = -1;

    unsafe bool BakeGlass()
    {
        var tone = app.Tone;
        var region = BackdropRegion();
        int clarity = Math.Clamp(app.Config.GlassClarity, 0, 100);
        if (glassBitmap != null && glassVersion == tone.Version && glassScreen == region && glassClarity == clarity) return true;
        if (region.Width <= 0 || region.Height <= 0) return false;

        if (glassBitmap == null || glassScreen.Size != region.Size)
        {
            FreeGlass();
            glassPixels = new uint[region.Width * region.Height];
            glassPin = System.Runtime.InteropServices.GCHandle.Alloc(glassPixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            glassBitmap = new Bitmap(region.Width, region.Height, region.Width * 4, PixelFormat.Format32bppPArgb, glassPin.AddrOfPinnedObject());
        }
        glassScreen = region;
        glassVersion = tone.Version;
        glassClarity = clarity;
        // 透明度：50 是原来的样子；往下颜色层加厚再盖一层磨砂；
        // 往上整块玻璃本身变透明（窗口是逐像素透明的），后面真实的桌面、动态壁纸直接透出来，到 100% 只剩一层薄薄的底色
        float layers = clarity >= 50 ? 1 : 1 + (50 - clarity) / 50f;
        float frost = clarity < 50 ? (50 - clarity) / 50f : 0;
        float opacity = clarity > 50 ? 1 - (clarity - 50) / 50f * .9f : 1;
        using (var g = Graphics.FromImage(glassBitmap))
        {
            var all = new RectangleF(0, 0, region.Width, region.Height);
            // 模糊的背景 → 壁纸色 → 保证文字看得清的明暗 → 夜里的压暗
            if (!tone.DrawBackdrop(g, all, region)) Fill(g, all, Color.FromArgb(255, Mix(tone.Tint, Color.Black, .6f)));
            Fill(g, all, Color.FromArgb(Math.Min(255, (int)((tone.DarkInk ? 87 : 51) * layers)), tone.Tint));
            if (tone.DarkInk) Fill(g, all, Color.FromArgb(Math.Min(255, (int)(60 * layers)), 255, 255, 255));
            else Fill(g, all, Color.FromArgb(Byte(Math.Clamp((tone.TopLuminance - .3f) * .6f + .08f, .08f, .34f) * layers), 0, 0, 0));
            if (frost > 0) Fill(g, all, Color.FromArgb(Byte(.62f * frost), tone.DarkInk ? Mix(tone.Tint, Color.White, .75f) : Mix(tone.Tint, Color.Black, .62f)));
            Fill(g, all, tone.Veil);
            g.Flush(FlushIntention.Sync);
        }
        if (opacity < 1)
        {
            // 预乘像素：四个通道一起乘，就是整体变透明
            uint k = (uint)(opacity * 256);
            fixed (uint* p = glassPixels)
                for (int i = 0, n = glassPixels!.Length; i < n; i++)
                {
                    uint px = p[i];
                    p[i] = ((px & 0xFF00FF) * k >> 8) & 0xFF00FF | (((px >> 8) & 0xFF00FF) * k) & 0xFF00FF00;
                }
        }
        return true;
    }

    void FreeGlass()
    {
        glassBitmap?.Dispose();
        glassBitmap = null;
        if (glassPin.IsAllocated) glassPin.Free();
        glassPixels = null;
    }

    /// <summary>
    /// 把玻璃贴图按屏幕位置直接拷进画布的圆角矩形里。GDI+ 的贴图填充在这么大的面积上要 10ms，
    /// 逐行拷内存不到 1ms；圆角边缘按覆盖比例混合，保持抗锯齿。
    /// </summary>
    unsafe void FillGlass(RectangleF c, float radius, Point origin)
    {
        if (!BakeGlass() || surface.Bits == IntPtr.Zero) return;
        int gw = glassScreen.Width, gh = glassScreen.Height, stride = surface.Stride / 4;
        int offX = origin.X - glassScreen.X, offY = origin.Y - glassScreen.Y;
        uint* canvas = (uint*)surface.Bits;
        int top = Math.Max(0, (int)c.Top), bottom = (int)MathF.Ceiling(c.Bottom);
        fixed (uint* glass = glassPixels)
        {
            for (int y = top; y < bottom; y++)
            {
                float rowCover = Math.Min(y + 1, c.Bottom) - Math.Max(y, c.Top);
                if (rowCover <= 0) continue;
                float yc = y + .5f, xl = c.Left, xr = c.Right;
                float d = yc < c.Top + radius ? c.Top + radius - yc : yc > c.Bottom - radius ? yc - (c.Bottom - radius) : 0;
                if (d > 0)
                {
                    if (d >= radius) continue;
                    float inset = radius - MathF.Sqrt(radius * radius - d * d);
                    xl += inset;
                    xr -= inset;
                }
                uint* src = glass + Math.Clamp(y + offY, 0, gh - 1) * gw;
                uint* dst = canvas + y * stride;
                int left = Math.Max(0, (int)xl), right = Math.Min(stride, (int)MathF.Ceiling(xr));
                int solidL = (int)MathF.Ceiling(xl), solidR = (int)xr; // 完全盖住的像素 [solidL, solidR)
                if (rowCover < .999f) solidL = solidR = right;
                for (int x = left; x < right; x++)
                {
                    if (x == solidL && solidR > solidL && x + offX >= 0 && solidR + offX <= gw)
                    {
                        Buffer.MemoryCopy(src + x + offX, dst + x, (right - x) * 4L, (solidR - x) * 4L);
                        x = solidR - 1;
                        continue;
                    }
                    uint s = src[Math.Clamp(x + offX, 0, gw - 1)];
                    float cover = (Math.Min(x + 1, xr) - Math.Max(x, xl)) * rowCover;
                    if (cover >= .999f) dst[x] = s;
                    else if (cover > 0) dst[x] = Lerp(dst[x], s, (uint)(cover * 256));
                }
            }
        }
    }

    /// <summary>预乘像素按 a/256 混合。</summary>
    static uint Lerp(uint d, uint s, uint a)
    {
        uint ia = 256 - a;
        uint rb = (((s & 0xFF00FF) * a + (d & 0xFF00FF) * ia) >> 8) & 0xFF00FF;
        uint ag = (((s >> 8) & 0xFF00FF) * a + ((d >> 8) & 0xFF00FF) * ia) & 0xFF00FF00;
        return rb | ag;
    }

    /// <summary>胶囊（含展开后、阴影、探向鼠标的偏移）可能覆盖到的整块屏幕区域。</summary>
    Rectangle BackdropRegion()
    {
        var area = Area;
        float s = S;
        // 最大的样子：展开（设置开着至少 780×490）或者带歌词的宽封面音乐卡片
        float maxW = Math.Max(Math.Max(OpenW, 780), MusicPad * 2 + MusicArtMaxW + 20 + MusicInfoW + 24 + MusicLyricsW);
        float w = (Math.Min(maxW, area.Width / s - 24) + Rim * 2 + 24) * s, h = (Math.Min(Math.Max(OpenMaxH, 490), area.Height / s - 90) + Rim * 2 + 12) * s;
        var region = new Rectangle((int)(area.Left + area.Width / 2f - w / 2), (int)(area.Top + (TopGap - Rim) * s), (int)w, (int)h);
        return Rectangle.Intersect(region, Screen.PrimaryScreen!.Bounds);
    }

    static void Glow(Graphics g, PointF center, float radius, Color color, RectangleF bounds)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        using var brush = new PathGradientBrush(path)
        {
            CenterPoint = center,
            CenterColor = color,
            SurroundColors = new[] { Color.FromArgb(0, color) },
        };
        g.FillRectangle(brush, RectangleF.Intersect(bounds, path.GetBounds()));
    }

    static void EllipseGlow(Graphics g, PointF center, float rx, float ry, Color color, RectangleF bounds)
    {
        if (color.A == 0) return;
        using var path = new GraphicsPath();
        path.AddEllipse(center.X - rx, center.Y - ry, rx * 2, ry * 2);
        using var brush = new PathGradientBrush(path)
        {
            CenterPoint = center,
            CenterColor = color,
            SurroundColors = new[] { Color.FromArgb(0, color) },
        };
        g.FillRectangle(brush, RectangleF.Intersect(bounds, path.GetBounds()));
    }

    // ---------- 胶囊上的一行字 ----------

    void BuildPill()
    {
        pill.Clear();
        switch (mode)
        {
            case Mode.Receive:
                pill.Add((Seg.Dot, "", 0, 0));
                if (dropFiles.Length == 1)
                {
                    pill.Add((Seg.Text, L.T("松手，收进", "Drop to file into"), 0, 10));
                    pill.Add((Seg.Chip, app.CategoryNameFor(dropFiles[0]), 0, 8));
                }
                else pill.Add((Seg.Text, L.T($"松手，收纳 {dropFiles.Length} 个文件", $"Drop to file {dropFiles.Length} items"), 0, 10));
                break;

            case Mode.Notify when chargeNote:
                pill.Add((Seg.Battery, "", 0, 0));
                pill.Add((Seg.Text, L.T("正在充电", "Charging"), 0, 10));
                pill.Add((Seg.Bold, $"{(int)Math.Round(chargePercent * 100)}%", 0, 8));
                break;

            case Mode.Notify when adNote:
                pill.Add((Seg.Dot, "", 0, 0));
                pill.Add((Seg.Text, adNoteText, 0, 10));
                if (adNoteChip) pill.Add((Seg.Chip, L.T("跳过", "Skip"), 0, 10));
                break;

            case Mode.Notify when notePath == null && downloadName != null && !Focusing:
                pill.Add((Seg.Muted, L.T("正在下载", "Downloading"), 0, 0));
                pill.Add((Seg.Text, Shorten(downloadName, 22), 0, 10));
                pill.Add((Seg.Bar, "", 0, 12));
                break;

            case Mode.Notify:
                pill.Add((Seg.Dot, "", 0, 0));
                pill.Add((Seg.Muted, noteLead, 0, 10));
                if (noteFence != null) pill.Add((Seg.Chip, noteFence.Name, 0, 8));
                else if (notePage is { } kind && IndexOfKind(kind) is int at and >= 0) pill.Add((Seg.Chip, pages[at].Name, 0, 8));
                pill.Add((Seg.Text, Shorten(noteName, 18), 0, 8));
                if (noteUndo) pill.Add((Seg.Chip, L.T("撤销", "Undo"), 0, 10));
                break;

            case Mode.Idle when focusEnd != null:
                // 番茄钟：胶囊上只留一个小圆环和倒计时，别的都先不打扰
                var left = FocusLeft < TimeSpan.Zero ? TimeSpan.Zero : FocusLeft;
                pill.Add((Seg.Ring, "", 0, 0));
                pill.Add((Seg.Count, $"{(int)left.TotalMinutes:00}:{left.Seconds:00}", 0, 10));
                pill.Add((Seg.Muted, focusBreak ? L.T("休息一下", "Break") : L.T("专注中", "Focusing"), 0, 8));
                break;

            case Mode.Idle when media is { Playing: true } && app.Config.ShowNowPlaying:
                // 在放歌/视频：胶囊上显示正在放什么，像灵动岛；开了“收起时滚动歌词”就显示正在唱的那句
                pill.Add((Seg.Note, "", 0, 0));
                if (PillLyricLine() is { } line)
                {
                    line = Shorten(line, 22);
                    if (line != pillLyric)
                    {
                        pillLyricPrev = pillLyric;
                        pillLyric = line;
                        pillLyricSince = pillLyricPrev.Length > 0 ? Now : -1;
                        Kick();
                    }
                    pill.Add((Seg.Lyric, line, 0, 10));
                }
                else
                {
                    pillLyric = pillLyricPrev = "";
                    var (songTitle, songArtist) = Pretty(media);
                    pill.Add((Seg.Text, Shorten(songTitle, 24), 0, 10));
                    if (songArtist.Length > 0) pill.Add((Seg.Muted, Shorten(songArtist, 14), 0, 8));
                }
                if (app.Config.MusicMotion && !Focusing) pill.Add((Seg.Wave, "", 0, 10));
                break;

            default:
                int today = app.TodayCount;
                pill.Add((Seg.Dot, "", 0, 0));
                if (today > 0)
                {
                    pill.Add((Seg.Text, L.T("今天", "Today"), 0, 10));
                    pill.Add((Seg.Bold, $"+{today}", 0, 4));
                }
                else pill.Add((Seg.Text, Greeting(), 0, 10));
                pill.Add((Seg.Sep, "", 0, 10));
                pill.Add((Seg.Bold, app.TotalCount.ToString(), 0, 10));
                pill.Add((Seg.Muted, L.T("个文件", "files"), 0, 4));
                break;
        }

        float total = 0;
        for (int i = 0; i < pill.Count; i++)
        {
            var (kind, text, _, gap) = pill[i];
            float width = kind switch
            {
                Seg.Dot => 8,
                Seg.Note => media?.Art != null ? 24 : 14,
                Seg.Wave => 18,
                Seg.Sep => 1,
                Seg.Bar => 96,
                Seg.Battery => 24,
                Seg.Ring => 16,
                Seg.Count => Measure("88:88", boldFont), // 固定宽度：数字每秒在变，胶囊不跟着抖
                Seg.Lyric => Math.Max(Measure(text, pillFont), 170), // 短句也留这么宽：一句句换时胶囊不忽宽忽窄
                Seg.Chip => Measure(text, chipFont) + 18,
                _ => Measure(text, kind == Seg.Bold ? boldFont : pillFont),
            };
            pill[i] = (kind, text, width, i == 0 ? 0 : gap);
            total += width + (i == 0 ? 0 : gap);
        }
        pillWidth = total;

        // 倒计时的数字、一句句的歌词有自己的过渡，不算“换内容”（不然每秒都整行淡入一次）
        string key = mode + string.Join("|", pill.Select(p => p.Kind is Seg.Count or Seg.Lyric ? p.Kind.ToString() : p.Text));
        if (key != pillKey)
        {
            if (pillKey.Length > 0) pillSwap = Now;
            pillKey = key;
            Kick();
        }
    }

    /// <summary>量字宽，返回 DIP。</summary>
    float Measure(string text, Font font) => text.Length == 0 ? 0 : measure.MeasureString(text, font, PointF.Empty, Tight).Width / S;

    static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    static string Greeting() => DateTime.Now.Hour switch
    {
        >= 5 and < 11 => L.T("早上好", "Good morning"),
        >= 11 and < 13 => L.T("中午好", "Good day"),
        >= 13 and < 18 => L.T("下午好", "Good afternoon"),
        >= 18 and < 23 => L.T("晚上好", "Good evening"),
        _ => L.T("夜深了", "It's late"),
    };

    void DrawPill(Graphics g, RectangleF c, float alpha)
    {
        float s = S;
        float swap = pillSwap >= 0 ? Math.Clamp((Now - pillSwap) / 280f, 0, 1) : 1;
        float ease = 1 - (1 - swap) * (1 - swap);
        alpha *= ease;
        float x = c.X + (c.Width - pillWidth * s) / 2, cy = c.Y + c.Height / 2 + (1 - ease) * 5 * s;
        var tint = app.Tone.Tint;

        foreach (var (kind, text, width, gap) in pill)
        {
            x += gap * s;
            float w = width * s;
            switch (kind)
            {
                case Seg.Dot:
                    var dot = Mix(tint, Color.White, .35f);
                    using (var halo = new SolidBrush(Color.FromArgb(Byte(.35f * alpha), dot))) g.FillEllipse(halo, x - 3 * s, cy - 7 * s, 14 * s, 14 * s);
                    using (var brush = new SolidBrush(Color.FromArgb(Byte(alpha), dot))) g.FillEllipse(brush, x, cy - 4 * s, 8 * s, 8 * s);
                    break;
                case Seg.Sep:
                    Fill(g, new RectangleF(x, cy - 7 * s, Math.Max(1, s), 14 * s), Ink(.3f * alpha));
                    break;
                case Seg.Note when media?.Art is { } disc:
                    DrawDisc(g, disc, x + w / 2, cy, 12 * s, alpha, spin: app.Config.MusicMotion && media.Playing && !Focusing);
                    break;
                case Seg.Wave:
                    // 跟着声音跳的四根音柱（只在放歌、胶囊露在外面时每秒 20 帧地动）
                    var (waveAccent, waveAmount) = Accent();
                    var waveColor = Color.FromArgb(Byte(alpha), Mix(Mix(tint, Color.White, .45f), Mix(waveAccent, Color.White, .25f), waveAmount));
                    for (int b = 0; b < barLevels.Length; b++)
                    {
                        float h = (3 + 11 * barLevels[b]) * s;
                        FillRound(g, new RectangleF(x + b * 4.6f * s, cy - h / 2, 2.6f * s, h), 1.3f * s, waveColor);
                    }
                    break;
                case Seg.Note:
                    // 没有封面：三根音柱（静止的，不为了跳动一直重画）
                    var (accent, amount) = Accent();
                    var bar = Mix(Mix(tint, Color.White, .45f), Mix(accent, Color.White, .25f), amount);
                    float[] heights = { 7, 12, 9 };
                    for (int b = 0; b < 3; b++)
                        FillRound(g, new RectangleF(x + b * 5 * s, cy + 6 * s - heights[b] * s, 3 * s, heights[b] * s), 1.5f * s, Color.FromArgb(Byte(alpha), bar));
                    break;
                case Seg.Bar:
                    var track = new RectangleF(x, cy - 2 * s, w, 4 * s);
                    FillRound(g, track, 2 * s, Ink(.18f * alpha));
                    float phase = Now % 1400 / 1400f;
                    float segW = w * .32f, segX = track.X - segW + (track.Width + segW) * phase;
                    var state = g.Save();
                    g.SetClip(track, CombineMode.Intersect);
                    FillRound(g, new RectangleF(segX, track.Y, segW, track.Height), 2 * s, Ink(.95f * alpha));
                    g.Restore(state);
                    break;
                case Seg.Chip:
                    FillRound(g, new RectangleF(x, cy - 10 * s, w, 20 * s), 10 * s, Ink(.16f * alpha));
                    DrawTight(g, text, chipFont, Ink(alpha), x + 9 * s, cy);
                    break;
                case Seg.Battery:
                    DrawBattery(g, x, cy, alpha);
                    break;
                case Seg.Ring:
                    DrawFocusRing(g, x + w / 2, cy, 7 * s, alpha);
                    break;
                case Seg.Count:
                    DrawTight(g, text, boldFont, Ink(alpha), x + (w - Measure(text, boldFont) * s) / 2, cy);
                    break;
                case Seg.Lyric:
                    // 换句：旧的往上退着淡掉，新的从下面浮上来
                    float roll = animate && pillLyricSince >= 0 ? Math.Clamp((Now - pillLyricSince) / 320f, 0, 1) : 1;
                    float re = 1 - (1 - roll) * (1 - roll) * (1 - roll);
                    if (roll < 1 && pillLyricPrev.Length > 0)
                        DrawTight(g, pillLyricPrev, pillFont, Ink(alpha * (1 - re)), x + (w - Measure(pillLyricPrev, pillFont) * s) / 2, cy - re * 11 * s);
                    DrawTight(g, text, pillFont, Ink(alpha * (roll < 1 ? re : 1)), x + (w - Measure(text, pillFont) * s) / 2, cy + (1 - re) * 11 * s);
                    break;
                default:
                    var font = kind == Seg.Bold ? boldFont : pillFont;
                    DrawTight(g, text, font, Ink(kind == Seg.Muted ? .68f * alpha : alpha), x, cy);
                    break;
            }
            x += w;
        }
    }

    static void DrawTight(Graphics g, string text, Font font, Color color, float x, float centerY)
    {
        using var brush = new SolidBrush(color);
        float h = font.GetHeight(g);
        g.DrawString(text, font, brush, new PointF(x, centerY - h / 2), Tight);
    }

    // ---------- 面板 ----------

    void LayoutPanel(RectangleF c)
    {
        float s = S;
        float timer = app.Config.FocusTimer ? 40 : 0; // 开了番茄钟：搜索框让出一个按钮的位置
        searchRect = new RectangleF(c.X + 18 * s, c.Y + 16 * s, c.Width - 36 * s - 104 * s - 44 * s - timer * s, 38 * s);
        focusRect = timer > 0 ? new RectangleF(searchRect.Right + 6 * s, searchRect.Y + 2 * s, 34 * s, 34 * s) : RectangleF.Empty;
        gearRect = new RectangleF((timer > 0 ? focusRect.Right : searchRect.Right) + 6 * s, searchRect.Y + 2 * s, 34 * s, 34 * s);
        // 分类一排放不下时可以左右滚（滚轮在这一排上滚，或者切换时自动滚到当前那个）
        chipRow = new RectangleF(c.X + 14 * s, c.Y + 66 * s, c.Width - 28 * s, 30 * s);
        chipRects.Clear();
        float start = chipRow.X + 4 * s - chipScroll * s, x = start;
        // 正在调顺序：拿起来的那个跟着鼠标，要放下的地方空出它的宽度
        float gap = chipLift >= 0 ? chipWidths[chipLift] * s + 6 * s : 0;
        int slot = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            if (i == chipLift)
            {
                chipRects.Add(RectangleF.Empty);
                continue;
            }
            if (chipLift >= 0 && slot == chipInsert) x += gap;
            float w = chipWidths[i] * s;
            chipRects.Add(new RectangleF(x, chipRow.Y, w, chipRow.Height));
            x += w + (i == FixedPages - 1 ? 17 : 6) * s; // “常用”“最近下载”和分类之间隔一道细线
            slot++;
        }
        if (chipLift >= 0)
        {
            if (slot == chipInsert) x += gap;
            float w = chipWidths[chipLift] * s;
            chipRects[chipLift] = new RectangleF(Math.Clamp(chipLiftX - chipGrabDx, chipRow.X, chipRow.Right - w), chipRow.Y, w, chipRow.Height);
        }
        chipRowWidth = (x - start) / s;
        gridRect = new RectangleF(c.X + 16 * s, c.Y + 108 * s, c.Width - 32 * s, Math.Max(0, c.Height - 108 * s - 36 * s));
        columns = Columns(c.Width / s);
        gridLeft = gridRect.X + (gridRect.Width - columns * CellW * s) / 2;
    }

    float MaxScroll
    {
        get
        {
            int rows = (shown.Count + columns - 1) / columns;
            return Math.Max(0, rows * CellH - gridRect.Height / S);
        }
    }

    float MaxChipScroll => Math.Max(0, chipRowWidth - chipRow.Width / S + 8);

    /// <summary>按在分类上、还在等它拿起来。</summary>
    bool ChipHolding => chipPressed && !chipScrolling && chipLift < 0 && chipPress >= FixedPages;

    /// <summary>%APPDATA%\GlassBox 里放一个 chip-debug 文件：把分类拖动的每一步记进日志（查问题用）。</summary>
    static readonly bool ChipDebug = File.Exists(Path.Combine(ConfigStore.Dir, "chip-debug"));

    static void ChipLog(string message)
    {
        if (ChipDebug) Log.Info("chip " + message);
    }

    void BeginChipLift(float x)
    {
        ChipLog($"lift press={chipPress} fixed={FixedPages} rects={chipRects.Count} x={x}");
        if (chipPress < FixedPages || chipPress >= chipRects.Count) return;
        chipHold.Stop();
        chipGrabDx = chipPressAt.X - chipRects[chipPress].X;
        chipLift = chipInsert = chipPress;
        chipLiftX = x;
        chipLiftSince = Now;
        hoverChip = -1;
        Cursor = Cursors.SizeWE;
        Kick();
    }

    void MoveChipLift(float x)
    {
        chipLiftX = x;
        UpdateChipInsert();
        Redraw();
    }

    /// <summary>
    /// 拿着的分类该插到哪：按没留空位时的位置算（免得来回跳），它放在某个分类前面和后面时中心差一个“自己的宽度+间隔”，
    /// 越过那个分类的中心再多半个空位才算到了它后面。
    /// </summary>
    void UpdateChipInsert()
    {
        float s = S;
        LayoutPanel(CapsuleRect);
        var held = chipRects[chipLift];
        float center = held.X + held.Width / 2, gap = chipWidths[chipLift] * s + 6 * s;
        int slot = FixedPages;
        for (int i = FixedPages; i < pages.Count; i++)
        {
            if (i == chipLift) continue;
            var other = chipRects[i];
            float oc = other.X + other.Width / 2;
            int order = i < chipLift ? i : i - 1;
            if (order >= chipInsert) oc -= gap;
            if (oc + gap / 2 < center) slot++;
        }
        if (slot != chipInsert)
        {
            chipInsert = slot;
            LayoutPanel(CapsuleRect);
        }
    }

    /// <summary>拿着分类（或者拿着文件停在分类这一排上）靠近两头：自动往那边滚，排在后面的分类也够得着。</summary>
    void AutoScrollChips(float dt)
    {
        var at = PointToClient(Cursor.Position);
        float s = S, mx = at.X, edge = 30 * s, over = 0;
        if (mx < chipRow.Left + edge) over = mx - (chipRow.Left + edge);
        else if (mx > chipRow.Right - edge) over = mx - (chipRow.Right - edge);
        if (over == 0 || MaxChipScroll <= 0) return;
        float next = Math.Clamp(chipScroll + over / s * 14 * dt, 0, MaxChipScroll);
        if (next == chipScroll) return;
        chipScroll = next;
        if (chipLift >= 0) UpdateChipInsert();
        else
        {
            LayoutPanel(CapsuleRect);
            dropChip = ChipDropTarget(at); // 标签滚过去了：松手放进的是现在鼠标底下那个
            Redraw();
        }
    }

    /// <summary>拿着文件时鼠标在分类那一排上（上下放宽一点）。</summary>
    bool NearChipRow(Point p) => mode == Mode.Open && RectangleF.Inflate(chipRow, 0, 10 * S).Contains(p);

    /// <summary>文件可以放进的分类标签：自动页不能放，拖回本页标签没意义（搜索结果里的可以）。</summary>
    int ChipDropTarget(Point p)
    {
        int chip = ChipAt(p);
        return chip >= 0 && (pages[chip].Fence == null || chip == tab && query.Length == 0) ? -1 : chip;
    }

    void EndChipPress(Point p, bool click)
    {
        chipHold.Stop();
        int press = chipPress, from = chipLift - FixedPages, to = chipInsert - FixedPages;
        bool lifted = chipLift >= 0, scrolled = chipScrolling;
        ChipLog($"end click={click} lifted={lifted} scrolled={scrolled} from={from} to={to} at={p} held={Now - chipPressSince}ms");
        chipPressed = chipScrolling = false;
        chipPress = chipLift = chipInsert = -1;
        Cursor = Cursors.Default;
        if (lifted)
        {
            var movable = pages.Skip(FixedPages).ToList();
            if (from != to && from >= 0 && from < movable.Count && to >= 0 && to < movable.Count)
            {
                var page = movable[from];
                movable.RemoveAt(from);
                movable.Insert(to, page);
                // 分类的顺序存在分类列表里，「图像」只记它排第几
                app.Config.Fences.Clear();
                app.Config.Fences.AddRange(movable.Where(p => p.Fence != null).Select(p => p.Fence!));
                int shots = movable.FindIndex(p => p.Kind == PageKind.Screenshots);
                if (shots >= 0) app.Config.ScreenshotSlot = shots;
                app.ScheduleSave();
            }
            BuildPages(); // 当前选中的分类跟着它走（按分类找，不按位置）
        }
        else if (click && !scrolled && press >= 0 && press == ChipAt(p)) SetTab(press);
        Redraw();
    }

    /// <summary>当前分类的标签露出来（在排外面就滚过去）。</summary>
    void FollowChip()
    {
        if (tab < 0 || tab >= chipRects.Count) return;
        float s = S;
        var r = chipRects[tab];
        if (r.Left < chipRow.Left + 4 * s) chipScroll -= (chipRow.Left + 4 * s - r.Left) / s + 24;
        else if (r.Right > chipRow.Right - 4 * s) chipScroll += (r.Right - chipRow.Right + 4 * s) / s + 24;
        chipScroll = Math.Clamp(chipScroll, 0, MaxChipScroll);
    }

    readonly List<float> chipWidths = new();

    /// <summary>界面语言换了：页签名、胶囊上的字重新排。</summary>
    public void LanguageChanged()
    {
        Text = L.T("玻璃收纳", "GlassBox");
        BuildPages();
        BuildPill();
        if (mode == Mode.Open) RefreshShown(keepScroll: true);
        Redraw();
    }

    void BuildPages()
    {
        var current = CurrentPage;
        pages.Clear();
        pages.Add(new Page(PageKind.Frequent, L.T("常用", "Frequent"), null, app.FrequentItems(FrequentMax).Count));
        pages.Add(new Page(PageKind.Downloads, L.T("最近下载", "Downloads"), null, app.RecentDownloads.Count));
        foreach (var fence in app.Config.Fences) pages.Add(new Page(PageKind.Fence, fence.Name, fence, app.CountOf(fence)));
        // 「图像」和分类排在一起，可以拖到任何位置
        if (app.Config.ScreenshotPage)
            pages.Insert(2 + Math.Clamp(app.Config.ScreenshotSlot, 0, app.Config.Fences.Count), new Page(PageKind.Screenshots, L.T("图像", "Images"), null, app.Screenshots.Count));

        if (current is { } was)
        {
            int same = pages.FindIndex(p => p.Kind == was.Kind && p.Fence == was.Fence);
            tab = same >= 0 ? same : Math.Min(tab, pages.Count - 1);
        }
        else if (tab < 0) tab = pages[0].Count > 0 ? 0 : Math.Min(FixedPages, pages.Count - 1); // 第一次：有常用的就先看常用
        tab = Math.Clamp(tab, 0, pages.Count - 1);

        chipWidths.Clear();
        foreach (var page in pages)
            chipWidths.Add(Measure(page.Name, chipFont) + Measure(page.Count.ToString(), smallFont) + 6 + 24);
    }

    int IndexOfKind(PageKind kind) => pages.FindIndex(p => p.Kind == kind);

    /// <summary>最前面固定的两页（常用、最近下载），后面是可以拖着调顺序的（「图像」和各个分类）。</summary>
    int FixedPages => pages.Count(p => p.Kind is PageKind.Frequent or PageKind.Downloads);

    int IndexOfFence(FenceConfig? fence) => fence == null ? -1 : pages.FindIndex(p => p.Fence == fence);

    RectangleF TileRect(int i) => new(gridLeft + i % columns * CellW * S, gridRect.Y + (i / columns * CellH - scroll) * S, CellW * S, CellH * S);

    void DrawPanel(Graphics g, RectangleF c, float a)
    {
        float s = S;
        LayoutPanel(c);
        bool searching = query.Length > 0;

        // 搜索框
        FillRound(g, searchRect, 12 * s, Ink((focused || hoverSearch ? .16f : .10f) * a));
        float mx = searchRect.X + 19 * s, my = searchRect.Y + searchRect.Height / 2 - 1 * s;
        using (var pen = new Pen(Ink(.7f * a), 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawEllipse(pen, mx - 6 * s, my - 6 * s, 12 * s, 12 * s);
            g.DrawLine(pen, mx + 4.5f * s, my + 4.5f * s, mx + 8.5f * s, my + 8.5f * s);
        }
        var textBox = new RectangleF(searchRect.X + 36 * s, searchRect.Y, searchRect.Width - 46 * s, searchRect.Height);
        float textY = searchRect.Y + searchRect.Height / 2;
        var clip = g.Save();
        g.SetClip(textBox, CombineMode.Intersect);
        if (searching)
        {
            float tw = Measure(query, searchFont) * s;
            float tx = Math.Min(textBox.X, textBox.Right - tw - 4 * s); // 太长时显示末尾
            DrawTight(g, query, searchFont, Ink(a), tx, textY);
            if (focused && caretOn) Fill(g, new RectangleF(tx + tw + 1 * s, textY - 9 * s, 1.5f * s, 18 * s), Ink(.9f * a));
        }
        else
        {
            if (focused && caretOn) Fill(g, new RectangleF(textBox.X, textY - 9 * s, 1.5f * s, 18 * s), Ink(.9f * a));
            string hint = focused ? L.T($"直接输入，搜索 {app.TotalCount} 个文件", $"Type to search {app.TotalCount} files") : L.T($"搜索 {app.TotalCount} 个文件", $"Search {app.TotalCount} files");
            DrawTight(g, hint, searchFont, Ink(.55f * a), textBox.X + (focused ? 5 * s : 0), textY);
        }
        g.Restore(clip);
        DrawFocusButton(g, a);
        DrawGear(g, a);

        // 时间（番茄钟在走时，日期那行换成还剩多久）
        var now = DateTime.Now;
        clockShown = now.ToString("HH:mm");
        var focusLeft = FocusLeft < TimeSpan.Zero ? TimeSpan.Zero : FocusLeft;
        string left = $"{(int)focusLeft.TotalMinutes:00}:{focusLeft.Seconds:00}";
        string date = focusEnd != null ? L.T($"{(focusBreak ? "休息" : "专注")} 还剩 {left}", $"{(focusBreak ? "Break" : "Focus")} {left} left")
            : L.En ? now.ToString("ddd, MMM d", System.Globalization.CultureInfo.InvariantCulture)
            : $"{now.Month}月{now.Day}日 周{"日一二三四五六"[(int)now.DayOfWeek]}";
        float right = c.Right - 18 * s;
        DrawTight(g, clockShown, clockFont, Ink(a), right - Measure(clockShown, clockFont) * s, searchRect.Y + 13 * s);
        DrawTight(g, date, smallFont, Ink(.65f * a), right - Measure(date, smallFont) * s, searchRect.Y + 32 * s);

        // 分类
        if (chipFollow)
        {
            chipFollow = false;
            FollowChip();
            LayoutPanel(c);
        }
        chipScroll = Math.Clamp(chipScroll, 0, MaxChipScroll);
        clip = g.Save();
        g.SetClip(chipRow, CombineMode.Intersect);
        for (int i = 0; i < chipRects.Count; i++)
        {
            if (i == chipLift) continue;
            var r = chipRects[i];
            if (r.Right < chipRow.Left) continue;
            if (r.Left > chipRow.Right) break;
            bool on = !searching && i == tab;
            if (on) FillRound(g, r, r.Height / 2, Ink(.92f * a));
            else if (i == dropChip) FillRound(g, r, r.Height / 2, Ink(.26f * a));
            else if (i == hoverChip) FillRound(g, r, r.Height / 2, Ink(.10f * a));
            if (i == chipPress && ChipHolding)
            {
                // 按住等拿起来：从左往右慢慢涨满
                float hold = Math.Clamp((Now - chipPressSince) / (float)chipHold.Interval, 0, 1);
                FillRound(g, new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * hold), r.Height), r.Height / 2, on ? Color.FromArgb(Byte(.16f * a), 0, 0, 0) : Ink(.22f * a));
            }
            string name = pages[i].Name, count = pages[i].Count.ToString();
            float nameW = Measure(name, chipFont) * s;
            float ty = r.Y + r.Height / 2;
            DrawTight(g, name, chipFont, on ? Deep(a) : Ink(.85f * a), r.X + 12 * s, ty);
            DrawTight(g, count, smallFont, on ? Deep(.6f * a) : Ink(.55f * a), r.X + 12 * s + nameW + 6 * s, ty + 1 * s);
            if (i == FixedPages - 1) Fill(g, new RectangleF(r.Right + 8 * s, r.Y + 8 * s, Math.Max(1, s), r.Height - 16 * s), Ink(.25f * a));
        }
        if (chipLift >= 0 && chipLift < chipRects.Count)
        {
            // 拿在手里的分类：浮起来一点，带一圈影子
            float pop = 1 - MathF.Pow(1 - Math.Clamp((Now - chipLiftSince) / 160f, 0, 1), 3); // 拿起来那一下轻轻弹大
            var r = RectangleF.Inflate(chipRects[chipLift], 2 * pop * s, 1 * pop * s);
            r.Offset(0, -2 * pop * s);
            FillRound(g, RectangleF.Inflate(r, 2 * s, 2 * s), r.Height / 2 + 2 * s, Color.FromArgb(Byte(.28f * a), 0, 0, 0));
            FillRound(g, r, r.Height / 2, Ink(.95f * a));
            string name = pages[chipLift].Name, count = pages[chipLift].Count.ToString();
            float nameW = Measure(name, chipFont) * s;
            DrawTight(g, name, chipFont, Deep(a), r.X + 12 * s, r.Y + r.Height / 2);
            DrawTight(g, count, smallFont, Deep(.6f * a), r.X + 12 * s + nameW + 6 * s, r.Y + r.Height / 2 + 1 * s);
        }
        g.Restore(clip);
        // 两头还有没露出来的标签：边上淡一点提示可以滚
        if (chipScroll > 0.5f) FadeEdge(g, new RectangleF(chipRow.Left, chipRow.Top, 18 * s, chipRow.Height), left: true, a);
        if (chipScroll < MaxChipScroll - 0.5f) FadeEdge(g, new RectangleF(chipRow.Right - 18 * s, chipRow.Top, 18 * s, chipRow.Height), left: false, a);

        // 图标
        clip = g.Save();
        g.SetClip(gridRect, CombineMode.Intersect);
        if (shown.Count == 0)
        {
            string empty = searching ? L.T($"没有找到“{query}”", $"No results for “{query}”") : CurrentPage?.Kind switch
            {
                PageKind.Frequent => L.T("常打开的文件会自动出现在这里", "Files you open often show up here"),
                PageKind.Downloads => L.T("最近 30 天下载的东西会自动出现在这里", "Downloads from the last 30 days show up here"),
                PageKind.Screenshots => L.T("截的图（Win+Shift+S、PrtSc）会自动出现在这里", "Screenshots (Win+Shift+S, PrtSc) show up here"),
                _ => L.T("这里还是空的，把文件拖到胶囊上试试", "Nothing here yet. Try dragging files onto the capsule"),
            };
            using var brush = new SolidBrush(Ink(.62f * a));
            g.DrawString(empty, labelFont, brush, gridRect, CenterFormat);
            layoutMoving = false;
        }
        else DrawGrid(g, a);
        g.Restore(clip);
        bool trashOn = TrashVisible;
        if (trashOn) DrawTrash(g, c, a);
        if (liftPath != null) DrawLifted(g, a); // 拿在手里的可以拖到分类标签上，不裁在网格里

        if (MaxScroll > 0)
        {
            float track = gridRect.Height, content = track / S + MaxScroll;
            float thumb = Math.Max(24 * s, track * (track / S) / content);
            float y = gridRect.Y + (track - thumb) * scroll / MaxScroll;
            FillRound(g, new RectangleF(c.Right - 9 * s, y, 3 * s, thumb), 1.5f * s, Ink(.3f * a));
        }

        // 底部：在放歌就是迷你播放器，不然是提示
        float fy = c.Bottom - 22 * s;
        string keys = L.T("空格预览 · Enter 打开 · 连按 Ctrl 呼出", "Space preview · Enter open · Ctrl twice to summon");
        float keysW = Measure(keys, smallFont) * s;
        bool showKeys = c.Width > 600 * s && !trashOn; // 右下角的回收站条子出来时让开
        bool toast = Now < trashToastUntil;
        if (toast)
        {
            Array.Clear(mediaButtons);
            playerInfo = RectangleF.Empty;
            DrawTrashToast(g, c, a);
        }
        else if (trashOn)
        {
            // 拿着图标：迷你播放器（进度条、按钮）先让开，免得挡着回收站
            Array.Clear(mediaButtons);
            playerInfo = RectangleF.Empty;
        }
        else if (media != null) DrawPlayer(g, new RectangleF(c.X + 16 * s, fy - 13 * s, (showKeys ? c.Width - keysW - 56 * s : c.Width - 32 * s), 26 * s), a);
        else
        {
            Array.Clear(mediaButtons);
            playerInfo = RectangleF.Empty;
            string hint = CanReorder ? L.T("拖动图标可以调整顺序，拖到分类上就换分类", "Drag icons to reorder, drop on a category to move") : L.T("把文件拖到胶囊上，会自动收进对应分类", "Drag files onto the capsule to file them by type");
            DrawTight(g, hint, smallFont, Ink(.55f * a), c.X + 20 * s, fy);
        }
        if (!toast) trashUndoRect = RectangleF.Empty;
        if (showKeys) DrawTight(g, keys, smallFont, Ink(.55f * a), c.Right - 20 * s - keysW, fy);

        if (previewItem != null) DrawPreview(g, c, a);
        if (settingsOpen) DrawSettings(g, c, a);
    }

    void FadeEdge(Graphics g, RectangleF r, bool left, float a)
    {
        var tint = Deep(1);
        using var brush = new LinearGradientBrush(r, Color.FromArgb(Byte(.0f), tint), Color.FromArgb(Byte(.0f), tint), LinearGradientMode.Horizontal);
        var blend = new ColorBlend
        {
            Colors = left
                ? new[] { Color.FromArgb(Byte(.5f * a), tint), Color.FromArgb(0, tint) }
                : new[] { Color.FromArgb(0, tint), Color.FromArgb(Byte(.5f * a), tint) },
            Positions = new[] { 0f, 1f },
        };
        brush.InterpolationColors = blend;
        g.FillRectangle(brush, r);
    }

    // ---------- 图标网格（带拖动排序的滑动） ----------

    /// <summary>正在拖的图标在这页里排第几（不在这页、不能排序就是 -1）。</summary>
    int DraggedIndex => app.DraggingPath == null || !CanReorder ? -1
        : shown.FindIndex(i => i.Path.Equals(app.DraggingPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>图标该排在第几格：正在拖的那个让出位置，要放下的地方空出一格。</summary>
    int SlotOf(int i, int dragged)
    {
        int slot = dragged >= 0 && i > dragged ? i - 1 : i;
        return dropIndex >= 0 && slot >= dropIndex ? slot + 1 : slot;
    }

    PointF SlotPoint(int slot) => new(slot % columns * CellW, slot / columns * CellH);

    /// <summary>鼠标在网格里的这个位置松手，会插到第几个（按“拿掉正在拖的那个之后”算）。</summary>
    int InsertIndex(Point p)
    {
        float s = S;
        int count = shown.Count - (DraggedIndex >= 0 ? 1 : 0);
        int col = Math.Clamp((int)((p.X - gridLeft) / (CellW * s)), 0, columns - 1);
        int row = Math.Max(0, (int)((p.Y - gridRect.Y + scroll * s) / (CellH * s)));
        return Math.Clamp(row * columns + col, 0, count);
    }

    void DrawGrid(Graphics g, float a)
    {
        float s = S;
        int dragged = DraggedIndex;
        float k = animate ? 1 - MathF.Exp(-frameDt * 16) : 1; // 每帧往目标位置靠一点，看起来是“滑”过去的
        float kz = animate ? 1 - MathF.Exp(-frameDt * 18) : 1;
        bool anyMoving = false;

        // 放大镜的中心：鼠标在网格里就跟着鼠标；用方向键选的时候跟着选中的那个；拖动、预览时不放大
        PointF? magnet = null;
        if (mode == Mode.Open && dragged < 0 && liftPath == null && dropIndex < 0 && previewItem == null && !settingsOpen)
        {
            var mouse = PointToClient(Cursor.Position);
            if (keyboardPicked && selected >= 0 && selected < shown.Count)
            {
                var p = SlotPoint(SlotOf(selected, dragged));
                magnet = new PointF(gridLeft + (p.X + CellW / 2) * s, gridRect.Y + (p.Y - scroll + 10) * s + app.Icons.Size / 2f);
            }
            else if (gridRect.Contains(mouse)) magnet = mouse;
        }
        float reach = CellW * s * 1.5f;

        tileDraws.Clear();
        for (int i = 0; i < shown.Count; i++)
        {
            // 被拖着的那个跟着鼠标走（面板里自己画，拖出去是系统的拖动图），原位置空着
            if (i == dragged || liftPath != null && shown[i].Path.Equals(liftPath, StringComparison.OrdinalIgnoreCase)) continue;
            var target = SlotPoint(SlotOf(i, dragged));
            var path = shown[i].Path;
            var pos = tilePos.TryGetValue(path, out var was) ? was : target;
            if (Math.Abs(pos.X - target.X) > .5f || Math.Abs(pos.Y - target.Y) > .5f)
            {
                pos = new PointF(pos.X + (target.X - pos.X) * k, pos.Y + (target.Y - pos.Y) * k);
                anyMoving = true;
            }
            else pos = target;
            tilePos[path] = pos;

            var r = new RectangleF(gridLeft + pos.X * s, gridRect.Y + (pos.Y - scroll) * s, CellW * s, CellH * s);

            // 离鼠标越近放得越大（平滑地衰减到一格半之外）
            var center = new PointF(r.X + r.Width / 2, r.Y + 10 * s + app.Icons.Size / 2f);
            float want = 1;
            float dx = 0, dy = 0, d = 0;
            if (magnet is { } m)
            {
                dx = center.X - m.X;
                dy = center.Y - m.Y;
                d = MathF.Sqrt(dx * dx + dy * dy);
                float f = Math.Clamp(1 - d / reach, 0, 1);
                want = 1 + ZoomMax * f * f * (3 - 2 * f);
            }
            float zoom = tileZoom.TryGetValue(path, out var z0) ? z0 : 1;
            if (Math.Abs(want - zoom) > .002f)
            {
                zoom += (want - zoom) * kz;
                anyMoving = true;
            }
            else zoom = want;
            if (zoom == 1) tileZoom.Remove(path);
            else tileZoom[path] = zoom;

            // 旁边的图标往外让一点：正中间那个（放到最大）和远处的都不动
            float zt = ZoomT(zoom);
            if (zt > 0 && d > 1)
            {
                float push = 7 * s * 4 * zt * (1 - zt) / d;
                r.Offset(dx * push, dy * push);
            }
            if (r.Bottom < gridRect.Top || r.Top > gridRect.Bottom) continue;
            tileDraws.Add((i, r, zoom));
        }
        // 放得最大的最后画，压在邻居上面
        int top = -1;
        for (int j = 0; j < tileDraws.Count; j++)
            if (tileDraws[j].Zoom > 1.001f && (top < 0 || tileDraws[j].Zoom > tileDraws[top].Zoom)) top = j;
        for (int j = 0; j < tileDraws.Count; j++)
            if (j != top) DrawTile(g, tileDraws[j].Index, tileDraws[j].Rect, a, tileDraws[j].Zoom);
        if (top >= 0)
        {
            // 第一排放大后会顶出网格上沿一点：只给这一个放宽裁剪，滚上去一半的那排不受影响
            var (index, rect, zoom) = tileDraws[top];
            var state = g.Save();
            if (rect.Top >= gridRect.Top - 1 * s)
            {
                var room = gridRect;
                room.Y -= 12 * s;
                room.Height += 12 * s;
                g.SetClip(room, CombineMode.Replace); // 网格上面 12px 还在胶囊里面，不会画出圆角外
            }
            DrawTile(g, index, rect, a, zoom);
            g.Restore(state);
        }
        if (dropIndex >= 0 && app.DraggingPath != null || dropIndex >= 0 && dropFiles.Length > 0)
        {
            // 要放下的那一格：淡淡的虚框
            var p = SlotPoint(dropIndex);
            var slot = new RectangleF(gridLeft + p.X * s + 6 * s, gridRect.Y + (p.Y - scroll) * s + 6 * s, CellW * s - 12 * s, CellH * s - 12 * s);
            using var pen = new Pen(Ink(.45f * a), 1.4f * s) { DashStyle = DashStyle.Dash };
            using var shape = Shapes.RoundRect(slot, 14 * s);
            g.DrawPath(pen, shape);
        }
        layoutMoving = anyMoving;
    }

    // ---------- 音乐卡片 ----------

    const float MusicPad = 18, MusicArtH = 148, MusicArtMaxW = 264, MusicInfoW = 240, MusicLyricsW = 290, MusicRowH = 34;
    static readonly int[] SleepMinutes = { 0, 15, 30, 60 };
    static readonly StringFormat WrapFormat = new(StringFormatFlags.LineLimit) { Trimming = StringTrimming.EllipsisWord };

    /// <summary>鼠标停上去（或点）展开成什么：在放歌就是音乐卡片，不然是文件面板。</summary>
    Mode HoverTarget => media is { Playing: true } && app.Config.ShowNowPlaying && app.Config.MusicCard && floatWin == null ? Mode.Music : Mode.Open;

    /// <summary>现在要不要一直出帧：唱片在转、音柱在跳、歌词在走。胶囊缩着、被窗口挡着、全屏时都不动。</summary>
    bool MusicLive => (media is { Playing: true } || media != null && TurntableMoving) && Visible && !Tucked && !fullscreenBusy && (!Focusing || mode == Mode.Music)
        && (mode == Mode.Music || mode == Mode.Idle && app.Config.MusicMotion && app.Config.ShowNowPlaying && (uncovered || Floating));

    /// <summary>封面框的宽：按封面本来的比例（视频是宽的），太宽太窄都收一点。</summary>
    float MusicArtW
    {
        get
        {
            if (app.Config.Turntable) return MusicArtH * 1.12f; // 唱片机：右边留出唱臂的位置
            var cover = media?.Cover;
            float aspect = cover != null && cover.Height > 0 ? cover.Width / (float)cover.Height : 1;
            return Math.Clamp(MusicArtH * aspect, MusicArtH * .75f, MusicArtMaxW);
        }
    }

    (float W, float H) MusicSize()
    {
        float w = MusicPad + MusicArtW + 20 + MusicInfoW + (app.Config.Lyrics ? 24 + MusicLyricsW : 0) + MusicPad;
        return (Math.Min(w, Area.Width / S - 24), MusicPad + MusicArtH + 14 + MusicRowH + 12);
    }

    void EnterMusic()
    {
        CloseFloat();
        musicLast = true;
        panelLast = false;
        seekDrag = volumeDrag = false;
        lyricIndex = -1;
        ReadVolume();
        EnsureLyrics();
        if (app.Config.BluetoothBattery) ReadBluetooth();
    }

    void ReadBluetooth()
    {
        btRead = Now;
        if (btReading) return;
        btReading = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            List<(string, int)> levels;
            try { levels = BluetoothBattery.Read(); }
            catch (Exception ex)
            {
                Log.Error(ex);
                levels = new();
            }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    btReading = false;
                    btLevels = levels;
                    Redraw();
                }));
            }
            catch (InvalidOperationException) { } // 正在退出
        });
    }

    void LeaveMusic()
    {
        seekDrag = volumeDrag = cardPressed = false;
        if (musicRaised)
        {
            musicRaised = false;
            sinkPending = true; // Ctrl 叫出来浮在最上面的：缩回去后沉回桌面层
        }
    }

    void ReadVolume()
    {
        volumeRead = Now;
        var v = media == null ? null : Audio.GetVolume(media.Source);
        volume = v?.Volume ?? -1;
        volumePerApp = v?.PerApp ?? false;
        output = Audio.DefaultOutput();
    }

    /// <summary>歌词开着、这首歌还没找过：后台去找（先看本地缓存），找到了重画。</summary>
    void EnsureLyrics()
    {
        if (!app.Config.Lyrics || media == null) return;
        string key = media.Title + "\n" + media.Artist;
        if (key == lyricsKey) return;
        if (media.Settling) return; // 刚换歌、时长还没报：等报了再找（找歌词要按时长挑版本）
        FindLyrics(key, 0, null);
    }

    /// <summary>右键“换一个版本”“手动搜索”：不看缓存，重新找，挑中的存起来。</summary>
    void RefindLyrics(int choice, string? search)
    {
        if (!app.Config.Lyrics || media == null) return;
        FindLyrics(media.Title + "\n" + media.Artist, choice, search);
    }

    void FindLyrics(string key, int choice, string? search)
    {
        lyricsKey = key;
        lyrics = null;
        lyricsLoading = true;
        lyricsOffline = false;
        lyricIndex = -1;
        lyricsCancel?.Cancel();
        var cancel = lyricsCancel = new CancellationTokenSource();
        var query = new LyricsQuery(media!.Title, media.Artist, media.Duration, app.Config.NeteaseLyrics, choice, search,
            app.Config.LyricTranslation && app.Config.NeteaseLyrics);
        Task.Run(async () =>
        {
            SongLyrics? found = null;
            bool offline = false;
            try { found = await LyricsSource.FindAsync(query, cancel.Token); }
            catch (OperationCanceledException) { return; }
            catch (LyricsUnavailableException) { offline = true; }
            catch (Exception ex) { Log.Error(ex); }
            if (cancel.IsCancellationRequested || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (lyricsKey != key) return;
                    lyrics = found;
                    lyricsLoading = false;
                    lyricsOffline = offline;
                    if (offline) lyricsKey = ""; // 下次打开卡片再试
                    lyricIndex = pillLyricIndex = -1;
                    BuildPill(); // 收起时滚歌词：找到了就换上
                    UpdateFloat();
                    Redraw();
                }));
            }
            catch (InvalidOperationException) { } // 窗口已经关了
        });
    }

    /// <summary>
    /// 音柱：读系统输出的音量峰值。歌一直都挺响，直接用峰值柱子会一直顶满，
    /// 所以看它比最近一秒的平均响多少（鼓点）；四根各按自己的节奏摆，起得快落得慢；没声音就趴下。
    /// </summary>
    void UpdateBars()
    {
        float peak = Audio.Peak();
        peakAvg += (peak - peakAvg) * .06f;
        float beat = peak < .01f ? 0 : Math.Clamp(.42f + (peak - peakAvg) * 3.2f, .12f, 1);
        float t = Now / 1000f;
        for (int i = 0; i < barLevels.Length; i++)
        {
            float wobble = .55f + .45f * MathF.Abs(MathF.Sin(t * (5.3f + i * 2.1f) + i * 1.7f));
            float want = beat * wobble;
            barLevels[i] += (want - barLevels[i]) * (want > barLevels[i] ? .6f : .22f);
        }
    }

    void DrawMusic(Graphics g, RectangleF c, float a)
    {
        float s = S;
        musicHits.Clear();
        playerDrawn = Now;
        var np = media;
        if (np == null)
        {
            using var brush = new SolidBrush(Ink(.6f * a));
            g.DrawString(L.T("现在没有在播放", "Nothing playing"), labelFont, brush, c, CenterFormat);
            return;
        }
        var mouse = PointToClient(Cursor.Position);
        float pad = MusicPad * s;
        // 卡片上小字多：玻璃调得很透、后面又是白色窗口时会看不清，按透明度铺一层薄底
        float clear = Math.Clamp((app.Config.GlassClarity - 40) / 60f, 0, 1);
        if (clear > 0 && !lightInk) Fill(g, c, app.Tone.DarkInk ? Color.FromArgb(Byte(.32f * clear * a), 255, 255, 255) : Deep(.45f * clear * a));

        // 封面：按比例铺满框，多出来的裁掉
        var art = new RectangleF(c.X + pad, c.Y + pad, MusicArtW * s, MusicArtH * s);
        DrawArt(g, art, np, np.Cover ?? np.Art, a);

        // 中间：歌名、歌手、时间、进度、按钮
        float x0 = art.Right + 20 * s, iw = MusicInfoW * s;
        var (songTitle, songArtist) = Pretty(np);
        DrawTight(g, Fit(songTitle, titleFont, iw), titleFont, Ink(a), x0, art.Y + 16 * s);
        if (songArtist.Length > 0) DrawTight(g, Fit(songArtist, labelFont, iw), labelFont, Ink(.62f * a), x0, art.Y + 41 * s);

        var position = seekDrag && np.Duration > TimeSpan.Zero ? TimeSpan.FromSeconds(seekValue * np.Duration.TotalSeconds) : np.CurrentPosition;
        var (accent, amount) = Accent();
        if (np.Duration > TimeSpan.Zero)
        {
            float ty = art.Bottom - 66 * s;
            DrawTight(g, Clock(position), smallFont, Ink(.68f * a), x0, ty);
            string rest = "-" + Clock(np.Duration - position);
            DrawTight(g, rest, smallFont, Ink(.68f * a), x0 + iw - Measure(rest, smallFont) * s, ty);
            var track = new RectangleF(x0, art.Bottom - 50 * s, iw, 4 * s);
            seekTrack = track;
            var hit = RectangleF.Inflate(track, 6 * s, 10 * s);
            bool hot = seekDrag || hit.Contains(mouse);
            if (hot) track.Inflate(0, 1 * s);
            float progress = (float)Math.Clamp(position.TotalSeconds / np.Duration.TotalSeconds, 0, 1);
            FillRound(g, track, track.Height / 2, Ink(.18f * a));
            var fill = Color.FromArgb(Byte(.92f * a), Mix(Ink(1), Mix(accent, Color.White, .2f), amount));
            FillRound(g, new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * progress), track.Height), track.Height / 2, fill);
            if (hot) Knob(g, track.X + track.Width * progress, track.Y + track.Height / 2, 6.5f * s, a);
            musicHits.Add((hit, "seek", 0));
        }

        float by = art.Bottom - 15 * s, slot = iw / 4;
        for (int i = 0; i < 4; i++)
        {
            var b = new RectangleF(x0 + i * slot, by - 16 * s, slot, 32 * s);
            var round = new RectangleF(b.X + b.Width / 2 - 19 * s, b.Y, 38 * s, b.Height);
            bool on = i == 0 && app.Config.Lyrics;
            if (on) FillRound(g, round, 16 * s, Ink(.2f * a));
            else if (b.Contains(mouse)) FillRound(g, round, 16 * s, Ink(.12f * a));
            using var brush = new SolidBrush(Ink(.92f * a));
            float cx = b.X + b.Width / 2;
            switch (i)
            {
                case 0: // 歌词：三行长短不一的字
                    FillRound(g, new RectangleF(cx - 7 * s, by - 5.5f * s, 14 * s, 2 * s), 1 * s, Ink(.92f * a));
                    FillRound(g, new RectangleF(cx - 7 * s, by - 1 * s, 9 * s, 2 * s), 1 * s, Ink(.92f * a));
                    FillRound(g, new RectangleF(cx - 7 * s, by + 3.5f * s, 12 * s, 2 * s), 1 * s, Ink(.92f * a));
                    break;
                case 1: Triangle(g, brush, cx + 3 * s, by, -1, 7 * s); g.FillRectangle(brush, cx - 7.5f * s, by - 6 * s, 2 * s, 12 * s); break;
                case 2:
                    if (np.Playing)
                    {
                        FillRound(g, new RectangleF(cx - 6 * s, by - 8 * s, 4 * s, 16 * s), 1.5f * s, Ink(.95f * a));
                        FillRound(g, new RectangleF(cx + 2 * s, by - 8 * s, 4 * s, 16 * s), 1.5f * s, Ink(.95f * a));
                    }
                    else Triangle(g, brush, cx - 2 * s, by, 1, 9 * s);
                    break;
                case 3: Triangle(g, brush, cx - 3 * s, by, 1, 7 * s); g.FillRectangle(brush, cx + 5.5f * s, by - 6 * s, 2 * s, 12 * s); break;
            }
            musicHits.Add((b, i switch { 0 => "lyrics", 1 => "prev", 2 => "play", _ => "next" }, 0));
        }

        // 右边：歌词
        if (app.Config.Lyrics)
        {
            float x1 = x0 + iw + 24 * s;
            var box = new RectangleF(x1, art.Y, c.Right - pad - x1, art.Height);
            if (box.Width > 60 * s) DrawLyrics(g, box, position, a);
        }

        // 右上角的小锁：鼠标在卡片上时浮出来，锁着时一直亮着（双击卡片也一样）
        lockRect = new RectangleF(c.Right - 36 * s, c.Y + 8 * s, 28 * s, 28 * s);
        bool lockHot = lockRect.Contains(mouse);
        float lockAlpha = musicPinned ? 1 : c.Contains(mouse) ? (lockHot ? .9f : .42f) : 0;
        if (lockAlpha > 0)
        {
            if (musicPinned) FillRound(g, lockRect, 14 * s, Ink(.9f * a));
            else if (lockHot) FillRound(g, lockRect, 14 * s, Ink(.14f * a));
            DrawLock(g, lockRect.X + lockRect.Width / 2, lockRect.Y + lockRect.Height / 2, musicPinned, musicPinned ? Deep(a) : Ink(lockAlpha * a));
            musicHits.Add((lockRect, "lock", 0));
        }

        // 底下一排：音量（和现在从哪个设备出声）、定时暂停、切换来源、蓝牙耳机电量、跳过广告、回到文件
        float rcy = art.Bottom + 14 * s + MusicRowH * s / 2;
        float x = c.X + pad;
        if (output is { Headphones: true }) DrawHeadphones(g, x - 1 * s, rcy, Ink(.8f * a));
        else DrawSpeaker(g, x + 7 * s, rcy, a);
        x += 22 * s;
        var vt = new RectangleF(x, rcy - 2 * s, 110 * s, 4 * s);
        volumeTrack = vt;
        if (volume >= 0)
        {
            var hit = RectangleF.Inflate(vt, 6 * s, 13 * s);
            bool hot = volumeDrag || hit.Contains(mouse);
            if (hot) vt.Inflate(0, 1.5f * s); // 鼠标过来：变粗一点
            FillRound(g, vt, vt.Height / 2, Ink(.18f * a));
            var level = new RectangleF(vt.X, vt.Y, Math.Max(vt.Height, vt.Width * volume), vt.Height);
            if (amount > .05f) FillRound(g, RectangleF.Inflate(level, 2.5f * s, 2.5f * s), level.Height, Color.FromArgb(Byte((hot ? .3f : .16f) * amount * a), accent)); // 一层歌曲颜色的光
            FillRound(g, level, vt.Height / 2, Color.FromArgb(Byte(.92f * a), Mix(Ink(1), Mix(accent, Color.White, .25f), amount)));
            if (hot) Knob(g, vt.X + vt.Width * volume, rcy, 6.5f * s, a);
            musicHits.Add((hit, "volume", 0));
            string pct = $"{(int)Math.Round(volume * 100)}%" + (volumePerApp ? "" : L.T(" 系统", " system"));
            DrawTight(g, pct, smallFont, Ink(.65f * a), vt.Right + 10 * s, rcy);
            x = vt.Right + 10 * s + Measure(pct, smallFont) * s + 12 * s;
        }
        else
        {
            FillRound(g, vt, 2 * s, Ink(.18f * a));
            x = vt.Right + 18 * s;
        }

        string files = L.T("文件", "Files");
        float fw = Measure(files, chipFont) * s + 40 * s;
        var fb = new RectangleF(c.Right - pad - fw, rcy - 13 * s, fw, 26 * s);
        FillRound(g, fb, 13 * s, fb.Contains(mouse) ? Ink(.18f * a) : Ink(.09f * a));
        for (int q = 0; q < 4; q++) // 四个小方块：文件面板
            FillRound(g, new RectangleF(fb.X + 12 * s + q % 2 * 5 * s, rcy - 4.5f * s + q / 2 * 5 * s, 4 * s, 4 * s), 1 * s, Ink(.8f * a));
        DrawTight(g, files, chipFont, Ink(.85f * a), fb.X + 26 * s, rcy);
        musicHits.Add((fb, "files", 0));
        float limit = fb.X - 8 * s;

        // YouTube 广告能跳过了：一颗亮着的按钮
        if (adSkippable)
        {
            string skip = L.T("跳过广告", "Skip ad");
            float kw = Measure(skip, chipFont) * s + 36 * s;
            var kb = new RectangleF(limit - kw, rcy - 13 * s, kw, 26 * s);
            FillRound(g, kb, 13 * s, Ink(.92f * a));
            DrawSkip(g, kb.X + 15 * s, rcy, Deep(a));
            DrawTight(g, skip, chipFont, Deep(a), kb.X + 26 * s, rcy);
            musicHits.Add((kb, "skipad", 0));
            limit = kb.X - 8 * s;
        }

        // 现在从哪里出声：“WH-1000XM5 · 76%”（蓝牙耳机报了电量就带上）；开了切换的话是一颗能点的小胶囊
        string? matched = null;
        if (output is { } device)
        {
            string name = DeviceName(device);
            int battery = -1;
            if (app.Config.BluetoothBattery)
                foreach (var (btName, percent) in btLevels)
                    if (device.Name.Contains(btName, StringComparison.OrdinalIgnoreCase) || btName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        battery = percent;
                        matched = btName;
                        break;
                    }
            string text = battery >= 0 ? $"{name} · {battery}%" : name;
            if (app.Config.OutputSwitch)
            {
                float used = MusicChip(g, x, rcy, text, "output", 0, false, mouse, a, limit - 120 * s);
                if (used > 0) x += used + 8 * s;
            }
            else if (x + Measure(text, smallFont) * s < limit - 120 * s)
            {
                var color = battery is >= 0 and <= 20 ? Color.FromArgb(Byte(a), 248, 113, 113) : Ink(.55f * a);
                DrawTight(g, text, smallFont, color, x, rcy);
                x += Measure(text, smallFont) * s + 14 * s;
            }
        }

        // 别的蓝牙耳机电量（不是现在出声的那副）：排在右边；地方不够先省掉名字，再不够就不显示
        if (app.Config.BluetoothBattery)
            foreach (var (name, percent) in btLevels.Where(b => b.Name != matched).Take(2))
            {
                float taken = DrawEarbuds(g, limit, rcy, name, percent, x + 120 * s, a);
                if (taken <= 0) break;
                limit -= taken + 10 * s;
            }

        var sleep = app.Media?.SleepAtUtc;
        string sleepText = sleep is { } at ? L.T($"{Clock(at - DateTime.UtcNow)} 后暂停", $"Pause in {Clock(at - DateTime.UtcNow)}") : L.T("定时暂停", "Sleep timer");
        float chip = MusicChip(g, x, rcy, sleepText, "sleep", 0, sleep != null, mouse, a, limit);
        if (chip > 0) x += chip + 8 * s;
        if (np.Sources.Length > 1)
        {
            for (int i = 0; i < np.Sources.Length && i < 4; i++)
            {
                chip = MusicChip(g, x, rcy, AppName(np.Sources[i]), "source", i, np.Sources[i] == np.Source, mouse, a, limit);
                if (chip < 0) break;
                x += chip + 6 * s;
            }
        }
        else
        {
            string from = L.T("来自 ", "from ") + AppName(np.Source);
            if (x + Measure(from, smallFont) * s < limit) DrawTight(g, from, smallFont, Ink(.5f * a), x + 4 * s, rcy);
        }

        DrawVolumeBubble(g, art, a);
    }

    /// <summary>封面：按比例铺满框，多出来的裁掉；没有封面画一个音符。</summary>
    void DrawCover(Graphics g, RectangleF art, Bitmap? cover, float a)
    {
        float s = S;
        if (cover != null)
        {
            var state = g.Save();
            using (var shape = Shapes.RoundRect(art, 14 * s))
            {
                g.SetClip(shape, CombineMode.Intersect);
                float k = Math.Max(art.Width / cover.Width, art.Height / cover.Height);
                float dw = cover.Width * k, dh = cover.Height * k;
                DrawImage(g, cover, new RectangleF(art.X + (art.Width - dw) / 2, art.Y + (art.Height - dh) / 2, dw, dh), a);
            }
            g.Restore(state);
            using var edge = new Pen(Color.FromArgb(Byte(.18f * a), 255, 255, 255), 1 * s);
            using var outline = Shapes.RoundRect(RectangleF.Inflate(art, -.5f * s, -.5f * s), 14 * s);
            g.DrawPath(edge, outline);
        }
        else
        {
            FillRound(g, art, 14 * s, Ink(.12f * a));
            using var brush = new SolidBrush(Ink(.5f * a));
            g.DrawString("♪", clockFont, brush, art, CenterFormat);
        }
    }

    // ---------- 唱片机（设置里打开后代替方封面） ----------

    float turnAngle, turnSpeed, armPos; // 唱片转到的角度、转速（0 停 … 1 全速）、唱臂（0 搁着 … 1 落在唱片上）
    double turnLast = -1;               // 上一帧的时间（毫秒，高精度：系统的毫秒计数一跳 16ms，转起来会一顿一顿）
    volatile bool pacerHalf;            // 只有唱片在转：跟着屏幕刷新出帧，但每秒 30 帧就够（均匀比帧多更顺）

    static double FineNow => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>唱片机正在卡片上转、唱臂在动（桌面小播放器有自己的计时器）。</summary>
    bool TurntableShown => app.Config.Turntable && mode == Mode.Music && (turnSpeed > .005f || armPos > .001f && armPos < .999f || media is { Playing: true });
    Bitmap? deckCache;                  // 底座、唱盘、沟槽、反光：不动的部分画一次存着
    (int W, int H, int Tint) deckKey;

    /// <summary>唱片还在慢慢停、唱臂还在抬：暂停后也要再出几帧。</summary>
    bool TurntableMoving => app.Config.Turntable && (turnSpeed > .005f || armPos > .001f && armPos < .999f);

    /// <summary>卡片上的封面：唱片机打开了就画唱片机，不然画方封面。</summary>
    void DrawArt(Graphics g, RectangleF art, NowPlaying np, Bitmap? cover, float a)
    {
        if (app.Config.Turntable) DrawTurntable(g, art, cover, np.Playing, a);
        else DrawCover(g, art, cover, a);
    }

    /// <summary>
    /// 唱片机：深色拉丝底座，金属唱盘上一张黑胶，封面做中间的标签跟着转；反光不跟着转（灯在外面），
    /// 所以转起来像真的。放歌时唱臂落下、唱片起转；暂停时唱片慢慢停、唱臂抬回支架。
    /// </summary>
    void DrawTurntable(Graphics g, RectangleF art, Bitmap? cover, bool playing, float a)
    {
        float s = S;
        // 动画：按真实经过的时间走；很久没画（卡片刚打开）就直接到位，不从头演一遍
        double now = FineNow;
        bool first = turnLast < 0; // 头一次画：直接到位
        float dt = first ? 0 : (float)(now - turnLast);
        turnLast = now;
        float want = playing ? 1 : 0;
        if (dt > 400) dt = 16; // 停了一阵又画（暂停后按播放）：从现在的样子接着演
        if (first || !animate)
        {
            turnSpeed = armPos = want;
        }
        else
        {
            // 放歌：唱臂先落（0.7 秒），落到了唱片才起转；暂停：唱片 1.2 秒慢慢停，唱臂同时抬起
            armPos = Math.Clamp(armPos + (playing ? 1 : -1) * dt / 700f, 0, 1);
            float target = playing && armPos >= 1 ? 1 : 0;
            turnSpeed += (target - turnSpeed) * Math.Min(1, dt / (target > turnSpeed ? 450f : 380f));
            if (turnSpeed < .004f && target == 0) turnSpeed = 0;
            turnAngle = (turnAngle + turnSpeed * dt * 360 / 2600f) % 360; // 2.6 秒一圈
        }

        var (accent, amount) = Accent();
        float r = Math.Min(art.Height * .41f, art.Width * .37f);
        float cx = art.X + art.Height * .08f + r, cy = art.Y + art.Height / 2;
        float label = r * .6f; // 封面做的标签：比真唱片的大，照片看得清，外面还留一圈沟槽

        // 不动的部分：从缓存贴上去
        int w = (int)Math.Ceiling(art.Width), h = (int)Math.Ceiling(art.Height);
        var key = (w, h, accent.ToArgb() & 0xF0F0F0);
        if (deckCache == null || deckKey != key)
        {
            deckCache?.Dispose();
            deckCache = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppPArgb);
            deckKey = key;
            using var dg = Graphics.FromImage(deckCache);
            dg.SmoothingMode = SmoothingMode.AntiAlias;
            dg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            DrawDeck(dg, new RectangleF(0, 0, art.Width, art.Height), cx - art.X, cy - art.Y, r, label, accent, amount);
        }
        DrawImage(g, deckCache, new RectangleF(art.X, art.Y, deckCache.Width, deckCache.Height), a);

        // 标签：封面做成圆的，跟着唱片转
        if (cover != null && LabelFace(cover, (int)Math.Ceiling(label * 2)) is { } face)
        {
            using var attributes = a < .99f ? Fade(a) : null;
            using var brush = attributes != null ? new TextureBrush(face, new Rectangle(0, 0, face.Width, face.Height), attributes) : new TextureBrush(face);
            brush.WrapMode = WrapMode.Clamp;
            float k = 2 * label / face.Width;
            using var m = new Matrix();
            m.Translate(cx, cy);
            m.Rotate(turnAngle);
            m.Scale(k, k);
            m.Translate(-face.Width / 2f, -face.Height / 2f);
            brush.Transform = m;
            g.FillEllipse(brush, cx - label, cy - label, label * 2, label * 2);
        }
        else
        {
            using var plain = new SolidBrush(Color.FromArgb(Byte(a), Mix(accent, Color.FromArgb(70, 74, 82), 1 - amount * .8f)));
            g.FillEllipse(plain, cx - label, cy - label, label * 2, label * 2);
        }
        // 标签边上一圈压痕、一点点从左上来的光
        using (var rim = new Pen(Color.FromArgb(Byte(.55f * a), 0, 0, 0), 1.2f * s)) g.DrawEllipse(rim, cx - label, cy - label, label * 2, label * 2);
        using (var gloss = new GraphicsPath())
        {
            gloss.AddEllipse(cx - label, cy - label, label * 2, label * 2);
            using var shine = new LinearGradientBrush(new PointF(cx - label, cy - label), new PointF(cx + label * .3f, cy + label * .3f),
                Color.FromArgb(Byte(.16f * a), 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
            g.FillPath(shine, gloss);
        }
        // 中心轴：一颗小金属钮
        float pin = 3.2f * s;
        using (var axle = new LinearGradientBrush(new PointF(cx - pin, cy - pin), new PointF(cx + pin, cy + pin), Color.FromArgb(Byte(a), 236, 239, 243), Color.FromArgb(Byte(a), 96, 101, 109)))
            g.FillEllipse(axle, cx - pin, cy - pin, pin * 2, pin * 2);

        DrawTonearm(g, art, cx, cy, r, a);

        // 左下角的电源灯：放歌时亮成歌曲的颜色
        float led = 2.6f * s, lx = art.X + 10 * s, ly = art.Bottom - 10 * s;
        var on = Mix(Color.FromArgb(120, 255, 170), accent, amount * .8f);
        if (armPos > .5f)
        {
            using var halo = new GraphicsPath();
            halo.AddEllipse(lx - led * 3, ly - led * 3, led * 6, led * 6);
            using var glow = new PathGradientBrush(halo) { CenterColor = Color.FromArgb(Byte(.5f * a), on), SurroundColors = new[] { Color.FromArgb(0, on) } };
            g.FillPath(glow, halo);
        }
        using (var dot = new SolidBrush(armPos > .5f ? Color.FromArgb(Byte(a), on) : Color.FromArgb(Byte(.7f * a), 60, 63, 68)))
            g.FillEllipse(dot, lx - led, ly - led, led * 2, led * 2);
    }

    Bitmap? labelFace;   // 做好的圆标签（方的）：每首歌做一次
    Bitmap? labelFrom;   // 是用哪张封面做的（只比对是不是同一张，不再用它）
    int labelSide;

    /// <summary>
    /// 唱片中间的标签图。差不多方的封面：铺满裁成圆。太宽/太高的（MV 缩略图常常很扁）：铺满的话只剩中间一小块，
    /// 人和画面都切掉了；改成底下铺一层同一张图放大、模糊、压暗，中间放完整的照片。
    /// </summary>
    Bitmap? LabelFace(Bitmap cover, int side)
    {
        side = Math.Clamp(side * 2, 64, 512); // 做大一点：转起来缩放时不糊
        if (ReferenceEquals(labelFrom, cover) && labelSide == side) return labelFace; // 做过了（做失败的也不再试）
        labelFace?.Dispose();
        labelFace = null;
        labelFrom = cover;
        labelSide = side;
        try
        {
            var face = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(face);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float aspect = cover.Width / (float)Math.Max(1, cover.Height);
            using var wrap = new ImageAttributes();
            wrap.SetWrapMode(WrapMode.TileFlipXY); // 边上不要混进一圈透明
            void Put(Image image, RectangleF to) =>
                g.DrawImage(image, Rectangle.Round(to), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrap);
            // 铺满（多出来的裁掉）
            float fill = side / Math.Min((float)cover.Width, cover.Height);
            var full = new RectangleF((side - cover.Width * fill) / 2, (side - cover.Height * fill) / 2, cover.Width * fill, cover.Height * fill);
            if (aspect is > .8f and < 1.25f) Put(cover, full);
            else
            {
                // 模糊的底：先缩成很小再放大，自然就糊了；再压暗一点，中间的照片才显眼
                using (var tiny = new Bitmap(10, 10, PixelFormat.Format32bppPArgb))
                {
                    using (var tg = Graphics.FromImage(tiny))
                    {
                        tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        int sq = Math.Min(cover.Width, cover.Height); // 取中间方的一块来糊
                        tg.DrawImage(cover, new Rectangle(0, 0, 10, 10), (cover.Width - sq) / 2, (cover.Height - sq) / 2, sq, sq, GraphicsUnit.Pixel);
                    }
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    Put(tiny, new RectangleF(-side * .1f, -side * .1f, side * 1.2f, side * 1.2f));
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                }
                using (var dim = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillRectangle(dim, 0, 0, side, side);
                // 整张照片放中间：四个角都要在圆里（对角线不超过标签的 94%）
                float k = side * .94f / MathF.Sqrt(cover.Width * (float)cover.Width + cover.Height * (float)cover.Height);
                var fit = new RectangleF((side - cover.Width * k) / 2, (side - cover.Height * k) / 2, cover.Width * k, cover.Height * k);
                using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillRectangle(shadow, fit.X, fit.Y + side * .015f, fit.Width, fit.Height);
                Put(cover, fit);
            }
            labelFace = face;
        }
        catch (Exception ex) { Log.Error(ex); } // 封面刚被换掉释放了：这帧先不画标签
        return labelFace;
    }

    /// <summary>不动的部分：底座、唱盘、黑胶和沟槽、固定的反光。画在缓存里，坐标从 (0,0) 起。</summary>
    void DrawDeck(Graphics g, RectangleF box, float cx, float cy, float r, float label, Color accent, float amount)
    {
        float s = S, radius = 14 * s;
        // 底座：深色拉丝金属，带一点歌曲的颜色，上沿一道细高光
        using (var shape = Shapes.RoundRect(box, radius))
        {
            var top = Mix(Color.FromArgb(58, 61, 67), accent, .10f * amount);
            var bottom = Mix(Color.FromArgb(26, 27, 30), accent, .05f * amount);
            using (var body = new LinearGradientBrush(new PointF(0, box.Y), new PointF(0, box.Bottom + 1), top, bottom)) g.FillPath(body, shape);
            var state = g.Save();
            g.SetClip(shape, CombineMode.Intersect);
            var rng = new Random(7);
            for (float y = box.Y + 1; y < box.Bottom; y += 1.5f * s) // 拉丝：一条条深浅不一的横线
            {
                using var line = new Pen(Color.FromArgb(rng.Next(2, 8), rng.Next(2) == 0 ? Color.White : Color.Black), .6f * s);
                g.DrawLine(line, box.X, y, box.Right, y + (float)(rng.NextDouble() - .5) * s);
            }
            g.Restore(state);
            using (var edge = new Pen(Color.FromArgb(70, 255, 255, 255), 1 * s))
            using (var inner = Shapes.RoundRect(RectangleF.Inflate(box, -.5f * s, -.5f * s), radius - .5f * s))
                g.DrawPath(edge, inner);
        }

        // 唱盘下面的影子（两层，软一点）
        for (int i = 0; i < 2; i++)
        {
            float grow = (3 + i * 3) * s;
            using var shadow = new SolidBrush(Color.FromArgb(i == 0 ? 70 : 35, 0, 0, 0));
            g.FillEllipse(shadow, cx - r - grow / 2, cy - r + 2.5f * s - grow / 2 + i * 2 * s, (r + grow / 2) * 2, (r + grow / 2) * 2);
        }
        // 唱盘的金属边：上亮下暗
        float rim = r + 3 * s;
        using (var platter = new LinearGradientBrush(new PointF(cx, cy - rim), new PointF(cx, cy + rim), Color.FromArgb(178, 184, 192), Color.FromArgb(48, 51, 56)))
            g.FillEllipse(platter, cx - rim, cy - rim, rim * 2, rim * 2);

        // 黑胶：中间稍亮、边上更黑
        using (var disc = new GraphicsPath())
        {
            disc.AddEllipse(cx - r, cy - r, r * 2, r * 2);
            using var vinyl = new PathGradientBrush(disc) { CenterColor = Color.FromArgb(34, 35, 39), SurroundColors = new[] { Color.FromArgb(10, 10, 12) } };
            g.FillPath(vinyl, disc);
        }
        // 沟槽：一圈圈细纹，隔几圈有一道歌与歌之间的空隙（亮一点）
        int ring = 0;
        for (float rr = label + 3 * s; rr < r - 1.5f * s; rr += 1.35f * s, ring++)
        {
            bool gap = ring % 11 == 6;
            using var groove = new Pen(Color.FromArgb(gap ? 26 : ring % 2 == 0 ? 11 : 5, 255, 255, 255), (gap ? .9f : .6f) * s);
            g.DrawEllipse(groove, cx - rr, cy - rr, rr * 2, rr * 2);
        }
        using (var lip = new Pen(Color.FromArgb(40, 255, 255, 255), .8f * s)) g.DrawEllipse(lip, cx - r + .5f * s, cy - r + .5f * s, (r - .5f * s) * 2, (r - .5f * s) * 2);

        // 反光：灯在左上方，在唱片上照出两道相对的扇形亮带（真唱片转起来亮带是不动的）
        var keep = g.Save();
        using (var ringArea = new GraphicsPath())
        {
            ringArea.AddEllipse(cx - r, cy - r, r * 2, r * 2);
            ringArea.AddEllipse(cx - label - 2 * s, cy - label - 2 * s, (label + 2 * s) * 2, (label + 2 * s) * 2);
            ringArea.FillMode = FillMode.Alternate;
            g.SetClip(ringArea, CombineMode.Intersect);
        }
        foreach (float centre in new[] { -125f, 55f })
            for (int k = -7; k <= 7; k++)
            {
                float strength = MathF.Exp(-(k * k) / 9f) * (centre < 0 ? 46 : 26);
                using var band = new SolidBrush(Color.FromArgb((int)strength, 255, 255, 255));
                g.FillPie(band, cx - r, cy - r, r * 2, r * 2, centre + k * 2.4f - 1.2f, 2.4f);
            }
        g.Restore(keep);

        // 唱臂的底座和搁架（不动的部分）
        var (px, py, _) = ArmGeometry(box, cx, cy, r);
        float baseR = 10 * s;
        using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(shadow, px - baseR + 1.5f * s, py - baseR + 2.5f * s, baseR * 2, baseR * 2);
        using (var plate = new LinearGradientBrush(new PointF(px - baseR, py - baseR), new PointF(px + baseR, py + baseR), Color.FromArgb(150, 156, 165), Color.FromArgb(44, 47, 52)))
            g.FillEllipse(plate, px - baseR, py - baseR, baseR * 2, baseR * 2);
        using (var ringPen = new Pen(Color.FromArgb(60, 255, 255, 255), .8f * s)) g.DrawEllipse(ringPen, px - baseR + .5f * s, py - baseR + .5f * s, baseR * 2 - s, baseR * 2 - s);
        var rest = ArmTip(px, py, ArmGeometry(box, cx, cy, r).Length, ArmRestAngle);
        float post = 3.2f * s;
        using (var postBrush = new LinearGradientBrush(new PointF(rest.X - post, rest.Y - post), new PointF(rest.X + post, rest.Y + post), Color.FromArgb(120, 125, 133), Color.FromArgb(36, 38, 42)))
            g.FillEllipse(postBrush, rest.X - post, rest.Y + 5 * s - post, post * 2, post * 2);
    }

    const float ArmRestAngle = .07f; // 弧度：搁着时几乎竖直往下

    /// <summary>唱臂转轴在哪、臂多长、落在唱片上时的角度（0 = 竖直往下，往左转是负的）。</summary>
    (float X, float Y, float Length) ArmGeometry(RectangleF box, float cx, float cy, float r)
    {
        float px = box.Right - box.Width * .13f, py = box.Y + box.Height * .17f;
        float tx = cx + r * .52f, ty = cy + r * .56f; // 唱针落在外圈靠右下
        return (px, py, MathF.Sqrt((tx - px) * (tx - px) + (ty - py) * (ty - py)));
    }

    static PointF ArmTip(float px, float py, float length, float angle) => new(px + length * MathF.Sin(angle), py + length * MathF.Cos(angle));

    void DrawTonearm(Graphics g, RectangleF art, float cx, float cy, float r, float a)
    {
        float s = S;
        var (px, py, length) = ArmGeometry(art, cx, cy, r);
        float tx = cx + r * .52f, ty = cy + r * .56f;
        float play = MathF.Atan2(tx - px, ty - py);
        float e = armPos * armPos * (3 - 2 * armPos); // 缓进缓出
        float angle = ArmRestAngle + (play - ArmRestAngle) * e;
        // 抬着的时候离唱片高一点：影子离得远、臂也略亮
        float lift = 1 - e;
        var tip = ArmTip(px, py, length, angle);
        var back = ArmTip(px, py, -14 * s, angle); // 转轴另一边的配重
        float dx = MathF.Sin(angle), dy = MathF.Cos(angle);
        // 唱头：往里折一点
        float headAngle = angle + .45f;
        var head = new PointF(tip.X + 12 * s * MathF.Sin(headAngle), tip.Y + 12 * s * MathF.Cos(headAngle));

        // 影子（往右下偏，抬起来时偏得多）
        float off = (3 + 3 * lift) * s;
        using (var shade = new Pen(Color.FromArgb(Byte((.38f - .1f * lift) * a), 0, 0, 0), 4.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(shade, new[] { new PointF(back.X + off, back.Y + off), new PointF(tip.X + off, tip.Y + off), new PointF(head.X + off, head.Y + off) });

        // 配重：一截粗圆柱
        using (var weight = new Pen(Color.FromArgb(Byte(a), 70, 74, 80), 8.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
            g.DrawLine(weight, back, new PointF(px - dx * 6 * s, py - dy * 6 * s));
        using (var weightHi = new Pen(Color.FromArgb(Byte(.5f * a), 210, 214, 220), 1.6f * s) { StartCap = LineCap.Round })
            g.DrawLine(weightHi, new PointF(back.X - dy * 2.2f * s, back.Y + dx * 2.2f * s), new PointF(px - dx * 6 * s - dy * 2.2f * s, py - dy * 6 * s + dx * 2.2f * s));

        // 臂管：银色，中间一道高光像圆管
        using (var tube = new Pen(Color.FromArgb(Byte(a), 168, 174, 183), 3.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(tube, px, py, tip.X, tip.Y);
        using (var dark = new Pen(Color.FromArgb(Byte(.45f * a), 40, 42, 46), 1.1f * s))
            g.DrawLine(dark, px + dy * 1.2f * s, py - dx * 1.2f * s, tip.X + dy * 1.2f * s, tip.Y - dx * 1.2f * s);
        using (var shine = new Pen(Color.FromArgb(Byte(.9f * a), 246, 248, 251), 1.1f * s))
            g.DrawLine(shine, px - dy * .8f * s, py + dx * .8f * s, tip.X - dy * .8f * s, tip.Y + dx * .8f * s);

        // 唱头：深色小块 + 一点歌曲颜色的唱针盒
        var (accent, amount) = Accent();
        // 唱头壳：深灰金属，上沿一道细高光；唱针盒是暗红（带一点歌曲的颜色），不抢眼
        float hx = MathF.Sin(headAngle), hy = MathF.Cos(headAngle);
        using (var shell = new Pen(Color.FromArgb(Byte(a), 58, 61, 67), 7.4f * s) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
            g.DrawLine(shell, tip, head);
        using (var edge = new Pen(Color.FromArgb(Byte(.45f * a), 255, 255, 255), .8f * s))
            g.DrawLine(edge, tip.X - hy * 3.2f * s, tip.Y + hx * 3.2f * s, head.X - hy * 3.2f * s, head.Y + hx * 3.2f * s);
        var mid = new PointF(tip.X + hx * 5 * s, tip.Y + hy * 5 * s);
        using (var cart = new Pen(Color.FromArgb(Byte(a), Mix(Color.FromArgb(120, 36, 32), accent, amount * .35f)), 4.4f * s) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
            g.DrawLine(cart, mid, head);
        using (var finger = new Pen(Color.FromArgb(Byte(a), 150, 155, 163), 1.3f * s) { EndCap = LineCap.Round }) // 拿唱臂用的小指扣
            g.DrawLine(finger, head.X - hy * 3.5f * s, head.Y + hx * 3.5f * s, head.X - hy * 7 * s, head.Y + hx * 7 * s);

        // 转轴盖：一颗亮的金属钮
        float cap = 4.6f * s;
        using (var hub = new LinearGradientBrush(new PointF(px - cap, py - cap), new PointF(px + cap, py + cap), Color.FromArgb(Byte(a), 240, 242, 246), Color.FromArgb(Byte(a), 92, 97, 105)))
            g.FillEllipse(hub, px - cap, py - cap, cap * 2, cap * 2);
    }

    /// <summary>滚滚轮调音量时：封面上盖一层暗，中间一个大数字，外面一圈是音量。一秒后淡掉。</summary>
    void DrawVolumeBubble(Graphics g, RectangleF over, float a)
    {
        if (volumeShownSince < 0 || volume < 0) return;
        float t = (Now - volumeShownSince) / (float)VolumeShowMs;
        if (t >= 1) return;
        float s = S, fade = (t < .7f ? 1 : (1 - t) / .3f) * a;
        var (accent, amount) = Accent();
        FillRound(g, over, 14 * s, Color.FromArgb(Byte(.5f * fade), 0, 0, 0));
        float cx = over.X + over.Width / 2, cy = over.Y + over.Height / 2, r = Math.Min(over.Width, over.Height) * .34f;
        using (var track = new Pen(Color.FromArgb(Byte(.22f * fade), 255, 255, 255), 4 * s)) g.DrawEllipse(track, cx - r, cy - r, r * 2, r * 2);
        var ring = amount > .05f ? Mix(Color.White, accent, .6f * amount) : Color.White;
        if (volume > .001f)
            using (var pen = new Pen(Color.FromArgb(Byte(fade), ring), 4 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -90, 360 * volume);
        using var brush = new SolidBrush(Color.FromArgb(Byte(fade), 255, 255, 255));
        g.DrawString(((int)Math.Round(volume * 100)).ToString(), volumeFont, brush, new RectangleF(cx - r, cy - r, r * 2, r * 2), CenterFormat);
    }

    /// <summary>只在放歌时往前走的时钟：背景漂、锁住的光呼吸用。暂停时停住，再放时接着走，不会跳一下。</summary>
    long driftStamp;
    float Drift()
    {
        long now = Now;
        if (media is { Playing: true } && animate) drift += Math.Min(.1f, (now - driftStamp) / 1000f);
        driftStamp = now;
        return drift;
    }

    /// <summary>“耳机 (WH-1000XM5)”→“WH-1000XM5”；“扬声器 (Realtek(R) Audio)”→“扬声器”；“耳机 (Realtek(R) Audio)”→“耳机”。</summary>
    static string DeviceName(Audio.Output device)
    {
        var m = System.Text.RegularExpressions.Regex.Match(device.Name, @"^(.*?)\s*\((.+)\)\s*$");
        // 括号里是声卡名（插线耳机“耳机 (Realtek(R) Audio)”）时用前面那半；蓝牙耳机括号里才是耳机自己的名字
        bool card = m.Success && System.Text.RegularExpressions.Regex.IsMatch(m.Groups[2].Value, @"(?i)realtek|nvidia|intel|amd|high definition|usb audio|audio device|smart sound");
        string name = !m.Success ? device.Name : device.Headphones && !card ? m.Groups[2].Value : m.Groups[1].Value;
        name = System.Text.RegularExpressions.Regex.Replace(name, @"(?i)\s+(stereo|hands-free.*|a2dp.*)$", "");
        return Shorten(name.Trim(), 18);
    }

    void DrawLyrics(Graphics g, RectangleF box, TimeSpan position, float a)
    {
        float s = S;
        string? message = lyricsLoading ? (app.Config.NeteaseLyrics ? L.T("正在找歌词…", "Finding lyrics…") : L.T("正在从 lrclib.net 找歌词…", "Finding lyrics on lrclib.net…"))
            : lyricsOffline ? L.T("歌词服务暂时连不上，下次打开再试", "Lyrics service unreachable, will retry later")
            : lyrics == null ? L.T("没有找到这首歌的歌词（右键可以手动搜索）", "No lyrics found (right-click to search)")
            : lyrics.Instrumental ? L.T("纯音乐，请欣赏", "Instrumental, enjoy") : null;
        if (message != null)
        {
            using var brush = new SolidBrush(Ink(.5f * a));
            var size = g.MeasureString(message, labelFont, (int)box.Width, WrapFormat);
            g.DrawString(message, labelFont, brush, new RectangleF(box.X, box.Y + (box.Height - size.Height) / 2, box.Width, size.Height + 2 * s), WrapFormat);
            return;
        }
        var lines = lyrics!.Lines;
        int index = lyrics.IndexAt(position + TimeSpan.FromMilliseconds(250)); // 早一点点换，跟唱的感觉对得上
        if (index != lyricIndex)
        {
            // 唱到下一句：整列往上滚一格；拖进度跳过去的就直接换，不滚
            lyricRollFrom = lyricIndex >= 0 && index == lyricIndex + 1 ? lyricIndex : -1;
            if (lyricIndex >= 0 || index >= 0) lyricSince = Now;
            lyricIndex = index;
        }
        if (lyricPathsFor != lyrics) ClearLyricPaths();

        float t = animate && lyricSince >= 0 ? Math.Clamp((Now - lyricSince) / (float)LyricRollMs, 0, 1) : 1;
        float e = 1 - (1 - t) * (1 - t) * (1 - t);
        // 滚动中：每句的“名次”（0 = 正在唱，-1 = 上一句，1 = 下一句）从旧位置连续地挪到新位置
        float roll = lyricRollFrom >= 0 ? 1 - e : 0;
        float fade = lyricRollFrom >= 0 ? 1 : e;

        float big = lyricFont.Size, small = lyricNextFont.Size;
        float bigH = BoundsOf(LyricPath(index, big, box.Width)).Height;
        float gap = 12 * s, step = Math.Max(bigH, big) / 2 + gap + small * .7f;
        float centerY = box.Y + box.Height / 2;
        var (accent, amount) = Accent();
        // 翻译：正在唱的那句下面多一行小字，这句和翻译一起居中；滚动时跟着这句的“名次”慢慢让出/收回位置
        bool translate = app.Config.LyricTranslation;
        float trH = chipFont.Height + 4 * s;
        float Extra(int j) => translate && j >= 0 && j < lines.Length && !string.IsNullOrEmpty(lines[j].Translation)
            ? trH * Math.Clamp(1 - Math.Abs(j - index + roll), 0, 1) : 0;
        int first = Math.Max(0, index - 2);
        float extraAll = 0;
        for (int j = first; j <= index + 2 && j < lines.Length; j++) extraAll += Extra(j);
        var state = g.Save();
        g.SetClip(RectangleF.Inflate(box, 8 * s, 2 * s), CombineMode.Intersect);
        for (int i = first; i <= index + 2 && i < lines.Length; i++)
        {
            if (i < 0) continue;
            float rank = i - index + roll;
            if (Math.Abs(rank) > 1.6f) continue;
            bool empty = lines[i].Text.Trim().Length == 0;
            if (empty && Math.Abs(rank) > .5f) continue; // 间奏的空行只在正在唱时显示成 ♪
            float focus = Math.Clamp(1 - Math.Abs(rank), 0, 1);
            float size = small + (big - small) * focus;
            var path = LyricPath(i, size, box.Width);
            var bounds = BoundsOf(path);
            float above = 0;
            for (int j = first; j < i; j++) above += Extra(j);
            float y = centerY + rank * step - bounds.Height / 2 - bounds.Y + above - extraAll / 2;
            float alpha = a * fade * (rank < 0 ? .34f + .66f * focus : .46f + .54f * focus) * Math.Clamp((1.6f - Math.Abs(rank)) / .6f, 0, 1);
            using var m = new Matrix();
            m.Translate(box.X, y);
            using var placed = (GraphicsPath)path.Clone();
            placed.Transform(m);
            // 一句太长折成两行时，第二行从这里开始（路径原点在第一行顶上）
            float rowH = size * lyricFont.FontFamily.GetLineSpacing(FontStyle.Bold) / lyricFont.FontFamily.GetEmHeight(FontStyle.Bold);
            float split = bounds.Bottom > rowH * 1.15f ? y + rowH : float.NaN;
            DrawLyricLine(g, placed, focus, alpha, i, position, accent, amount, split);
            if (Extra(i) > .5f)
            {
                string said = Fit(lines[i].Translation!, chipFont, box.Width);
                using var trBrush = new SolidBrush(Ink(alpha * focus * .72f));
                g.DrawString(said, chipFont, trBrush, box.X, y + bounds.Bottom + 3 * s);
            }
        }
        g.Restore(state);
    }

    const int LyricRollMs = 420;

    /// <summary>
    /// 一句歌词：正在唱的那句有一层往下的柔影和一圈歌曲颜色的光晕，唱过的部分从左往右亮起来（卡拉 OK）；
    /// 上一句、下一句只是淡淡的字。
    /// </summary>
    void DrawLyricLine(Graphics g, GraphicsPath path, float focus, float alpha, int line, TimeSpan position, Color accent, float amount, float split = float.NaN)
    {
        float s = S;
        bool dark = DarkInk;
        // 影子：往下错开一点的深色字，再淡一点的一层更远，像浮在玻璃上
        using (var m = new Matrix())
        {
            m.Translate(0, 2.2f * s);
            using var shadow = (GraphicsPath)path.Clone();
            shadow.Transform(m);
            using var brush = new SolidBrush(Color.FromArgb(Byte((dark ? .10f : .38f) * alpha), 0, 0, 0));
            g.FillPath(brush, shadow);
        }
        if (focus > .05f)
        {
            // 光晕：几圈越来越宽、越来越淡的描边，颜色跟着歌（没有歌色时是白光）
            var glow = amount > .05f ? Mix(Color.White, accent, .55f * amount) : Color.White;
            foreach (var (width, k) in new[] { (9f, .05f), (6f, .08f), (3.5f, .12f) })
            {
                using var pen = new Pen(Color.FromArgb(Byte(k * focus * alpha * (dark ? .6f : 1)), glow), width * s) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
            }
        }
        var bounds = path.GetBounds();
        float sung = focus > .5f ? SungFraction(line, position) : line < lyricIndex ? 1 : 0;
        if (focus > .05f && sung < .999f && bounds.Width > 1)
        {
            // 卡拉 OK：唱到哪亮到哪，交界处软一点。折成两行的：先亮完第一行，再从第二行开头接着亮
            float p = Math.Clamp(sung, 0, 1);
            if (!float.IsNaN(split) && RowSpans(path, split) is var (top, bottom) && top.Width > 1 && bottom.Width > 1)
            {
                float filled = p * (top.Width + bottom.Width);
                var state = g.Save();
                g.SetClip(new RectangleF(bounds.X - 20 * s, bounds.Y - 20 * s, bounds.Width + 40 * s, split - bounds.Y + 20 * s), CombineMode.Intersect);
                FillSung(g, path, top, filled / top.Width, alpha);
                g.Restore(state);
                state = g.Save();
                g.SetClip(new RectangleF(bounds.X - 20 * s, split, bounds.Width + 40 * s, bounds.Bottom - split + 20 * s), CombineMode.Intersect);
                FillSung(g, path, bottom, (filled - top.Width) / bottom.Width, alpha);
                g.Restore(state);
            }
            else FillSung(g, path, bounds, p, alpha);
        }
        else
        {
            using var brush = new SolidBrush(Ink(alpha));
            g.FillPath(brush, path);
        }
    }

    /// <summary>在 span 这一段（一行字的左右）里，从左数 p 成亮、其余暗。</summary>
    void FillSung(Graphics g, GraphicsPath path, RectangleF span, float p, float alpha)
    {
        p = Math.Clamp(p, 0, 1);
        var bright = Ink(alpha);
        var dim = Ink(alpha * .5f);
        if (p <= 0 || p >= 1)
        {
            using var solid = new SolidBrush(p >= 1 ? bright : dim);
            g.FillPath(solid, path);
            return;
        }
        using var brush = new LinearGradientBrush(new RectangleF(span.X - 1, span.Y, span.Width + 2, Math.Max(1, span.Height)), bright, dim, LinearGradientMode.Horizontal);
        float edge = .06f;
        brush.InterpolationColors = new ColorBlend
        {
            Colors = new[] { bright, bright, dim, dim },
            Positions = new[] { 0f, Math.Clamp(p - edge, 0, 1), Math.Clamp(p + edge, 0.0001f, 1), 1f },
        };
        if (brush.InterpolationColors.Positions[1] >= brush.InterpolationColors.Positions[2])
            brush.InterpolationColors = new ColorBlend { Colors = new[] { bright, dim }, Positions = new[] { 0f, 1f } };
        brush.WrapMode = WrapMode.TileFlipX; // 渐变框外（光晕边）别重复出亮条
        g.FillPath(brush, path);
    }

    /// <summary>折成两行的歌词：第一行、第二行各自的左右范围（按字形的点算）。</summary>
    static (RectangleF Top, RectangleF Bottom)? RowSpans(GraphicsPath path, float split)
    {
        var points = path.PathPoints;
        float t0 = float.MaxValue, t1 = float.MinValue, b0 = float.MaxValue, b1 = float.MinValue;
        float ty0 = float.MaxValue, ty1 = float.MinValue, by0 = float.MaxValue, by1 = float.MinValue;
        foreach (var pt in points)
        {
            if (pt.Y < split)
            {
                t0 = Math.Min(t0, pt.X); t1 = Math.Max(t1, pt.X); ty0 = Math.Min(ty0, pt.Y); ty1 = Math.Max(ty1, pt.Y);
            }
            else
            {
                b0 = Math.Min(b0, pt.X); b1 = Math.Max(b1, pt.X); by0 = Math.Min(by0, pt.Y); by1 = Math.Max(by1, pt.Y);
            }
        }
        if (t1 < t0 || b1 < b0) return null;
        return (new RectangleF(t0, ty0, t1 - t0, ty1 - ty0), new RectangleF(b0, by0, b1 - b0, by1 - by0));
    }

    /// <summary>
    /// 这一句唱了几成（按字宽算）：有逐字时间的（网易云）唱到哪个字亮到哪个字；
    /// 没有的按到下一句的时间算（太长的间隔按字数估，免得一句话要亮好久）。
    /// </summary>
    float SungFraction(int line, TimeSpan position)
    {
        var lines = lyrics?.Lines;
        if (lines == null || line < 0 || line >= lines.Length) return 1;
        if (lines[line].Words is { Length: > 0 } words)
        {
            var now = position + TimeSpan.FromMilliseconds(120);
            string done = "";
            foreach (var word in words)
            {
                if (now >= word.Start + word.Length)
                {
                    done += word.Text;
                    continue;
                }
                float full = Measure(lines[line].Text, lyricFont);
                if (full <= 0) return 1;
                float part = now > word.Start ? (float)((now - word.Start).TotalMilliseconds / Math.Max(1, word.Length.TotalMilliseconds)) : 0;
                return Math.Clamp((Measure(done.TrimStart(), lyricFont) + part * Measure(word.Text, lyricFont)) / full, 0, 1);
            }
            return 1;
        }
        var start = lines[line].Time;
        double span = line + 1 < lines.Length ? (lines[line + 1].Time - start).TotalSeconds : 4;
        span = Math.Clamp(Math.Min(span, .32 * lines[line].Text.Length + .8), .4, 12);
        return (float)Math.Clamp((position + TimeSpan.FromMilliseconds(250) - start).TotalSeconds / span, 0, 1);
    }

    /// <summary>一句歌词的字形（原点在左上），同一句同一字号只做一次。</summary>
    GraphicsPath LyricPath(int line, float size, float width)
    {
        var key = (line, (int)Math.Round(size * 4), (int)width);
        if (lyricPaths.TryGetValue(key, out var path)) return path;
        if (lyricPaths.Count > 40) ClearLyricPaths(keepOwner: true);
        string text = lyrics != null && line >= 0 && line < lyrics.Lines.Length ? lyrics.Lines[line].Text.Trim() : "";
        if (text.Length == 0) text = "♪";
        path = new GraphicsPath();
        // 最多两行；太长的截掉
        bool focusSize = size > (lyricFont.Size + lyricNextFont.Size) / 2;
        if (!focusSize) text = Fit(text, lyricNextFont, width * lyricNextFont.Size / Math.Max(1, size));
        using var format = new StringFormat(focusSize ? StringFormatFlags.LineLimit : StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisWord };
        path.AddString(text, lyricFont.FontFamily, (int)FontStyle.Bold, size, new RectangleF(0, 0, width, size * (focusSize ? 2.7f : 1.4f)), format);
        return lyricPaths[key] = path;
    }

    static RectangleF BoundsOf(GraphicsPath path)
    {
        var b = path.GetBounds();
        return b.IsEmpty ? new RectangleF(0, 0, 1, 1) : b;
    }

    void ClearLyricPaths(bool keepOwner = false)
    {
        foreach (var path in lyricPaths.Values) path.Dispose();
        lyricPaths.Clear();
        if (!keepOwner) lyricPathsFor = lyrics;
    }

    float MusicChip(Graphics g, float x, float cy, string text, string key, int value, bool on, Point mouse, float a, float limit)
    {
        float s = S, w = Measure(text, chipFont) * s + 22 * s, h = 26 * s;
        if (x + w > limit) return -1;
        var b = new RectangleF(x, cy - h / 2, w, h);
        FillRound(g, b, h / 2, on ? Ink(.9f * a) : b.Contains(mouse) ? Ink(.18f * a) : Ink(.09f * a));
        DrawTight(g, text, chipFont, on ? Deep(a) : Ink(.82f * a), b.X + 11 * s, cy);
        musicHits.Add((b, key, value));
        return w;
    }

    /// <summary>小锁：锁着时锁梁扣下去，开着时往上翘、左脚离开锁身。</summary>
    void DrawLock(Graphics g, float cx, float cy, bool closed, Color color)
    {
        float s = S;
        FillRound(g, new RectangleF(cx - 5.5f * s, cy - 1 * s, 11 * s, 8.5f * s), 2 * s, color);
        using var pen = new Pen(color, 1.7f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float lift = closed ? 0 : 2.6f * s, top = cy - 8 * s - lift;
        g.DrawArc(pen, cx - 3.6f * s, top, 7.2f * s, 7.2f * s, 180, 180);
        g.DrawLine(pen, cx + 3.6f * s, top + 3.6f * s, cx + 3.6f * s, cy - 1 * s);
        g.DrawLine(pen, cx - 3.6f * s, top + 3.6f * s, cx - 3.6f * s, closed ? cy - 1 * s : top + 4.6f * s);
    }

    /// <summary>跳过：一个小三角加一竖。</summary>
    void DrawSkip(Graphics g, float x, float cy, Color color)
    {
        float s = S;
        using var brush = new SolidBrush(color);
        Triangle(g, brush, x - 3 * s, cy, 1, 5.5f * s);
        g.FillRectangle(brush, x + 3.2f * s, cy - 4.5f * s, 1.8f * s, 9 * s);
    }

    /// <summary>头戴耳机：一道弧加两个耳罩，left 是左边缘，16px 宽。</summary>
    void DrawHeadphones(Graphics g, float left, float cy, Color ink)
    {
        float s = S;
        using (var pen = new Pen(ink, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, left + 1.5f * s, cy - 7 * s, 13 * s, 12 * s, 180, 180);
        FillRound(g, new RectangleF(left, cy - 1 * s, 4.5f * s, 7.5f * s), 1.8f * s, ink);
        FillRound(g, new RectangleF(left + 11.5f * s, cy - 1 * s, 4.5f * s, 7.5f * s), 1.8f * s, ink);
    }

    /// <summary>右对齐画在 right 左边：耳机图标、名字、电量（20% 以下变红）。返回用掉的宽度，放不下返回 0。</summary>
    float DrawEarbuds(Graphics g, float right, float cy, string name, int percent, float minLeft, float a)
    {
        float s = S;
        string pct = $"{percent}%";
        float pctW = Measure(pct, boldFont) * s;
        string label = Fit(name, smallFont, 96 * s);
        float labelW = Measure(label, smallFont) * s;
        float w = 16 * s + 6 * s + labelW + 6 * s + pctW;
        if (right - w < minLeft) { label = ""; w = 16 * s + 6 * s + pctW; } // 地方不够：只留图标和电量
        if (right - w < minLeft) return 0;
        float x = right - w;
        DrawHeadphones(g, x, cy, Ink(.82f * a));
        x += 22 * s;
        if (label.Length > 0)
        {
            DrawTight(g, label, smallFont, Ink(.62f * a), x, cy);
            x += labelW + 6 * s;
        }
        var color = percent <= 20 ? Color.FromArgb(Byte(a), 248, 113, 113) : Ink(.92f * a);
        DrawTight(g, pct, boldFont, color, x, cy);
        return w;
    }

    void Knob(Graphics g, float x, float cy, float r, float a)
    {
        float s = S;
        using (var shadow = new SolidBrush(Color.FromArgb(Byte(.25f * a), 0, 0, 0))) g.FillEllipse(shadow, x - r, cy - r + 1.5f * s, r * 2, r * 2);
        using var knob = new SolidBrush(Color.FromArgb(Byte(a), 255, 255, 255));
        g.FillEllipse(knob, x - r, cy - r, r * 2, r * 2);
    }

    void DrawSpeaker(Graphics g, float cx, float cy, float a)
    {
        float s = S;
        using var brush = new SolidBrush(Ink(.8f * a));
        g.FillRectangle(brush, cx - 7 * s, cy - 3 * s, 3.5f * s, 6 * s);
        g.FillPolygon(brush, new[] { new PointF(cx - 4 * s, cy - 3 * s), new PointF(cx + 1 * s, cy - 7 * s), new PointF(cx + 1 * s, cy + 7 * s), new PointF(cx - 4 * s, cy + 3 * s) });
        using var pen = new Pen(Ink(.8f * a), 1.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, cx - 3 * s, cy - 5 * s, 10 * s, 10 * s, -50, 100);
    }

    /// <summary>收起时的小封面：圆的，像唱片一样慢慢转（12 秒一圈），中间一个小孔。</summary>
    void DrawDisc(Graphics g, Bitmap art, float cx, float cy, float r, float alpha, bool spin)
    {
        float s = S;
        using var attributes = alpha < .99f ? Fade(alpha) : null;
        using var brush = attributes != null ? new TextureBrush(art, new Rectangle(0, 0, art.Width, art.Height), attributes) : new TextureBrush(art);
        brush.WrapMode = WrapMode.Clamp;
        using var m = new Matrix();
        m.Translate(cx, cy);
        if (spin) m.Rotate(Now % 12000 / 12000f * 360);
        m.Scale(2 * r / art.Width, 2 * r / art.Height);
        m.Translate(-art.Width / 2f, -art.Height / 2f);
        brush.Transform = m;
        g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
        using (var hole = new SolidBrush(Deep(.85f * alpha))) g.FillEllipse(hole, cx - 2.2f * s, cy - 2.2f * s, 4.4f * s, 4.4f * s);
        using var ring = new Pen(Ink(.35f * alpha), 1 * s);
        g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
    }

    static void DrawImage(Graphics g, Image image, RectangleF r, float alpha)
    {
        if (alpha >= .99f)
        {
            g.DrawImage(image, r);
            return;
        }
        using var attributes = Fade(alpha);
        g.DrawImage(image, Rectangle.Round(r), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
    }

    static ImageAttributes Fade(float alpha)
    {
        var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(alpha, 0, 1) });
        return attributes;
    }

    /// <summary>
    /// 给人看的歌名和歌手：YouTube 的“夏婉安 - 習慣『…』【動態歌詞Lyrics】”/频道名 洗成“習慣”/“夏婉安”
    /// （跟找歌词用同一套规则）。洗完什么都不剩就用原来的。
    /// </summary>
    (string Title, string Artist) Pretty(NowPlaying np)
    {
        if (prettyFor == (np.Title, np.Artist)) return pretty;
        var (titles, artist) = LyricsSource.Candidates(np.Title, np.Artist);
        prettyFor = (np.Title, np.Artist);
        pretty = (titles[0].Length > 0 ? titles[0] : np.Title, artist.Length > 0 ? artist : np.Artist);
        return pretty;
    }

    /// <summary>字太长就截断加省略号（量过的结果记下来，每帧不用重新量）。</summary>
    string Fit(string text, Font font, float width)
    {
        var key = (text, font, (int)width);
        if (fitCache.TryGetValue(key, out var done)) return done;
        if (fitCache.Count > 32) fitCache.Clear();
        string result = text;
        if (Measure(text, font) * S > width)
        {
            int n = text.Length;
            while (n > 1 && Measure(text[..n] + "…", font) * S > width) n--;
            result = text[..n].TrimEnd() + "…";
        }
        return fitCache[key] = result;
    }

    static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    /// <summary>媒体控制报的程序 id 换成人看得懂的名字。</summary>
    static string AppName(string id)
    {
        string name = id;
        int bang = name.IndexOf('!');
        if (bang >= 0) name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant() switch
        {
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "308046b0af4a39cb" or "firefox" => "Firefox",
            "spotify" => "Spotify",
            "cloudmusic" => L.T("网易云音乐", "NetEase Cloud Music"),
            "qqmusic" => L.T("QQ音乐", "QQ Music"),
            "kugou" => L.T("酷狗", "Kugou"),
            "microsoft.zunemusic" => L.T("媒体播放器", "Media Player"),
            "applemusic" or "appleinc.applemusicwin" => "Apple Music",
            _ => Shorten(name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : id, 14),
        };
    }

    void MusicMouseDown(Point p)
    {
        foreach (var (rect, key, value) in musicHits)
        {
            if (!rect.Contains(p)) continue;
            var np = media;
            switch (key)
            {
                case "prev": app.Media?.Send(MediaWatcher.Command.Previous); break;
                case "next": app.Media?.Send(MediaWatcher.Command.Next); break;
                case "play":
                    app.Media?.Send(MediaWatcher.Command.TogglePlay);
                    if (np != null) media = np with { Playing = !np.Playing, Position = np.CurrentPosition, UpdatedUtc = DateTime.UtcNow };
                    break;
                case "seek":
                    seekDrag = true;
                    SeekMove(p.X);
                    break;
                case "volume":
                    volumeDrag = true;
                    VolumeMove(p.X);
                    break;
                case "lyrics": ClickSetting("lyrics", 0); break;
                case "sleep":
                    if (app.Media == null) break;
                    if (app.Media.SleepAtUtc == null) sleepStep = 0;
                    sleepStep = (sleepStep + 1) % SleepMinutes.Length;
                    app.Media.SleepAtUtc = SleepMinutes[sleepStep] == 0 ? null : DateTime.UtcNow.AddMinutes(SleepMinutes[sleepStep]);
                    break;
                case "source":
                    if (np != null && value < np.Sources.Length) app.Media?.Select(np.Sources[value]);
                    break;
                case "files":
                    SetMode(Mode.Open);
                    break;
                case "lock": ToggleLock(); break;
                case "skipad":
                    SkipAd();
                    break;
                case "output":
                    menuOpen = true;
                    try { ShowOutputMenu(PointToScreen(new Point((int)rect.X, (int)rect.Bottom + 4))); }
                    finally { menuOpen = false; }
                    break;
            }
            Redraw();
            return;
        }
        // 空白处：双击锁住 / 解开；按住往下拖（开了的话）把卡片拖出来放到桌面上
        var dbl = SystemInformation.DoubleClickSize;
        if (lastCardClick >= 0 && Now - lastCardClick <= SystemInformation.DoubleClickTime
            && Math.Abs(p.X - lastCardClickAt.X) <= dbl.Width && Math.Abs(p.Y - lastCardClickAt.Y) <= dbl.Height)
        {
            lastCardClick = -1;
            ToggleLock();
            return;
        }
        lastCardClick = Now;
        lastCardClickAt = p;
        cardPressed = true;
        cardPressAt = p;
    }

    void ToggleLock()
    {
        musicPinned = !musicPinned;
        musicHoldUntil = 0; // 解开后照常：鼠标不在旁边就收起
        if (musicPinned) ripple = Now; // 锁上时一圈光扩散出去
        Redraw();
    }

    // ---------- 右键菜单：锁住、放到桌面、歌词、输出设备 ----------

    void ShowMusicMenu(Point at)
    {
        using var menu = new PopupMenu();
        menu.Add(L.T("锁住卡片（不自动收起）", "Lock card (don't auto-close)"), ToggleLock, check: musicPinned);
        if (app.Config.MusicFloat) menu.Add(L.T("放到桌面上（小播放器）", "Put on desktop (mini player)"), DetachToDesktop);
        AddLyricItems(menu);
        if (app.Config.OutputSwitch) AddOutputItems(menu.Sub(L.T("声音输出到", "Play sound through")));
        menu.Show(at, app.MenuOwner);
    }

    void AddLyricItems(PopupMenu menu)
    {
        if (!app.Config.Lyrics || media == null) return;
        menu.Separator();
        var l = lyrics;
        if (l is { Instrumental: false, Lines.Length: > 0 })
        {
            string from = L.Source(l.Source) + (l.Lines.Any(x => x.Words != null) ? L.T(" · 逐字", " · word-by-word") : "") + (l.Choices > 1 ? L.T($" · 第 {l.Choice + 1}/{l.Choices} 个", $" · {l.Choice + 1} of {l.Choices}") : "");
            menu.Add(L.T("歌词来自 ", "Lyrics from ") + from, () => { }, enabled: false);
        }
        menu.Add(L.T("换一个版本的歌词", "Try another version"), () => RefindLyrics((l?.Choices > 0 ? l.Choice : 0) + 1, null), enabled: !lyricsLoading);
        menu.Add(L.T("手动搜索歌词…", "Search lyrics…"), () =>
        {
            if (media == null) return;
            var (title, artist) = Pretty(media);
            string? text = InputDialog.Show(L.T("搜索歌词（歌名 歌手）", "Search lyrics (title artist)"), (title + " " + artist).Trim(), Cursor.Position);
            if (!string.IsNullOrWhiteSpace(text)) RefindLyrics(0, text);
        }, enabled: !lyricsLoading);
        if (l is { Instrumental: false, Lines.Length: > 0 })
        {
            menu.Add(L.T("歌词提早 0.5 秒", "Lyrics 0.5s earlier"), () => ShiftLyrics(-500));
            menu.Add(L.T("歌词延后 0.5 秒", "Lyrics 0.5s later"), () => ShiftLyrics(500));
            // 开头对得上、越往后越对不上：歌词那边的版本快慢跟这首不一样，整首拉长或压短一点
            menu.Add(L.T("歌词越唱越快了（放慢 2%）", "Lyrics drift ahead (slow down 2%)"), () => StretchLyrics(1.02));
            menu.Add(L.T("歌词越唱越慢了（加快 2%）", "Lyrics drift behind (speed up 2%)"), () => StretchLyrics(1 / 1.02));
            if (l.OffsetMs != 0 || Math.Abs(l.Scale - 1) > .001)
            {
                var parts = new List<string>();
                if (l.OffsetMs != 0) parts.Add(L.T($"{l.OffsetMs / 1000.0:+0.0;-0.0} 秒", $"{l.OffsetMs / 1000.0:+0.0;-0.0}s"));
                if (Math.Abs(l.Scale - 1) > .001) parts.Add(l.Scale > 1 ? L.T($"放慢 {(l.Scale - 1) * 100:0}%", $"slowed {(l.Scale - 1) * 100:0}%") : L.T($"加快 {(1 - l.Scale) * 100:0}%", $"sped up {(1 - l.Scale) * 100:0}%"));
                menu.Add(L.T($"还原歌词时间（现在 {string.Join("、", parts)}）", $"Reset lyric timing (now {string.Join(", ", parts)})"), () =>
                {
                    if (lyrics == null || media == null) return;
                    lyrics = lyrics.Stretched(1 / lyrics.Scale).Shifted(-lyrics.OffsetMs);
                    LyricsSource.SaveScale(media.Title, media.Artist, 1);
                    LyricsSource.SaveOffset(media.Title, media.Artist, 0);
                    LyricsChangedByHand();
                });
            }
        }
    }

    /// <summary>歌词整首拉长（&gt;1）或压短一点，记在缓存里。</summary>
    void StretchLyrics(double factor)
    {
        if (lyrics == null || media == null) return;
        lyrics = lyrics.Stretched(factor);
        LyricsSource.SaveScale(media.Title, media.Artist, lyrics.Scale);
        LyricsChangedByHand();
    }

    void LyricsChangedByHand()
    {
        ClearLyricPaths();
        lyricIndex = pillLyricIndex = -1;
        BuildPill();
        UpdateFloat();
        Redraw();
    }

    /// <summary>歌词对不上（MV 有片头）：整首往前或往后挪，记在缓存里，下次还是对的。</summary>
    void ShiftLyrics(int ms)
    {
        if (lyrics == null || media == null) return;
        lyrics = lyrics.Shifted(ms);
        LyricsSource.SaveOffset(media.Title, media.Artist, lyrics.OffsetMs);
        ClearLyricPaths();
        lyricIndex = pillLyricIndex = -1;
        BuildPill();
        UpdateFloat();
        Redraw();
    }

    void ShowOutputMenu(Point at)
    {
        using var menu = new PopupMenu();
        AddOutputItems(menu);
        if (menu.Count > 0) menu.Show(at, app.MenuOwner);
    }

    /// <summary>所有能用的输出设备，现在用的那个打勾；点一个就换过去（跟在 Windows 声音设置里选一样）。</summary>
    void AddOutputItems(PopupMenu menu)
    {
        string current = Audio.DefaultOutput()?.Id ?? "";
        foreach (var o in Audio.Outputs())
        {
            string id = o.Id;
            menu.Add(o.Name, () =>
            {
                if (!Audio.SetDefaultOutput(id)) Log.Info("切换输出设备失败");
                ReadVolume();
                Redraw();
            }, check: id == current);
        }
    }

    // ---------- 桌面上的小播放器（从卡片里拖出来的） ----------

    public bool FloatShouldShow => floatWin != null && media != null;
    public bool FloatLive => media is { Playing: true };

    void OpenFloat(Point topLeft)
    {
        if (floatWin != null)
        {
            floatWin.MoveCard(topLeft);
            return;
        }
        floatWin = new MusicFloat(this, topLeft);
        floatWin.Show();
        EnsureLyrics();
        UpdateFloat();
    }

    void CloseFloat()
    {
        if (floatWin == null) return;
        var closing = floatWin;
        floatWin = null;
        closing.Close(); // 非模态窗口关掉时自己会 Dispose
        floatBackdrop.Clear();
    }

    /// <summary>歌、歌词、播放状态变了：小播放器跟着重画；没在放东西时藏起来。</summary>
    void UpdateFloat()
    {
        if (floatWin == null) return;
        if (!FloatShouldShow)
        {
            if (floatWin.Visible) floatWin.Hide();
            return;
        }
        if (!floatWin.Visible && !FullscreenBusy()) floatWin.Show();
        floatWin.Repaint(FloatLive);
    }

    /// <summary>右键“放到桌面上”：放回上次的位置（第一次放在屏幕右下角）。</summary>
    void DetachToDesktop()
    {
        float s = S;
        var size = new Size((int)(MusicFloat.CardW * s), (int)(MusicFloat.CardH * s));
        var pos = new Point(app.Config.MusicFloatX, app.Config.MusicFloatY);
        if (pos.X == int.MinValue || !Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new Rectangle(pos, size))))
            pos = new Point(Area.Right - size.Width - (int)(28 * s), Area.Bottom - size.Height - (int)(28 * s));
        musicPinned = false;
        OpenFloat(pos);
        SetMode(downloadName != null ? Mode.Notify : Mode.Idle);
    }

    /// <summary>在卡片空白处按住往下拖：卡片“拎”下来缩成小播放器，跟着鼠标走，松手就放在那。</summary>
    void BeginDetach(Point local)
    {
        cardPressed = false;
        var c = CapsuleRect;
        float s = S, fw = MusicFloat.CardW * s, fh = MusicFloat.CardH * s;
        detachGrab = new Point((int)(Math.Clamp((local.X - c.X) / c.Width, .1f, .9f) * fw), (int)(Math.Clamp((local.Y - c.Y) / c.Height, .2f, .8f) * fh));
        var at = Cursor.Position;
        musicPinned = false;
        detaching = true;
        OpenFloat(new Point(at.X - detachGrab.X, at.Y - detachGrab.Y));
        SetMode(downloadName != null ? Mode.Notify : Mode.Idle);
        Capture = true;
    }

    void EndDetach()
    {
        if (!detaching) return;
        detaching = false;
        Capture = false;
        FloatMoved(released: true);
    }

    /// <summary>小播放器被拖了：松手时拖回屏幕顶上胶囊附近就收回胶囊；拖出屏幕的拉回来一点；记住位置。</summary>
    public void FloatMoved(bool released)
    {
        if (floatWin == null || !released) return;
        var card = floatWin.CardOnScreen;
        var screen = Screen.FromRectangle(card);
        float cx = capsuleOnScreen.X + capsuleOnScreen.Width / 2;
        if (card.Top < screen.Bounds.Top + 24 * S && Math.Abs(card.X + card.Width / 2 - cx) < 280 * S)
        {
            CloseFloat();
            ShowMusic();
            return;
        }
        var area = screen.WorkingArea;
        int x = Math.Clamp(card.X, area.Left - card.Width / 2, area.Right - card.Width / 2);
        int y = Math.Clamp(card.Y, area.Top, area.Bottom - card.Height / 2);
        if (x != card.X || y != card.Y) floatWin.MoveCard(new Point(x, y));
        app.Config.MusicFloatX = x;
        app.Config.MusicFloatY = y;
        app.ScheduleSave();
    }

    public void FloatMenu(Point at)
    {
        using var menu = new PopupMenu();
        menu.Add(L.T("收回胶囊", "Back to capsule"), () =>
        {
            CloseFloat();
            ShowMusic();
        });
        AddLyricItems(menu);
        if (app.Config.OutputSwitch) AddOutputItems(menu.Sub(L.T("声音输出到", "Play sound through")));
        menu.Separator();
        menu.Add(L.T("关掉小播放器", "Close mini player"), CloseFloat);
        menu.Show(at, app.MenuOwner);
    }

    public void FloatWheel(int delta)
    {
        if (media == null) return;
        if (volume < 0) ReadVolume();
        if (volume < 0) return;
        volume = Math.Clamp(volume + delta / 120f * .05f, 0, 1);
        Audio.SetVolume(media.Source, volume, volumePerApp);
        volumeRead = Now;
        volumeShownSince = Now;
        floatWin?.Animate(VolumeShowMs + 60);
    }

    public bool FloatMouseDown(Point p)
    {
        foreach (var (rect, key, _) in floatHits)
        {
            if (!rect.Contains(p)) continue;
            var np = media;
            switch (key)
            {
                case "prev": app.Media?.Send(MediaWatcher.Command.Previous); break;
                case "next": app.Media?.Send(MediaWatcher.Command.Next); break;
                case "play":
                    app.Media?.Send(MediaWatcher.Command.TogglePlay);
                    if (np != null) media = np with { Playing = !np.Playing, Position = np.CurrentPosition, UpdatedUtc = DateTime.UtcNow };
                    break;
                case "skipad":
                    SkipAd();
                    break;
            }
            UpdateFloat();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 小播放器的样子：深色底（开了模糊封面就是封面），左边封面（鼠标过来时盖上 上一首/播放/下一首），
    /// 右边歌名一小行、底下三行歌词（没有歌词就是大歌名），最底下一条细细的进度。字一律白色。
    /// </summary>
    public void PaintFloat(Graphics g, RectangleF card, Point mouse, bool hover)
    {
        floatHits.Clear();
        var np = media;
        float s = S, radius = 22 * s;
        var (accent, amount) = Accent();
        using var shape = Shapes.RoundRect(card, radius);
        var state = g.Save();
        g.SetClip(shape, CombineMode.Intersect);
        lightInk = true;
        try
        {
            var cover = np?.Cover ?? np?.Art;
            Fill(g, card, Color.FromArgb(238, Mix(app.Tone.Tint, Color.Black, .74f)));
            if (cover != null && app.Config.MusicBackdrop) floatBackdrop.Draw(g, card, card.Size, cover, Drift(), 1);
            else if (amount > .01f) EllipseGlow(g, new PointF(card.X + 70 * s, card.Y + card.Height / 2), 250 * s, 120 * s, Color.FromArgb(Byte(.5f * amount), accent), card);
            if (np == null) return;

            var art = new RectangleF(card.X + 16 * s, card.Y + 16 * s, card.Height - 32 * s, card.Height - 32 * s);
            DrawArt(g, art, np, cover, 1);
            if (hover)
            {
                FillRound(g, art, 14 * s, Color.FromArgb(130, 0, 0, 0));
                float by = art.Y + art.Height / 2, slot = art.Width / 3;
                using var brush = new SolidBrush(Color.White);
                for (int i = 0; i < 3; i++)
                {
                    var b = new RectangleF(art.X + slot * i, art.Y, slot, art.Height);
                    float cx = b.X + b.Width / 2;
                    if (b.Contains(mouse)) FillRound(g, new RectangleF(cx - 13 * s, by - 13 * s, 26 * s, 26 * s), 13 * s, Color.FromArgb(55, 255, 255, 255));
                    switch (i)
                    {
                        case 0: Triangle(g, brush, cx + 2.5f * s, by, -1, 6 * s); g.FillRectangle(brush, cx - 6 * s, by - 5 * s, 1.8f * s, 10 * s); break;
                        case 1:
                            if (np.Playing)
                            {
                                FillRound(g, new RectangleF(cx - 5 * s, by - 7 * s, 3.5f * s, 14 * s), 1.4f * s, Color.White);
                                FillRound(g, new RectangleF(cx + 1.5f * s, by - 7 * s, 3.5f * s, 14 * s), 1.4f * s, Color.White);
                            }
                            else Triangle(g, brush, cx - 2 * s, by, 1, 8 * s);
                            break;
                        case 2: Triangle(g, brush, cx - 2.5f * s, by, 1, 6 * s); g.FillRectangle(brush, cx + 4.2f * s, by - 5 * s, 1.8f * s, 10 * s); break;
                    }
                    floatHits.Add((b, i == 0 ? "prev" : i == 1 ? "play" : "next", 0));
                }
            }

            float x0 = art.Right + 18 * s, w = card.Right - 18 * s - x0;
            var (title, artist) = Pretty(np);
            var position = np.CurrentPosition;
            if (adSkippable)
            {
                // 能跳过广告：右上角一颗亮着的小按钮
                string skip = L.T("跳过广告", "Skip ad");
                float kw = Measure(skip, chipFont) * s + 36 * s;
                var kb = new RectangleF(card.Right - 14 * s - kw, card.Y + 10 * s, kw, 24 * s);
                FillRound(g, kb, 12 * s, Color.FromArgb(235, 255, 255, 255));
                DrawSkip(g, kb.X + 15 * s, kb.Y + kb.Height / 2, Deep(1));
                DrawTight(g, skip, chipFont, Deep(1), kb.X + 26 * s, kb.Y + kb.Height / 2);
                floatHits.Add((kb, "skipad", 0));
            }
            if (app.Config.Lyrics && lyrics is { Instrumental: false, Lines.Length: > 0 })
            {
                DrawTight(g, Fit(artist.Length > 0 ? $"{title} · {artist}" : title, smallFont, adSkippable ? w - 110 * s : w), smallFont, Ink(.6f), x0, card.Y + 22 * s);
                DrawLyrics(g, new RectangleF(x0, card.Y + 32 * s, w, card.Height - 48 * s), position, 1);
            }
            else
            {
                DrawTight(g, Fit(title, titleFont, w), titleFont, Ink(1), x0, card.Y + 46 * s);
                string under = app.Config.Lyrics && lyricsLoading ? L.T("正在找歌词…", "Finding lyrics…") : artist;
                if (under.Length > 0) DrawTight(g, Fit(under, labelFont, w), labelFont, Ink(.62f), x0, card.Y + 72 * s);
            }
            if (np.Duration > TimeSpan.Zero)
            {
                // 进度：底边一条细线，唱过的部分是歌的颜色
                var track = new RectangleF(x0, card.Bottom - 10 * s, w, 3 * s);
                float progress = (float)Math.Clamp(position.TotalSeconds / np.Duration.TotalSeconds, 0, 1);
                FillRound(g, track, track.Height / 2, Ink(.16f));
                FillRound(g, new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * progress), track.Height), track.Height / 2,
                    Color.FromArgb(235, Mix(Color.White, Mix(accent, Color.White, .2f), amount)));
            }
            DrawVolumeBubble(g, art, 1);
        }
        finally
        {
            lightInk = false;
            g.Restore(state);
        }
        using var edge = new Pen(Color.FromArgb(50, 255, 255, 255), 1.2f * s);
        using var outline = Shapes.RoundRect(RectangleF.Inflate(card, -.6f * s, -.6f * s), radius - .6f * s);
        g.DrawPath(edge, outline);
        if (amount > .01f)
        {
            using var tint = new Pen(Color.FromArgb(Byte(.4f * amount), Mix(accent, Color.White, .3f)), 1.2f * s);
            g.DrawPath(tint, outline);
        }
    }

    // ---------- YouTube 广告 ----------

    /// <summary>开着“跳过广告”、浏览器在放东西：后台去看；不然让它睡着。</summary>
    void UpdateAds()
    {
        if ((app.Config.SkipAds || app.Config.KeepWatching) && adBridge == null && !adBridgeFailed)
        {
            adBridge = AdBridge.Start();
            adBridgeFailed = adBridge == null;
            if (adBridge != null)
            {
                adBridge.Changed += found => Post(() => OnAdChanged(found, fromExtension: true));
                adBridge.Skipped += () => Post(() => OnAdSkipped(fromExtension: true));
                adBridge.Connected += () => Post(() =>
                {
                    UpdateAds();
                    // Edge 里还是旧版扩展：提醒一次去点“重新加载”
                    if (adBridge?.Outdated == true && !adOutdatedShown && !Focusing && mode == Mode.Idle)
                    {
                        adOutdatedShown = true;
                        ShowAdNote(L.T("Edge 扩展要重新加载：⚙ → 歌词与广告", "Reload the Edge extension: ⚙ → Lyrics & ads"), chip: false, ms: 6000);
                    }
                    Redraw();
                });
                adBridge.Failed += () => Post(() => OnAdChanged(true, fromExtension: true));
            }
        }
        if (adBridge != null)
        {
            adBridge.AutoSkip = app.Config.AutoSkipAds;
            adBridge.SkipAds = app.Config.SkipAds;
            adBridge.KeepWatching = app.Config.KeepWatching;
        }
        // 扩展来过就交给它（后台标签页也看得到）；老办法停下，省得两边一起按
        bool want = app.Config.SkipAds && adBridge?.Working != true && media is { Playing: true } np && IsBrowser(np.Source);
        if (want && ads == null)
        {
            ads = new AdSkipper();
            ads.Changed += found => Post(() => OnAdChanged(found, fromExtension: false));
            ads.Skipped += () => Post(() => OnAdSkipped(fromExtension: false));
        }
        if (ads == null) return;
        ads.AutoSkip = app.Config.AutoSkipAds;
        ads.SetActive(want);
    }

    void Post(Action action)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); }
        catch (InvalidOperationException) { } // 正在退出
    }

    /// <summary>
    /// 自带的 Edge 扩展只能由用户自己在 Edge 里加载（浏览器不让别的程序替你装）：
    /// 把文件夹路径复制好、打开 Edge 的扩展页，再告诉一步步怎么点。
    /// </summary>
    void InstallAdExtension()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "edge-extension");
        if (adBridge?.Outdated == true)
        {
            // 装过旧版：Edge 不会自己读新文件，要在扩展页点一下“重新加载”
            if (MessageBox.Show(L.T("玻璃收纳更新了扩展，Edge 里还是旧的。点「确定」打开 Edge 的扩展页，然后：\n\n", "GlassBox updated its extension, but Edge still has the old one. Click OK to open Edge's extensions page, then:\n\n") +
                    L.T("1. 在「玻璃收纳 · YouTube 跳过广告」上点「重新加载」（圆圈箭头）\n", "1. On “GlassBox · YouTube ad skip”, click Reload (the circular arrow)\n") +
                    L.T("2. 把开着的 YouTube 页面刷新一下", "2. Refresh any open YouTube pages"), L.T("更新 Edge 扩展", "Update Edge extension"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
            {
                try { Process.Start(new ProcessStartInfo("msedge", "edge://extensions/") { UseShellExecute = true }); }
                catch (Exception ex) { Log.Error(ex); }
            }
            return;
        }
        if (!File.Exists(Path.Combine(folder, "manifest.json")))
        {
            MessageBox.Show(L.T("没找到扩展文件夹：\n", "Extension folder not found:\n") + folder + L.T("\n\n请重新解压玻璃收纳。", "\n\nPlease extract GlassBox again."), L.T("玻璃收纳", "GlassBox"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!app.Config.SkipAds)
        {
            app.Config.SkipAds = true;
            app.ScheduleSave();
            UpdateAds();
        }
        try { Clipboard.SetText(folder); } catch (Exception ex) { Log.Error(ex); }
        var go = MessageBox.Show(
            L.T("扩展文件夹的路径已经复制好了。点「确定」打开 Edge 的扩展页，然后在 Edge 里：\n\n", "The extension folder path is copied. Click OK to open Edge's extensions page, then in Edge:\n\n") +
            L.T("1. 打开左边（或右上角）的「开发人员模式」\n", "1. Turn on “Developer mode” (on the left or top right)\n") +
            L.T("2. 点「加载解压缩的扩展」\n", "2. Click “Load unpacked”\n") +
            L.T("3. 在弹出的窗口上面的地址栏按 Ctrl+V 粘贴，回车，再点「选择文件夹」\n", "3. In the window that opens, press Ctrl+V in the address bar, press Enter, then click “Select Folder”\n") +
            L.T("4. 已经开着的 YouTube 页面刷新一下\n\n", "4. Refresh any YouTube pages that are already open\n\n") +
            L.T("扩展只在 YouTube 上工作，只和本机的玻璃收纳说话。装好后放 YouTube 时这里会显示「已连上」；", "The extension only runs on YouTube and only talks to GlassBox on this PC. Once it's in, this shows “Connected” while YouTube plays. ") +
            L.T("Edge 偶尔会提示“关闭开发人员模式的扩展”，选保留就行。不想用了在 Edge 扩展页删掉它即可。\n\n", "Edge may sometimes ask you to turn off developer-mode extensions; just keep it. To stop using it, remove it on Edge's extensions page.\n\n") +
            L.T("文件夹：", "Folder: ") + folder,
            L.T("装 Edge 扩展", "Install Edge extension"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (go != DialogResult.OK) return;
        try { Process.Start(new ProcessStartInfo("msedge", "edge://extensions/") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(ex); }
    }

    void SkipAd()
    {
        ads?.Skip();
        adBridge?.Skip();
        adSkippable = false; // 先把按钮收起来，按没按成一会儿就知道
    }

    void OnAdChanged(bool found, bool fromExtension)
    {
        if (fromExtension) adFromExt = found;
        else adFromUia = found;
        adSkippable = (adFromUia || adFromExt) && app.Config.SkipAds;
        // 收着的时候：胶囊滑下来提示“广告可以跳过了 · 跳过”（正在提示别的、专注时不打断）
        if (adSkippable && !app.Config.AutoSkipAds && !Focusing && (mode == Mode.Idle || mode == Mode.Notify && adNote))
            ShowAdNote(L.T("广告可以跳过了", "Ad can be skipped"), chip: true, ms: 15000);
        else if (!adSkippable && adNote && adNoteChip && mode == Mode.Notify) SetMode(Mode.Idle);
        UpdateFloat();
        Redraw();
    }

    void OnAdSkipped(bool fromExtension)
    {
        if (fromExtension) adFromExt = false;
        else adFromUia = false;
        adSkippable = (adFromUia || adFromExt) && app.Config.SkipAds;
        if (!Focusing && (mode == Mode.Idle || mode == Mode.Notify && adNote)) ShowAdNote(L.T("已跳过广告", "Ad skipped"), chip: false, ms: 1600);
        UpdateFloat();
        Redraw();
    }

    void ShowAdNote(string text, bool chip, int ms)
    {
        adNoteText = text;
        adNoteChip = chip;
        noteUndo = chargeNote = false;
        notePath = null;
        noteFence = null;
        notePage = null;
        adNote = true;
        SetMode(Mode.Notify);
        BuildPill();
        Schedule(Pending.EndNote, ms);
    }

    void SeekMove(float x)
    {
        if (seekTrack.Width <= 0) return;
        seekValue = Math.Clamp((x - seekTrack.X) / seekTrack.Width, 0, 1);
        Redraw();
    }

    void VolumeMove(float x)
    {
        if (volumeTrack.Width <= 0 || media == null) return;
        volume = Math.Clamp((x - volumeTrack.X) / volumeTrack.Width, 0, 1);
        Audio.SetVolume(media.Source, volume, volumePerApp);
        volumeRead = Now;
        Redraw();
    }


    // ---------- 迷你播放器 ----------

    void DrawPlayer(Graphics g, RectangleF r, float a)
    {
        float s = S;
        var np = media!;
        float x = r.X;
        playerDrawn = Now;
        // 封面
        var art = new RectangleF(x, r.Y, r.Height, r.Height);
        if (np.Art != null)
        {
            var state = g.Save();
            using var shape = Shapes.RoundRect(art, 6 * s);
            g.SetClip(shape, CombineMode.Intersect);
            g.DrawImage(np.Art, art);
            g.Restore(state);
        }
        else FillRound(g, art, 6 * s, Ink(.14f * a));
        x = art.Right + 10 * s;

        // 三个按钮放右边：上一首、播放/暂停、下一首
        float bw = 26 * s, by = r.Y + r.Height / 2;
        float bx = r.Right - bw * 3;
        for (int i = 0; i < 3; i++)
        {
            var b = new RectangleF(bx + i * bw, r.Y, bw, r.Height);
            mediaButtons[i] = b;
            bool hot = b.Contains(PointToClient(Cursor.Position));
            if (hot) FillRound(g, RectangleF.Inflate(b, -1 * s, -1 * s), 8 * s, Ink(.12f * a));
            using var brush = new SolidBrush(Ink(.9f * a));
            float cx = b.X + b.Width / 2;
            switch (i)
            {
                case 0: Triangle(g, brush, cx + 3 * s, by, -1, 6 * s); g.FillRectangle(brush, cx - 6 * s, by - 5 * s, 1.8f * s, 10 * s); break;
                case 1:
                    if (np.Playing)
                    {
                        g.FillRectangle(brush, cx - 4.5f * s, by - 6 * s, 3 * s, 12 * s);
                        g.FillRectangle(brush, cx + 1.5f * s, by - 6 * s, 3 * s, 12 * s);
                    }
                    else Triangle(g, brush, cx - 3 * s, by, 1, 7 * s);
                    break;
                case 2: Triangle(g, brush, cx - 3 * s, by, 1, 6 * s); g.FillRectangle(brush, cx + 4.2f * s, by - 5 * s, 1.8f * s, 10 * s); break;
            }
        }

        // 歌名 · 歌手，下面一条细进度
        var text = new RectangleF(x, r.Y, bx - x - 8 * s, r.Height);
        playerInfo = new RectangleF(r.X, r.Y, text.Right - r.X, r.Height);
        var clip = g.Save();
        g.SetClip(text, CombineMode.Intersect);
        var (songTitle, songArtist) = Pretty(np);
        float titleW = Measure(songTitle, labelFont) * s;
        DrawTight(g, songTitle, labelFont, Ink(.95f * a), x, r.Y + 9 * s);
        if (songArtist.Length > 0) DrawTight(g, "· " + songArtist, smallFont, Ink(.6f * a), x + titleW + 6 * s, r.Y + 9.5f * s);
        g.Restore(clip);
        if (np.Duration > TimeSpan.Zero)
        {
            float progress = (float)Math.Clamp(np.CurrentPosition.TotalSeconds / np.Duration.TotalSeconds, 0, 1);
            var track = new RectangleF(x, r.Bottom - 4 * s, text.Width, 2.5f * s);
            FillRound(g, track, 1.25f * s, Ink(.16f * a));
            var (accent, amount) = Accent();
            var fill = Color.FromArgb(Byte(.85f * a), Mix(Ink(1), Mix(accent, Color.White, .2f), amount));
            FillRound(g, new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * progress), track.Height), 1.25f * s, fill);
        }
    }

    static void Triangle(Graphics g, Brush brush, float x, float cy, int direction, float size)
    {
        // direction 1 朝右（播放、下一首），-1 朝左（上一首）
        var points = new[] { new PointF(x - direction * size * .55f, cy - size), new PointF(x - direction * size * .55f, cy + size), new PointF(x + direction * size * .85f, cy) };
        g.FillPolygon(brush, points);
    }

    void DrawTile(Graphics g, int i, RectangleF r, float a, float zoom = 1)
    {
        float s = S;
        var item = shown[i];
        var bg = r;
        bg.Inflate(-2 * s, -2 * s);
        float highlight = i == hover ? .10f : 0;
        if (i == selected) highlight = Math.Max(highlight, .18f);
        if (freshPath != null && freshSince >= 0 && item.Path.Equals(freshPath, StringComparison.OrdinalIgnoreCase))
        {
            float t = (Now - freshSince) / 1800f;
            if (t < 1) highlight = Math.Max(highlight, .28f * (t < .4f ? 1 : 1 - (t - .4f) / .6f));
        }
        int size = app.Icons.Size;
        float zt = ZoomT(zoom);
        float lift = 3 * s * zt;
        // 放大后的图标比底下那块亮底高：亮底跟着往上长，图标上面始终留 6px
        float iconTop = r.Y + 10 * s - lift - (zoom - 1) * size;
        if (iconTop - 6 * s < bg.Y)
        {
            float grow = bg.Y - (iconTop - 6 * s);
            bg.Y -= grow;
            bg.Height += grow;
        }
        if (highlight > 0) FillRound(g, bg, 14 * s, Ink(highlight * a));

        var box = new Rectangle((int)(r.X + (r.Width - size) / 2), (int)(r.Y + 10 * s - lift), size, size);
        var icon = app.Icons.Get(item.Path);
        if (icon != null)
        {
            var fit = Shapes.Fit(icon.Size, box);
            if (zoom < 1.002f)
            {
                if (a >= .99f) g.DrawImage(icon, fit); // 不放大时按整像素画，图标最清楚
                else
                {
                    using var attributes = new ImageAttributes();
                    attributes.SetColorMatrix(new ColorMatrix { Matrix33 = a });
                    g.DrawImage(icon, fit, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attributes);
                }
            }
            else
            {
                // 以图标底边中点为准往上、往两边长，不会压到下面的名字
                float w = fit.Width * zoom, h = fit.Height * zoom;
                float x = fit.X + fit.Width / 2f - w / 2, y = fit.Bottom - h;
                var corners = new[] { new PointF(x, y), new PointF(x + w, y), new PointF(x, y + h) };
                using var attributes = new ImageAttributes();
                if (a < .99f) attributes.SetColorMatrix(new ColorMatrix { Matrix33 = a });
                g.DrawImage(icon, corners, new RectangleF(0, 0, icon.Width, icon.Height), GraphicsUnit.Pixel, attributes);
            }
        }
        var label = new RectangleF(r.X + 4 * s, r.Y + 10 * s + size + 7 * s, r.Width - 8 * s, labelFont.GetHeight(g) * 2 + 1);
        using var brush = new SolidBrush(Ink(a));
        g.DrawString(item.Name, labelFont, brush, label, LabelFormat);
    }

    void RefreshShown(bool keepScroll)
    {
        shown.Clear();
        if (query.Length > 0) shown.AddRange(Search(query));
        else if (CurrentPage is { } page)
            shown.AddRange(page.Kind switch
            {
                PageKind.Frequent => app.FrequentItems(FrequentMax),
                PageKind.Downloads => app.RecentDownloads,
                PageKind.Screenshots => app.Screenshots,
                _ => app.ItemsOf(page.Fence!),
            });
        if (trashing.Count > 0) shown.RemoveAll(item => trashing.Contains(item.Path)); // 刚丢掉的（还能撤销）
        if (selected >= shown.Count) selected = shown.Count - 1;
        if (!keepScroll)
        {
            scroll = scrollTarget = 0;
            tilePos.Clear(); // 换页、换搜索词：图标直接出现在位置上，不从上一页的位置滑过来
            tileZoom.Clear();
        }
        else scrollTarget = Math.Clamp(scrollTarget, 0, MaxScroll);
        Kick();
    }

    /// <summary>
    /// 搜索：名字开头对上 → 拼音首字母开头对上（wx → 微信）→ 名字里包含 → 首字母包含；
    /// 同一档里常打开的排前面。“下载”文件夹里最近的文件也一起搜。
    /// </summary>
    IEnumerable<DesktopItem> Search(string text)
    {
        bool letters = text.All(ch => ch < 128 && char.IsLetterOrDigit(ch));
        string lower = text.ToLowerInvariant();
        var pool = app.AllItems.Concat(app.RecentDownloads.Concat(app.Screenshots).Where(d => !app.TryGetItem(d.Path, out _)).DistinctBy(d => d.Path, StringComparer.OrdinalIgnoreCase));
        return pool
            .Select(item => (Item: item, Rank: Rank(item)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenByDescending(x => app.UseScore(x.Item.Path))
            .ThenBy(x => x.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(300)
            .Select(x => x.Item);

        int Rank(DesktopItem item)
        {
            if (item.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) return 0;
            var (all, words) = letters ? Pinyin.Initials(item.Name) : ("", "");
            if (letters && (words.StartsWith(lower, StringComparison.Ordinal) || all.StartsWith(lower, StringComparison.Ordinal))) return 1;
            if (item.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return 2;
            if (letters && (words.Contains(lower, StringComparison.Ordinal) || all.Contains(lower, StringComparison.Ordinal))) return 3;
            return -1;
        }
    }

    void SetTab(int index)
    {
        if (pages.Count == 0) return;
        tab = (index % pages.Count + pages.Count) % pages.Count;
        query = "";
        selected = -1;
        keyboardPicked = false;
        chipFollow = true;
        ClosePreview();
        RefreshShown(keepScroll: false);
        Redraw();
    }

    void SetQuery(string next)
    {
        if (next == query) return;
        query = next;
        keyboardPicked = false;
        RefreshShown(keepScroll: false);
        selected = query.Length > 0 && shown.Count > 0 ? 0 : -1;
        caretOn = true;
        Redraw();
    }

    // ---------- 空格预览 ----------

    static readonly HashSet<string> TextLike = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".log", ".json", ".xml", ".ini", ".yaml", ".yml", ".cs", ".py", ".js", ".ts", ".java", ".c", ".cpp", ".h", ".html", ".css", ".sql", ".bat", ".ps1", ".sh",
    };

    void ShowPreview(int index)
    {
        if (index < 0 || index >= shown.Count) return;
        var item = shown[index];
        selected = index;
        ScrollTo(index);
        bool same = previewItem?.Path == item.Path;
        previewItem = item;
        if (!same)
        {
            previewImage?.Dispose();
            previewImage = null;
            previewText = null;
        }
        if (previewSince < 0 || !same) previewSince = Now;
        previewInfo = Describe(item);
        int token = ++previewToken;
        int size = (int)Px(420);
        // 缩略图、读文本可能要一会儿（大视频、网盘文件），放到后台；外壳接口要 STA
        var worker = new Thread(() =>
        {
            Bitmap? image = null;
            string? text = null;
            try
            {
                if (item.IsDir)
                {
                    text = string.Join("\n", Directory.EnumerateFileSystemEntries(item.Path).Take(14).Select(Path.GetFileName));
                    image = ShellIcons.Preview(item.Path, size);
                }
                else if (TextLike.Contains(item.Ext)) text = ReadHead(item.Path);
                else image = ShellIcons.Preview(item.Path, size);
            }
            catch (Exception) { }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (token != previewToken || IsDisposed) { image?.Dispose(); return; }
                    previewImage?.Dispose();
                    previewImage = image;
                    previewText = text;
                    Redraw();
                }));
            }
            catch (InvalidOperationException) { image?.Dispose(); } // 窗口已经没了
        }) { IsBackground = true, Name = "GlassBox preview" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Redraw();
    }

    void ClosePreview()
    {
        if (previewItem == null) return;
        previewItem = null;
        previewSince = -1;
        previewToken++;
        previewImage?.Dispose();
        previewImage = null;
        previewText = null;
        Redraw();
    }

    // ---------- 设置卡片 ----------

    void OpenSettings()
    {
        ClosePreview();
        settingsOpen = true;
        settingsSince = Now;
        autoStartOn = AutoStart.Enabled; // 读注册表，只在打开时读一次
        hover = hoverChip = -1;
        Redraw();
    }

    void CloseSettings()
    {
        if (!settingsOpen) return;
        settingsOpen = false;
        settingsSince = -1;
        if (sliderDrag != null) app.ScheduleSave();
        sliderDrag = null;
        Redraw();
    }

    // 两页：“胶囊”是它自己的样子和手感，“桌面与系统”是桌面和 Windows 的开关
    static string[] SettingTabs => L.En
        ? new[] { "Capsule", "Music", "Lyrics & ads", "Effects", "Desktop & system" }
        : new[] { "胶囊", "音乐", "歌词与广告", "动效", "桌面与系统" };
    static readonly string[][][] SettingColumns =
    {
        new[]
        {
            new[] { "width", "height", "topgap", "clarity", "zoom" },
            new[] { "hoveropen", "closespeed", "autohide", "ctrl" },
        },
        new[]
        {
            new[] { "nowplaying", "musiccard", "motion", "song", "btbattery" },
            new[] { "backdrop", "turntable", "float", "ctrlmusic", "output" },
        },
        new[]
        {
            new[] { "lyrics", "netease", "translate", "pilllyrics" },
            new[] { "skipads", "autoskip", "stillwatch", "adext" },
        },
        new[]
        {
            new[] { "sky", "charge", "shotfly" },
            new[] { "focus", "focuslen" },
        },
        new[]
        {
            new[] { "dblclick", "autosort", "sysicons", "shots" },
            new[] { "tbstyle", "lang", "autostart", "trash" },
        },
    };

    bool SettingOn(string key) => key switch
    {
        "song" => app.Config.SongColor,
        "nowplaying" => app.Config.ShowNowPlaying,
        "musiccard" => app.Config.MusicCard,
        "motion" => app.Config.MusicMotion,
        "lyrics" => app.Config.Lyrics,
        "pilllyrics" => app.Config.PillLyrics && app.Config.Lyrics,
        "sky" => app.Config.SkyGlow,
        "btbattery" => app.Config.BluetoothBattery,
        "netease" => app.Config.NeteaseLyrics && app.Config.Lyrics,
        "translate" => app.Config.LyricTranslation && app.Config.NeteaseLyrics && app.Config.Lyrics,
        "stillwatch" => app.Config.KeepWatching,
        "backdrop" => app.Config.MusicBackdrop,
        "turntable" => app.Config.Turntable,
        "float" => app.Config.MusicFloat,
        "ctrlmusic" => app.Config.CtrlLocksMusic,
        "output" => app.Config.OutputSwitch,
        "skipads" => app.Config.SkipAds,
        "autoskip" => app.Config.AutoSkipAds && app.Config.SkipAds,
        "charge" => app.Config.ChargeEffect,
        "shotfly" => app.Config.ShotFly,
        "focus" => app.Config.FocusTimer,
        "ctrl" => app.Config.DoubleCtrlToSummon,
        "dblclick" => app.Config.DoubleClickToHide,
        "autosort" => app.Config.AutoSortNewFiles,
        "shots" => app.Config.ScreenshotPage,
        "trash" => app.Config.TrashDrop,
        "sysicons" => app.Config.HideSystemIcons,
        "autostart" => autoStartOn,
        "autohide" => app.Config.CapsuleAutoHide,
        _ => false,
    };

    // 滑块：范围、步长，读和写都直接对配置
    static (int Min, int Max, int Step) SliderRange(string key) => key switch
    {
        "width" => (560, 1400, 20),
        "height" => (300, 900, 10),
        "topgap" => (0, 80, 2),
        _ => (0, 100, 1),
    };

    int SliderValue(string key) => key switch
    {
        "width" => app.Config.OpenWidth,
        "height" => app.Config.OpenHeight,
        "topgap" => app.Config.CapsuleTopGap,
        _ => app.Config.GlassClarity,
    };

    void SetSliderValue(string key, int value)
    {
        switch (key)
        {
            case "width": app.Config.OpenWidth = value; break;
            case "height": app.Config.OpenHeight = value; break;
            case "topgap": app.Config.CapsuleTopGap = value; break;
            default: app.Config.GlassClarity = value; break; // 下一帧重烤玻璃
        }
    }

    // 分段按钮：每段的字，和现在选的是第几段
    string[] SegmentNames(string key) => key switch
    {
        "zoom" => L.En ? new[] { "Off", "S", "M", "L" } : new[] { "关", "小", "中", "大" },
        "tbstyle" => L.En ? new[] { "Normal", "Clear", "Frosted" } : new[] { "原样", "透明", "磨砂" },
        "hoveropen" => L.En ? new[] { "Fast", "Med", "Slow", "Click" } : new[] { "快", "中", "慢", "点击" },
        "lang" => new[] { "中文", "English" },
        "focuslen" => FocusLengths.Select(m => m.ToString()).ToArray(),
        _ => L.En ? new[] { "Fast", "Med", "Slow" } : new[] { "快", "中", "慢" },
    };

    static readonly int[] FocusLengths = { 15, 25, 45, 60 };

    int SegmentValue(string key) => key switch
    {
        "zoom" => app.Config.HoverZoom,
        "focuslen" => Math.Max(0, Array.IndexOf(FocusLengths, app.Config.FocusMinutes)),
        "tbstyle" => app.Config.TaskbarStyle,
        "hoveropen" => app.Config.HoverOpen,
        "lang" => L.En ? 1 : 0,
        _ => app.Config.CloseSpeed,
    };

    void ClickSetting(string key, int value)
    {
        var config = app.Config;
        switch (key)
        {
            case "zoom":
                config.HoverZoom = value;
                app.ScheduleSave();
                break;
            case "hoveropen":
                config.HoverOpen = value;
                app.ScheduleSave();
                break;
            case "closespeed":
                config.CloseSpeed = value;
                app.ScheduleSave();
                break;
            case "song":
                config.SongColor = !config.SongColor;
                UpdateAccent(media);
                app.ScheduleSave();
                break;
            case "nowplaying":
                config.ShowNowPlaying = !config.ShowNowPlaying;
                BuildPill();
                app.ScheduleSave();
                break;
            case "musiccard":
                config.MusicCard = !config.MusicCard;
                app.ScheduleSave();
                break;
            case "motion":
                config.MusicMotion = !config.MusicMotion;
                BuildPill();
                app.ScheduleSave();
                break;
            case "lyrics":
                config.Lyrics = !config.Lyrics;
                if (!config.Lyrics)
                {
                    lyricsCancel?.Cancel();
                    lyricsKey = "";
                    lyrics = null;
                    lyricsLoading = false;
                }
                EnsureLyrics();
                BuildPill();
                app.ScheduleSave();
                break;
            case "pilllyrics":
                // 收起时滚歌词要先有歌词：打开它时顺便把同步歌词一起打开（那一行写着会联网）
                config.PillLyrics = !(config.PillLyrics && config.Lyrics);
                if (config.PillLyrics) config.Lyrics = true;
                EnsureLyrics();
                BuildPill();
                app.ScheduleSave();
                break;
            case "netease":
                // 要先有同步歌词：打开它时顺便打开（那一行写着会联网）；打开后这首没找到的再找一次
                config.NeteaseLyrics = !(config.NeteaseLyrics && config.Lyrics);
                if (config.NeteaseLyrics) config.Lyrics = true;
                if (lyrics == null && !lyricsLoading) lyricsKey = "";
                EnsureLyrics();
                app.ScheduleSave();
                break;
            case "translate":
                // 翻译是网易云带的：打开时顺便打开网易云歌词和同步歌词；这首重新读一次（以前存的可能还不带翻译）
                config.LyricTranslation = !(config.LyricTranslation && config.NeteaseLyrics && config.Lyrics);
                if (config.LyricTranslation) config.NeteaseLyrics = config.Lyrics = true;
                if (config.LyricTranslation && !lyricsLoading) lyricsKey = "";
                EnsureLyrics();
                app.ScheduleSave();
                break;
            case "stillwatch":
                config.KeepWatching = !config.KeepWatching;
                if (!config.KeepWatching && !config.SkipAds)
                {
                    adBridge?.Dispose();
                    adBridge = null;
                    adBridgeFailed = adFromExt = false;
                }
                UpdateAds();
                app.ScheduleSave();
                break;
            case "backdrop":
                config.MusicBackdrop = !config.MusicBackdrop;
                cardBackdrop.Clear();
                floatBackdrop.Clear();
                UpdateFloat();
                app.ScheduleSave();
                break;
            case "turntable":
                config.Turntable = !config.Turntable;
                turnLast = -1;
                deckCache?.Dispose();
                deckCache = null;
                app.ScheduleSave();
                break;
            case "float":
                config.MusicFloat = !config.MusicFloat;
                if (!config.MusicFloat) CloseFloat();
                app.ScheduleSave();
                break;
            case "ctrlmusic":
                config.CtrlLocksMusic = !config.CtrlLocksMusic;
                app.ScheduleSave();
                break;
            case "output":
                config.OutputSwitch = !config.OutputSwitch;
                app.ScheduleSave();
                break;
            case "skipads":
                config.SkipAds = !config.SkipAds;
                if (!config.SkipAds)
                {
                    ads?.Dispose();
                    ads = null;
                    if (!config.KeepWatching) // 自动继续还要用扩展：传话的留着
                    {
                        adBridge?.Dispose();
                        adBridge = null;
                        adBridgeFailed = false;
                    }
                    adFromUia = adFromExt = adSkippable = false;
                }
                UpdateAds();
                app.ScheduleSave();
                break;
            case "adext":
                InstallAdExtension();
                break;
            case "autoskip":
                config.AutoSkipAds = !(config.AutoSkipAds && config.SkipAds);
                if (config.AutoSkipAds) config.SkipAds = true;
                app.ScheduleSave();
                break;
            case "btbattery":
                config.BluetoothBattery = !config.BluetoothBattery;
                btLevels = new();
                if (config.BluetoothBattery) ReadBluetooth();
                app.ScheduleSave();
                break;
            case "sky":
                config.SkyGlow = !config.SkyGlow;
                app.ScheduleSave();
                break;
            case "charge":
                config.ChargeEffect = !config.ChargeEffect;
                app.ScheduleSave();
                break;
            case "shotfly":
                config.ShotFly = !config.ShotFly;
                app.ScheduleSave();
                break;
            case "focus":
                config.FocusTimer = !config.FocusTimer;
                if (!config.FocusTimer && focusEnd != null) StopFocus();
                app.ScheduleSave();
                break;
            case "focuslen":
                config.FocusMinutes = FocusLengths[Math.Clamp(value, 0, FocusLengths.Length - 1)];
                app.ScheduleSave();
                break;
            case "autosort":
                config.AutoSortNewFiles = !config.AutoSortNewFiles;
                app.ScheduleSave();
                break;
            case "trash":
                config.TrashDrop = !config.TrashDrop;
                app.ScheduleSave();
                break;
            case "shots":
                config.ScreenshotPage = !config.ScreenshotPage;
                BuildPages();
                if (mode == Mode.Open) RefreshShown(keepScroll: false);
                app.ScheduleSave();
                break;
            case "ctrl": app.SetDoubleCtrlToSummon(!config.DoubleCtrlToSummon); break;
            case "dblclick": app.SetDoubleClickToHide(!config.DoubleClickToHide); break;
            case "sysicons": app.SetHideSystemIcons(!config.HideSystemIcons); break;
            case "autostart":
                app.ToggleAutoStart();
                autoStartOn = AutoStart.Enabled;
                break;
            case "autohide": app.SetCapsuleAutoHide(!config.CapsuleAutoHide); break;
            case "tbstyle": app.SetTaskbarStyle(value); break;
            case "lang": app.SetLanguage(value == 1 ? "en" : "zh"); break;
            case "tab": settingsTab = value; break;
        }
        Redraw();
    }

    void SlideTo(float x)
    {
        if (sliderDrag == null || !sliderTracks.TryGetValue(sliderDrag, out var track) || track.Width <= 0) return;
        var (min, max, step) = SliderRange(sliderDrag);
        float t = Math.Clamp((x - track.X) / track.Width, 0, 1);
        int value = min + (int)Math.Round(t * (max - min) / step) * step;
        if (value == SliderValue(sliderDrag)) return;
        SetSliderValue(sliderDrag, value);
        Redraw(); // 宽度、高度、离顶距离：胶囊马上跟着变
    }

    void DrawGear(Graphics g, float a)
    {
        float s = S;
        bool hot = gearRect.Contains(PointToClient(Cursor.Position));
        if (settingsOpen || hot) FillRound(g, gearRect, 12 * s, Ink((settingsOpen ? .2f : .12f) * a));
        float cx = gearRect.X + gearRect.Width / 2, cy = gearRect.Y + gearRect.Height / 2;
        using var pen = new Pen(Ink(.78f * a), 1.7f * s);
        using var teeth = new Pen(Ink(.78f * a), 2.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int k = 0; k < 8; k++)
        {
            float angle = k * MathF.PI / 4, dx = MathF.Cos(angle), dy = MathF.Sin(angle);
            g.DrawLine(teeth, cx + dx * 6.4f * s, cy + dy * 6.4f * s, cx + dx * 8.4f * s, cy + dy * 8.4f * s);
        }
        g.DrawEllipse(pen, cx - 5.6f * s, cy - 5.6f * s, 11.2f * s, 11.2f * s);
        g.DrawEllipse(pen, cx - 2.1f * s, cy - 2.1f * s, 4.2f * s, 4.2f * s);
    }

    void DrawSettings(Graphics g, RectangleF c, float a)
    {
        float s = S;
        float t = Math.Clamp((Now - settingsSince) / 200f, 0, 1);
        float ease = 1 - (1 - t) * (1 - t) * (1 - t);
        a *= ease;
        var area = new RectangleF(c.X + 12 * s, c.Y + 62 * s, c.Width - 24 * s, c.Height - 62 * s - 32 * s);
        area.Inflate(-(1 - ease) * 14 * s, -(1 - ease) * 10 * s);
        settingsCard = area;
        settingHits.Clear();
        sliderTracks.Clear();

        FillRound(g, area, 18 * s, Color.FromArgb(Byte(.86f * a), Mix(app.Tone.Tint, Color.Black, app.Tone.DarkInk ? .15f : .62f)));
        using (var edge = new Pen(Ink(.14f * a), 1 * s))
        using (var shape = Shapes.RoundRect(area, 18 * s))
            g.DrawPath(edge, shape);

        var inner = RectangleF.Inflate(area, -22 * s, -16 * s);
        var mouse = PointToClient(Cursor.Position);
        DrawTight(g, L.T("设置", "Settings"), boldFont, Ink(a), inner.X, inner.Y + 11 * s);

        // 页签：标题右边两颗小胶囊
        float tx = inner.X + Measure(L.T("设置", "Settings"), boldFont) * s + 16 * s, th = 26 * s;
        for (int i = 0; i < SettingTabs.Length; i++)
        {
            float tw = Measure(SettingTabs[i], chipFont) * s + 24 * s;
            var b = new RectangleF(tx, inner.Y + 11 * s - th / 2, tw, th);
            bool on = settingsTab == i;
            if (on) FillRound(g, b, th / 2, Ink(.92f * a));
            else if (b.Contains(mouse)) FillRound(g, b, th / 2, Ink(.12f * a));
            DrawTight(g, SettingTabs[i], chipFont, on ? Deep(a) : Ink(.8f * a), b.X + 12 * s, b.Y + th / 2);
            settingHits.Add((b, "tab", i));
            tx += tw + 6 * s;
        }
        string hint = L.T("改了马上生效 · Esc 关闭", "Changes apply instantly · Esc to close");
        float hw = Measure(hint, smallFont) * s;
        if (inner.Right - hw > tx + 12 * s) DrawTight(g, hint, smallFont, Ink(.5f * a), inner.Right - hw, inner.Y + 12 * s);

        // 宽的时候分两列，窄的时候排成一列
        var columns = SettingColumns[Math.Clamp(settingsTab, 0, SettingColumns.Length - 1)];
        float top = inner.Y + 40 * s;
        bool two = inner.Width > 560 * s;
        float gap = 36 * s, colW = two ? (inner.Width - gap) / 2 : inner.Width;
        int perColumn = two ? columns.Max(col => col.Length) : columns.Sum(col => col.Length);
        float rh = Math.Min(46 * s, (inner.Bottom - top) / perColumn);
        float y = top;
        for (int col = 0; col < columns.Length; col++)
        {
            float x = two ? inner.X + col * (colW + gap) : inner.X;
            if (two) y = top;
            foreach (var key in columns[col])
            {
                DrawSettingRow(g, key, new RectangleF(x, y, colW, rh), mouse, a);
                y += rh;
            }
        }
    }

    void DrawSettingRow(Graphics g, string key, RectangleF row, Point mouse, float a)
    {
        float s = S, cy = row.Y + row.Height / 2;
        string label = key switch
        {
            "clarity" => L.T($"玻璃透明度  {app.Config.GlassClarity}%", $"Glass clarity  {app.Config.GlassClarity}%"),
            "width" => L.T($"展开宽度  {app.Config.OpenWidth}", $"Open width  {app.Config.OpenWidth}"),
            "height" => L.T($"展开最大高度  {app.Config.OpenHeight}", $"Max open height  {app.Config.OpenHeight}"),
            "topgap" => L.T($"离屏幕顶部  {app.Config.CapsuleTopGap}", $"Gap from top  {app.Config.CapsuleTopGap}"),
            "zoom" => L.T("悬停放大", "Zoom on hover"),
            "hoveropen" => L.T("鼠标停上去展开", "Open on hover"),
            "closespeed" => L.T("鼠标离开后收起", "Close when mouse leaves"),
            "song" => L.T("跟随歌曲变色", "Tint to match the song"),
            "nowplaying" => L.T("胶囊上显示正在播放", "Show now playing on capsule"),
            "musiccard" => L.T("悬停时打开音乐卡片", "Open music card on hover"),
            "motion" => L.T("封面旋转、音柱跟着声音跳", "Spinning cover, bars follow sound"),
            "lyrics" => L.T("同步歌词（联网查 lrclib.net）", "Synced lyrics (online, lrclib.net)"),
            "pilllyrics" => L.T("收起时胶囊上滚动歌词", "Lyrics on the closed capsule"),
            "sky" => L.T("天色光：胶囊跟着早晚变色", "Sky glow: tint follows time of day"),
            "btbattery" => L.T("音乐卡片显示蓝牙耳机电量", "Bluetooth headphone battery on card"),
            "netease" => L.T("也从网易云找歌词（中文歌多、逐字亮）", "Also use NetEase (Chinese songs, word sync)"),
            "translate" => L.T("外文歌下面显示中文翻译（网易云）", "Chinese translation line (NetEase)"),
            "stillwatch" => L.T("“仍在观看吗？”自动继续（要扩展）", "Auto-continue “Still watching?” (ext.)"),
            "backdrop" => L.T("卡片背景用模糊流动的封面", "Blurred flowing cover as background"),
            "turntable" => L.T("封面换成唱片机（放歌时转）", "Turntable instead of cover"),
            "float" => L.T("卡片能往下拖出来放在桌面上", "Drag card down onto the desktop"),
            "ctrlmusic" => L.T("放歌时连按两下 Ctrl 锁住卡片", "Ctrl twice locks card while playing"),
            "output" => L.T("点输出设备切换音箱 / 耳机", "Click output device to switch"),
            "skipads" => L.T("YouTube 广告：胶囊上一点就跳过", "YouTube ads: click capsule to skip"),
            "autoskip" => L.T("能跳过的广告自动跳过", "Auto-skip skippable ads"),
            "adext" => L.T("装 Edge 扩展：后台标签页也能跳", "Edge add-on: background tabs too"),
            "charge" => L.T("插上充电器时的充电动画", "Charging animation when plugged in"),
            "shotfly" => L.T("截图像飞进胶囊一样", "Screenshots fly into the capsule"),
            "focus" => L.T("番茄钟（面板上多一个计时按钮）", "Focus timer (adds a timer button)"),
            "focuslen" => L.T("每次专注（分钟）", "Focus length (minutes)"),
            "ctrl" => L.T("连按两下 Ctrl 呼出", "Ctrl twice to summon"),
            "dblclick" => L.T("双击桌面空白处隐藏", "Double-click desktop to hide"),
            "autosort" => L.T("新文件自动按类型收纳", "Auto-file new files by type"),
            "shots" => L.T("「图像」页收所有截图", "“Images” page collects screenshots"),
            "trash" => L.T("拖到右下角丢进回收站", "Drag to bottom-right to recycle"),
            "sysicons" => L.T("隐藏系统桌面图标", "Hide system desktop icons"),
            "autohide" => L.T("顶部胶囊自动隐藏", "Auto-hide top capsule"),
            "tbstyle" => L.T("Windows 任务栏", "Windows taskbar"),
            "lang" => "语言 / Language",
            _ => L.T("开机自动启动", "Start with Windows"),
        };
        bool slider = key is "clarity" or "width" or "height" or "topgap";
        bool segments = key is "zoom" or "tbstyle" or "hoveropen" or "closespeed" or "focuslen" or "lang";
        var hit = RectangleF.Inflate(row, 6 * s, -3 * s);
        if (!slider && !segments && hit.Contains(mouse)) FillRound(g, hit, 10 * s, Ink(.06f * a));
        DrawTight(g, label, labelFont, Ink(.92f * a), row.X, cy);

        if (slider)
        {
            float w = Math.Min(170 * s, row.Width * .42f);
            var track = new RectangleF(row.Right - w - 8 * s, cy - 2 * s, w, 4 * s);
            sliderTracks[key] = track;
            var (min, max, _) = SliderRange(key);
            float value = Math.Clamp((SliderValue(key) - min) / (float)(max - min), 0, 1);
            FillRound(g, track, 2 * s, Ink(.18f * a));
            FillRound(g, new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * value), track.Height), 2 * s, Ink(.85f * a));
            float kx = track.X + track.Width * value;
            bool hot = sliderDrag == key || RectangleF.Inflate(track, 8 * s, 12 * s).Contains(mouse);
            float kr = (hot ? 8 : 7) * s;
            using (var shadow = new SolidBrush(Color.FromArgb(Byte(.25f * a), 0, 0, 0))) g.FillEllipse(shadow, kx - kr, cy - kr + 1.5f * s, kr * 2, kr * 2);
            using (var knob = new SolidBrush(Color.FromArgb(Byte(a), 255, 255, 255))) g.FillEllipse(knob, kx - kr, cy - kr, kr * 2, kr * 2);
            settingHits.Add((RectangleF.Inflate(track, 10 * s, 14 * s), key, 0));
        }
        else if (segments)
        {
            string[] names = SegmentNames(key);
            int current = SegmentValue(key);
            float bw = names.Max(n => Measure(n, chipFont)) * s + 22 * s, bh = 26 * s, bx = row.Right - bw * names.Length - 4 * s;
            FillRound(g, new RectangleF(bx - 2 * s, cy - bh / 2 - 2 * s, bw * names.Length + 4 * s, bh + 4 * s), (bh + 4 * s) / 2, Ink(.10f * a));
            for (int i = 0; i < names.Length; i++)
            {
                var b = new RectangleF(bx + i * bw, cy - bh / 2, bw, bh);
                bool on = current == i;
                if (on) FillRound(g, b, bh / 2, Ink(.92f * a));
                else if (b.Contains(mouse)) FillRound(g, b, bh / 2, Ink(.12f * a));
                float tw = Measure(names[i], chipFont) * s;
                DrawTight(g, names[i], chipFont, on ? Deep(a) : Ink(.8f * a), b.X + (bw - tw) / 2, cy);
                settingHits.Add((b, key, i));
            }
        }
        else if (key == "adext")
        {
            // 不是开关：一个小按钮，点了打开 Edge 的扩展页；扩展来过就显示“已连上”
            bool outdated = adBridge?.Outdated == true, linked = adBridge?.Seen == true && !outdated;
            string text = outdated ? L.T("要重新加载…", "Reload needed…") : linked ? L.T("已连上 ✓", "Connected ✓") : L.T("怎么装…", "How to install…");
            float bw = Measure(text, chipFont) * s + 24 * s, bh = 26 * s;
            var b = new RectangleF(row.Right - bw - 4 * s, cy - bh / 2, bw, bh);
            FillRound(g, b, bh / 2, linked ? Ink(.10f * a) : hit.Contains(mouse) ? Ink(.9f * a) : Ink(.18f * a));
            DrawTight(g, text, chipFont, !linked && hit.Contains(mouse) ? Deep(a) : Ink(.85f * a), b.X + 12 * s, cy);
            settingHits.Add((hit, key, 0));
        }
        else
        {
            bool on = SettingOn(key);
            var sw = new RectangleF(row.Right - 40 * s - 4 * s, cy - 11 * s, 40 * s, 22 * s);
            FillRound(g, sw, 11 * s, on ? Ink(.9f * a) : Ink(.18f * a));
            float kx = on ? sw.Right - 11 * s : sw.X + 11 * s;
            using (var knob = new SolidBrush(on ? Deep(a) : Ink(.9f * a))) g.FillEllipse(knob, kx - 8 * s, cy - 8 * s, 16 * s, 16 * s);
            settingHits.Add((hit, key, 0));
        }
    }

    /// <summary>设置卡片开着时的点击；返回 true 表示这一下已经处理了。</summary>
    bool SettingsMouseDown(Point p)
    {
        if (gearRect.Contains(p))
        {
            if (settingsOpen) CloseSettings();
            else OpenSettings();
            return true;
        }
        if (!settingsOpen) return false;
        foreach (var (rect, key, value) in settingHits)
        {
            if (!rect.Contains(p)) continue;
            if (sliderTracks.ContainsKey(key))
            {
                sliderDrag = key;
                SlideTo(p.X);
            }
            else ClickSetting(key, value);
            return true;
        }
        if (!settingsCard.Contains(p)) CloseSettings(); // 点卡片外面：关掉设置
        return true;
    }

    static string ReadHead(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[6000];
        int n = stream.Read(buffer, 0, buffer.Length);
        // 有 BOM 按 BOM，否则先当 UTF-8，乱码多就当 GBK（国内很多老文本是 GBK）
        string text = System.Text.Encoding.UTF8.GetString(buffer, 0, n);
        if (text.Count(ch => ch == '�') > 3)
        {
            try { text = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(936)?.GetString(buffer, 0, n) ?? text; }
            catch (Exception) { }
        }
        var lines = text.Replace("\r", "").Split('\n').Take(24).Select(l => l.Length > 120 ? l[..120] : l);
        return string.Join("\n", lines).TrimEnd('�');
    }

    static string Describe(DesktopItem item)
    {
        var lines = new List<string>();
        try
        {
            if (item.IsDir) lines.Add(L.T("文件夹", "Folder"));
            else
            {
                long size = new FileInfo(item.Path).Length;
                string kind = item.Ext.Length > 0 ? item.Ext.TrimStart('.').ToUpperInvariant() + L.T(" 文件", " file") : L.T("文件", "File");
                if (item.HidesExtension) kind = L.T("快捷方式", "Shortcut");
                lines.Add($"{kind} · {FormatSize(size)}");
            }
        }
        catch (Exception) { lines.Add(item.IsDir ? L.T("文件夹", "Folder") : L.T("文件", "File")); }
        lines.Add(L.T($"修改于 {item.Modified:yyyy-MM-dd HH:mm}", $"Modified {item.Modified:yyyy-MM-dd HH:mm}"));
        lines.Add(L.T($"放进来 {item.Created:yyyy-MM-dd HH:mm}", $"Added {item.Created:yyyy-MM-dd HH:mm}"));
        lines.Add(Path.GetDirectoryName(item.Path) ?? "");
        return string.Join("\n", lines);
    }

    static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    void DrawPreview(Graphics g, RectangleF c, float a)
    {
        float s = S;
        var item = previewItem!;
        float t = Math.Clamp((Now - previewSince) / 200f, 0, 1);
        float ease = 1 - (1 - t) * (1 - t) * (1 - t);
        a *= ease;
        var area = new RectangleF(c.X + 12 * s, c.Y + 62 * s, c.Width - 24 * s, c.Height - 62 * s - 32 * s);
        area.Inflate(-(1 - ease) * 14 * s, -(1 - ease) * 10 * s);

        // 卡片：比面板更深一层的玻璃
        FillRound(g, area, 18 * s, Color.FromArgb(Byte(.82f * a), Mix(app.Tone.Tint, Color.Black, app.Tone.DarkInk ? .15f : .62f)));
        using (var edge = new Pen(Ink(.14f * a), 1 * s))
        using (var shape = Shapes.RoundRect(area, 18 * s))
            g.DrawPath(edge, shape);

        var inner = RectangleF.Inflate(area, -18 * s, -16 * s);
        float infoW = Math.Min(250 * s, inner.Width * .38f);
        var view = new RectangleF(inner.X, inner.Y, inner.Width - infoW - 18 * s, inner.Height);
        var info = new RectangleF(view.Right + 18 * s, inner.Y, infoW, inner.Height);

        // 左边：大图 / 文本
        if (previewText != null && previewImage == null)
        {
            FillRound(g, view, 12 * s, Ink(.06f * a));
            using var brush = new SolidBrush(Ink(.85f * a));
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(previewText.Length > 0 ? previewText : L.T("（空文件）", "(empty file)"), smallFont, brush, RectangleF.Inflate(view, -12 * s, -10 * s), format);
        }
        else if (previewImage != null)
        {
            var box = previewText != null ? new RectangleF(view.X, view.Y, view.Width * .45f, view.Height) : view;
            var dest = Shapes.Fit(previewImage.Size, Rectangle.Round(box));
            if (dest.Width > previewImage.Width && dest.Height > previewImage.Height) // 小图标别放大成一团糊
                dest = new Rectangle(dest.X + (dest.Width - previewImage.Width) / 2, dest.Y + (dest.Height - previewImage.Height) / 2, previewImage.Width, previewImage.Height);
            var state = g.Save();
            using (var shape = Shapes.RoundRect(dest, 10 * s)) g.SetClip(shape, CombineMode.Intersect);
            if (a >= .99f) g.DrawImage(previewImage, dest);
            else
            {
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = a });
                g.DrawImage(previewImage, dest, 0, 0, previewImage.Width, previewImage.Height, GraphicsUnit.Pixel, attributes);
            }
            g.Restore(state);
            if (previewText != null) // 文件夹：右半边列出里面的东西
            {
                using var brush = new SolidBrush(Ink(.78f * a));
                g.DrawString(previewText.Length > 0 ? previewText : L.T("（空文件夹）", "(empty folder)"), smallFont, brush, new RectangleF(box.Right + 12 * s, view.Y + 4 * s, view.Width - box.Width - 12 * s, view.Height - 8 * s));
            }
        }
        else
        {
            using var brush = new SolidBrush(Ink(.5f * a));
            g.DrawString(L.T("正在加载预览…", "Loading preview…"), labelFont, brush, view, CenterFormat);
        }

        // 右边：名字、信息、按键
        using (var brush = new SolidBrush(Ink(a)))
        using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter })
        {
            var nameBox = new RectangleF(info.X, info.Y + 2 * s, info.Width, boldFont.GetHeight(g) * 3);
            g.DrawString(item.Name, boldFont, brush, nameBox, format);
            var used = g.MeasureString(item.Name, boldFont, (int)info.Width, format);
            using var dim = new SolidBrush(Ink(.62f * a));
            g.DrawString(previewInfo, smallFont, dim, new RectangleF(info.X, nameBox.Y + Math.Min(used.Height, nameBox.Height) + 10 * s, info.Width, info.Height * .6f), format);
        }
        DrawTight(g, L.T("Enter 打开 · 空格关闭 · ←→ 换一个", "Enter open · Space close · ←→ next"), smallFont, Ink(.55f * a), info.X, info.Bottom - 8 * s);
    }

    // ---------- 命中测试 ----------

    bool InPanel(Point p) => mode == Mode.Open && CapsuleRect.Contains(p);

    int TileAt(Point p)
    {
        if (!InPanel(p) || settingsOpen) return -1; // 设置卡片盖在图标上面
        LayoutPanel(CapsuleRect);
        if (!gridRect.Contains(p)) return -1;
        float s = S;
        int col = (int)Math.Floor((p.X - gridLeft) / (CellW * s));
        if (col < 0 || col >= columns) return -1;
        int row = (int)Math.Floor((p.Y - gridRect.Y + scroll * s) / (CellH * s));
        int i = row * columns + col;
        if (i < 0 || i >= shown.Count) return -1;
        var r = TileRect(i);
        r.Inflate(-2 * s, -2 * s);
        return r.Contains(p) ? i : -1;
    }

    int ChipAt(Point p)
    {
        if (!InPanel(p) || settingsOpen) return -1;
        LayoutPanel(CapsuleRect);
        if (!chipRow.Contains(p)) return -1; // 滚到排外面的半个标签不算
        for (int i = 0; i < chipRects.Count; i++)
            if (chipRects[i].Contains(p)) return i;
        return -1;
    }

    bool SearchAt(Point p)
    {
        if (!InPanel(p)) return false;
        LayoutPanel(CapsuleRect);
        return searchRect.Contains(p);
    }

    // ---------- 鼠标 ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (detaching)
        {
            // 正拖着刚拎出来的小播放器
            if (e.Button == MouseButtons.Left && floatWin != null) floatWin.MoveCard(new Point(Cursor.Position.X - detachGrab.X, Cursor.Position.Y - detachGrab.Y));
            else EndDetach();
            return;
        }
        cursor = Cursor.Position;
        cursorOverDesktop = true;
        if (sliderDrag != null && e.Button == MouseButtons.Left)
        {
            SlideTo(e.X);
            return;
        }
        if (mode == Mode.Music)
        {
            if (e.Button == MouseButtons.Left && cardPressed && app.Config.MusicFloat && media != null && e.Y - cardPressAt.Y > 34 * S)
            {
                BeginDetach(e.Location);
                return;
            }
            if (e.Button == MouseButtons.Left && seekDrag) SeekMove(e.X);
            else if (e.Button == MouseButtons.Left && volumeDrag) VolumeMove(e.X);
            bool hot = seekDrag || volumeDrag;
            foreach (var hit in musicHits) hot |= hit.Rect.Contains(e.Location);
            Cursor = hot ? Cursors.Hand : Cursors.Default;
            Redraw();
            return;
        }
        if (chipPressed && mode == Mode.Open)
        {
            if (e.Button != MouseButtons.Left) EndChipPress(e.Location, click: false); // 松手的消息没收到
            else if (chipLift >= 0) MoveChipLift(e.X);
            else if (!chipScrolling && chipPress >= FixedPages && Math.Abs(e.Y - chipPressAt.Y) > 10 * S && Math.Abs(e.Y - chipPressAt.Y) > Math.Abs(e.X - chipPressAt.X))
                BeginChipLift(e.X); // 往下（或往上）一拉：直接拿起来，不用等
            // 按住等的那一下手抖几个像素不算滚，不然永远等不到拿起来
            else if (Math.Abs(e.X - chipPressAt.X) > (ChipHolding ? 10 * S : SystemInformation.DragSize.Width) || chipScrolling)
            {
                if (!chipScrolling && MaxChipScroll <= 0 && chipPress >= FixedPages) BeginChipLift(e.X); // 不用滚：直接拖就是调顺序
                else
                {
                    if (!chipScrolling) ChipLog($"scroll dx={e.X - chipPressAt.X} dy={e.Y - chipPressAt.Y} after={Now - chipPressSince}ms");
                    chipScrolling = true;
                    chipHold.Stop();
                    chipScroll = Math.Clamp(chipPressScroll - (e.X - chipPressAt.X) / S, 0, MaxChipScroll);
                    hoverChip = -1;
                    Cursor = Cursors.SizeWE;
                }
            }
            Redraw();
            return;
        }
        if (liftPath != null)
        {
            if (e.Button == MouseButtons.Left) MoveLift(e.Location);
            else EndLift(drop: false); // 按键已经松开了却没收到松开的消息
            return;
        }
        if (pressed >= 0 && e.Button == MouseButtons.Left)
        {
            var drag = SystemInformation.DragSize;
            if (Math.Abs(e.X - pressPoint.X) > drag.Width || Math.Abs(e.Y - pressPoint.Y) > drag.Height)
            {
                int i = pressed;
                pressed = -1;
                BeginLift(i, pressPoint, e.Location);
                return;
            }
        }
        if (mode == Mode.Open)
        {
            int h = TileAt(e.Location), hc = ChipAt(e.Location);
            bool hs = SearchAt(e.Location);
            if (h != hover || hc != hoverChip || hs != hoverSearch)
            {
                if (h >= 0) keyboardPicked = false; // 又用回鼠标了：放大跟着鼠标走
                hover = h;
                hoverChip = hc;
                hoverSearch = hs;
                Cursor = h >= 0 || hc >= 0 ? Cursors.Hand : hs ? Cursors.IBeam : Cursors.Default;
            }
            if (h < 0 && hc < 0 && !hs) Cursor = gearRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }
        else Cursor = CapsuleRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        Redraw();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        // 被别的窗口抢走了鼠标（弹窗、切换程序）：放回原位。
        // 晚一拍再看：正常松手时 WinForms 也会先放掉鼠标，那时松手的处理会先跑完
        if (liftPath != null && !Capture)
            BeginInvoke(new Action(() => { if (liftPath != null && !Capture) EndLift(drop: false); }));
        if (detaching && !Capture)
            BeginInvoke(new Action(() => { if (detaching && !Capture) EndDetach(); }));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = hoverChip = -1;
        hoverSearch = false;
        Redraw();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (mode == Mode.Music)
        {
            if (CapsuleRect.Contains(e.Location)) MusicMouseDown(e.Location);
            return;
        }
        if (mode != Mode.Open)
        {
            if (!CapsuleRect.Contains(e.Location)) return;
            if (mode == Mode.Notify && adNote)
            {
                if (adNoteChip) SkipAd(); // “广告可以跳过了 · 跳过”：点胶囊就跳
                return;
            }
            if (mode == Mode.Notify && noteUndo) UndoTrash(); // “已丢进回收站 · 撤销”：拿回来，再展开到它那里
            // 点胶囊：展开；点的是“已收进”的提示就直接跳到那个文件
            var fence = noteFence;
            var page = notePage;
            string? path = notePath;
            var target = mode == Mode.Idle ? HoverTarget : Mode.Open; // 在放歌时点胶囊：跟悬停一样打开音乐卡片
            Cancel();
            SetMode(target);
            if (target == Mode.Open && (fence != null || page != null) && path != null)
            {
                tab = Math.Max(0, page is { } kind ? IndexOfKind(kind) : IndexOfFence(fence));
                chipFollow = true;
                freshPath = path;
                freshSince = Now;
                RefreshShown(keepScroll: false);
                ScrollTo(shown.FindIndex(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
            }
            return;
        }
        if (!CapsuleRect.Contains(e.Location))
        {
            Collapse();
            return;
        }
        if (previewItem != null) // 预览开着：点哪里都是先关掉预览
        {
            ClosePreview();
            return;
        }
        if (!settingsOpen && focusRect.Contains(e.Location))
        {
            ToggleFocus(); // 开始专注 / 提前结束
            return;
        }
        if (SettingsMouseDown(e.Location)) return;
        if (Now < trashToastUntil && trashUndoRect.Contains(e.Location))
        {
            UndoTrash();
            return;
        }
        for (int b = 0; b < mediaButtons.Length; b++)
        {
            if (media == null || !mediaButtons[b].Contains(e.Location)) continue;
            app.Media?.Send(b switch { 0 => MediaWatcher.Command.Previous, 1 => MediaWatcher.Command.TogglePlay, _ => MediaWatcher.Command.Next });
            if (b == 1) media = media with { Playing = !media.Playing, Position = media.CurrentPosition, UpdatedUtc = DateTime.UtcNow }; // 先变图标，不等播放器回话
            Redraw();
            return;
        }
        if (media != null && !settingsOpen && playerInfo.Contains(e.Location))
        {
            // 卡片比文件面板矮：点完鼠标还在下面，要给时间移上去（移到卡片上就照常，离开才收）
            musicHoldUntil = Now + 5000;
            SetMode(Mode.Music);
            return;
        }
        LayoutPanel(CapsuleRect);
        ChipLog($"down at={e.Location} row={chipRow} inPanel={InPanel(e.Location)} settings={settingsOpen} chip={ChipAt(e.Location)}");
        if (InPanel(e.Location) && !settingsOpen && chipRow.Contains(e.Location))
        {
            // 先不切换：松手时没拖过才算点击
            chipPressed = true;
            chipScrolling = false;
            chipPress = ChipAt(e.Location); // -1 = 按在空白处，只能滚
            chipPressAt = e.Location;
            chipPressScroll = chipScroll;
            chipPressSince = Now;
            chipHold.Stop();
            if (chipPress >= FixedPages)
            {
                chipHold.Start();
                Kick(); // 按住的那个慢慢亮起来，亮满就拿起来了
            }
            return;
        }
        pressed = TileAt(e.Location);
        pressPoint = e.Location;
        if (pressed >= 0) selected = pressed;
        Redraw();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var screen = PointToScreen(e.Location);
        if (detaching && e.Button == MouseButtons.Left)
        {
            EndDetach();
            return;
        }
        if (e.Button == MouseButtons.Left) cardPressed = false;
        if (e.Button == MouseButtons.Left)
        {
            if (seekDrag)
            {
                seekDrag = false;
                if (media is { Duration.Ticks: > 0 } np)
                {
                    var to = TimeSpan.FromSeconds(seekValue * np.Duration.TotalSeconds);
                    app.Media?.Seek(to);
                    media = np with { Position = to, UpdatedUtc = DateTime.UtcNow }; // 先跳过去，不等播放器回话
                }
                Redraw();
                return;
            }
            if (volumeDrag)
            {
                volumeDrag = false;
                volumeRead = Now;
                return;
            }
            if (chipPressed)
            {
                EndChipPress(e.Location, click: true);
                return;
            }
            if (sliderDrag != null) // 拖完透明度：存一次
            {
                sliderDrag = null;
                app.ScheduleSave();
                Redraw();
                return;
            }
            if (liftPath != null)
            {
                EndLift(drop: true);
                return;
            }
            int i = pressed;
            pressed = -1;
            if (i >= 0 && i == TileAt(e.Location)) OpenItem(shown[i]);
            return;
        }
        if (e.Button != MouseButtons.Right) return;

        if (mode == Mode.Music && CapsuleRect.Contains(e.Location))
        {
            // 音乐卡片上右键：锁住、放到桌面、换歌词、调歌词时间、换输出设备
            menuOpen = true;
            try { ShowMusicMenu(screen); }
            finally { menuOpen = false; }
            if (mode == Mode.Music && Distance(Cursor.Position, capsuleOnScreen) > 46 * S) Schedule(Pending.Close, CloseMs);
            return;
        }
        menuOpen = true;
        try
        {
            int tile = TileAt(e.Location);
            if (tile >= 0)
            {
                var item = shown[tile];
                selected = tile;
                Redraw();
                app.ShowItemMenu(app.FenceOf(item.Path), item, screen);
            }
            else
            {
                int chip = ChipAt(e.Location);
                var target = chip >= 0 ? pages[chip].Fence : mode == Mode.Open ? CurrentFence : null;
                app.ShowCapsuleMenu(target, screen);
            }
        }
        finally { menuOpen = false; }
        if (mode == Mode.Open && Distance(Cursor.Position, capsuleOnScreen) > 46 * S && !focused) Schedule(Pending.Close, CloseMs);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (mode == Mode.Music) // 在音乐卡片上滚：调音量，封面上浮出一个大数字
        {
            if (volume < 0 || media == null) return;
            volume = Math.Clamp(volume + e.Delta / 120f * .05f, 0, 1);
            Audio.SetVolume(media.Source, volume, volumePerApp);
            volumeRead = Now;
            volumeShownSince = Now;
            Redraw();
            return;
        }
        if (mode != Mode.Open || previewItem != null || settingsOpen) return;
        LayoutPanel(CapsuleRect);
        if (chipRow.Contains(e.Location) && MaxChipScroll > 0) // 在分类那一排上滚：左右滚标签
        {
            chipScroll = Math.Clamp(chipScroll - e.Delta / 120f * 80, 0, MaxChipScroll);
            hoverChip = -1;
            Redraw();
            return;
        }
        float next = Math.Clamp(scrollTarget - e.Delta / 120f * CellH, 0, MaxScroll);
        if (next == scrollTarget) return;
        scrollTarget = next;
        hover = -1;
        Kick();
    }

    void ScrollTo(int index)
    {
        if (index < 0) return;
        LayoutPanel(CapsuleRect);
        int cols = Columns(Math.Min(780, Area.Width / S - 24));
        float top = index / cols * CellH, visible = (OpenHeight() - PanelChrome + 6);
        if (top < scrollTarget) scrollTarget = top;
        else if (top + CellH > scrollTarget + visible) scrollTarget = top + CellH - visible;
        scrollTarget = Math.Max(0, scrollTarget);
        Kick();
    }

    void OpenItem(DesktopItem item)
    {
        Collapse();
        app.Open(item);
    }

    // ---------- 键盘（展开后点进来才有） ----------

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (mode != Mode.Open || char.IsControl(e.KeyChar)) return;
        ClosePreview(); // 预览时直接打字：关掉预览去搜索
        CloseSettings();
        SetQuery(query + e.KeyChar);
        e.Handled = true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (mode != Mode.Open) return base.ProcessCmdKey(ref msg, keyData);
        if (liftPath != null && keyData == Keys.Escape) // 拖到一半按 Esc：放回原位
        {
            EndLift(drop: false);
            return true;
        }
        if (settingsOpen && keyData is Keys.Escape or Keys.Space)
        {
            CloseSettings();
            return true;
        }
        if (previewItem != null)
        {
            switch (keyData)
            {
                case Keys.Escape or Keys.Space:
                    ClosePreview();
                    return true;
                case Keys.Left or Keys.Right or Keys.Up or Keys.Down:
                    LayoutPanel(CapsuleRect);
                    int move = keyData switch { Keys.Left => -1, Keys.Right => 1, Keys.Up => -columns, _ => columns };
                    ShowPreview(Math.Clamp(selected + move, 0, shown.Count - 1));
                    return true;
            }
        }
        switch (keyData)
        {
            case Keys.Escape:
                if (query.Length > 0) SetQuery("");
                else Collapse();
                return true;
            case Keys.Space:
                // 空格预览：方向键选的那个；不然是鼠标指着的那个（上一次预览过的“选中”不算，不然永远是第一张）；
                // 正在打字搜索时空格还是空格
                int target = keyboardPicked && selected >= 0 ? selected
                    : query.Length > 0 ? -1
                    : hover >= 0 ? hover : selected;
                if (target < 0) break;
                ShowPreview(target);
                return true;
            case Keys.Back:
                if (query.Length > 0) SetQuery(query[..^1]);
                return true;
            case Keys.Delete when app.Config.TrashDrop:
                // 选中的（或者鼠标指着的）那个丢进回收站
                int victim = selected >= 0 ? selected : hover;
                if (victim >= 0 && victim < shown.Count)
                {
                    LayoutPanel(CapsuleRect);
                    ThrowAway(shown[victim], TileRect(victim));
                }
                return true;
            case Keys.Control | Keys.Back:
                SetQuery("");
                return true;
            case Keys.Control | Keys.V:
                var text = Clipboard.ContainsText() ? Clipboard.GetText().Split('\n')[0].Trim() : "";
                if (text.Length > 0) SetQuery(query + text);
                return true;
            case Keys.Enter:
                int i = selected >= 0 ? selected : shown.Count > 0 ? 0 : -1;
                if (i >= 0 && i < shown.Count) OpenItem(shown[i]);
                return true;
            case Keys.Tab:
                SetTab(tab + 1);
                return true;
            case Keys.Shift | Keys.Tab:
                SetTab(tab - 1);
                return true;
            case Keys.Left or Keys.Right or Keys.Up or Keys.Down:
                if (shown.Count == 0) return true;
                LayoutPanel(CapsuleRect);
                int step = keyData switch { Keys.Left => -1, Keys.Right => 1, Keys.Up => -columns, _ => columns };
                selected = Math.Clamp(selected < 0 ? 0 : selected + step, 0, shown.Count - 1);
                keyboardPicked = true;
                ScrollTo(selected);
                Redraw();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------- 丢进回收站：胶囊一口吞掉 ----------

    const int TrashFly = 280, TrashGulp = 460;

    /// <summary>
    /// 拿着图标时，底下右边变成一长条“丢进回收站”的区域（迷你播放器这时让开，不挡着）；
    /// 条子够宽，往右下角一甩就进，不用对准一个小圆。
    /// </summary>
    RectangleF TrashZone(RectangleF c)
    {
        float s = S, h = 32 * s;
        float w = Math.Max(Measure(L.T("拖到这里丢进回收站", "Drag here to recycle"), smallFont) * s + 64 * s, Math.Min(300 * s, c.Width * .42f));
        return new RectangleF(c.Right - 14 * s - w, c.Bottom - 22 * s - h / 2, w, h);
    }

    /// <summary>条子右头那个“口”的圆心。</summary>
    PointF TrashMouth(RectangleF c)
    {
        var zone = TrashZone(c);
        return new PointF(zone.Right - zone.Height / 2, zone.Y + zone.Height / 2);
    }

    bool TrashMouthHit(Point p) => RectangleF.Inflate(TrashZone(CapsuleRect), 12 * S, 8 * S).Contains(p);

    bool TrashAnimating => trashSince >= 0 && Now - trashSince < TrashFly + TrashGulp;

    bool TrashVisible => app.Config.TrashDrop && mode == Mode.Open && (liftPath != null || TrashAnimating);

    bool CanUndoTrash => trashItem != null && trashQueue.Contains(trashItem.Path);

    /// <summary>丢掉一个文件：图标转着飞进口里，胶囊“咽”一下。文件夹不在这里删。</summary>
    bool ThrowAway(DesktopItem item, RectangleF from)
    {
        if (!app.Config.TrashDrop) return false;
        if (Directory.Exists(item.Path))
        {
            trashToast = L.T("文件夹不在这里删，要删请去资源管理器", "Folders can't be deleted here, use File Explorer");
            trashToastUntil = Now + 2600;
            Redraw();
            return false;
        }
        trashing.Add(item.Path);
        trashQueue.Remove(item.Path);
        trashQueue.Add(item.Path);
        trashItem = item;
        trashFence = CurrentFence;
        trashPage = CurrentPage?.Kind;
        trashFrom = from;
        trashSince = Now;
        trashKicked = false;
        // OneDrive 里的文件删了云端也会跟着删（OneDrive 网页的回收站 30 天内还能找回），说一声
        trashToast = InOneDrive(item.Path) ? L.T("已丢进回收站（OneDrive 会同步删除）", "Moved to Recycle Bin (OneDrive deletes it too)") : L.T("已丢进回收站", "Moved to Recycle Bin");
        trashToastUntil = Now + trashTimer.Interval;
        trashTimer.Stop();
        trashTimer.Start();
        if (selected >= 0) selected = -1;
        hover = -1;
        RefreshShown(keepScroll: true);
        Kick();
        return true;
    }

    static bool InOneDrive(string path) =>
        new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Any(root => !string.IsNullOrEmpty(root) && path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    void UndoTrash()
    {
        if (!CanUndoTrash) return;
        string path = trashItem!.Path;
        trashQueue.Remove(path);
        trashing.Remove(path);
        if (trashQueue.Count == 0) trashTimer.Stop();
        trashToastUntil = 0;
        noteUndo = false;
        freshPath = path; // 回来的那个亮一下
        freshSince = Now;
        if (mode == Mode.Open) RefreshShown(keepScroll: true);
        Kick();
    }

    /// <summary>5 秒到了：真的放进回收站（后台做，回收站慢时不卡界面）。</summary>
    async void CommitTrash()
    {
        trashTimer.Stop();
        if (trashQueue.Count == 0) return;
        var paths = trashQueue.ToArray();
        trashQueue.Clear();
        trashToastUntil = 0;
        if (noteUndo)
        {
            noteUndo = false;
            if (mode == Mode.Notify) BuildPill();
        }
        Redraw();
        await Task.Run(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    if (!Recycle(path)) Log.Info($"没能丢进回收站：{path}");
                }
                catch (Exception ex) { Log.Error(ex); }
            }
        });
        await Task.Delay(3000); // 等文件监视那边重新扫完再放开，免得它在面板上闪回来一下
        if (IsDisposed) return;
        foreach (var path in paths) trashing.Remove(path); // 没删成的（还在）就会重新出现
        if (mode == Mode.Open) RefreshShown(keepScroll: true);
        Redraw();
    }

    /// <summary>右下角的口：拿着图标时张开，靠近了张大、盖子掀开，吞下去时鼓一下再荡开一圈。</summary>
    void DrawTrash(Graphics g, RectangleF c, float a)
    {
        float s = S;
        long now = Now;
        var m = TrashMouth(c);
        float appear = liftPath != null ? Math.Clamp((now - liftSince) / 160f, 0, 1) : 1;
        appear = 1 - (1 - appear) * (1 - appear);
        float k = trashSince >= 0 ? (now - trashSince - TrashFly) / (float)TrashGulp : -1;
        float gulp = k is >= 0 and <= 1 ? MathF.Sin(k * MathF.PI) * (1 - k) : 0;
        bool open = trashHot || trashSince >= 0 && k < 0; // 飞过来的时候也张着
        float r = (9 + 2 * appear + (trashHot ? 3 : 0) + 6 * gulp) * s;
        float fade = appear * a;

        // 底下那一长条：平时淡淡一层，拿到上面变成淡红（要删东西了）；吞完慢慢淡掉
        var zone = TrashZone(c);
        float zoneFade = liftPath != null ? fade : a * Math.Clamp(1 - Math.Max(0, k), 0, 1);
        FillRound(g, zone, zone.Height / 2, trashHot ? Color.FromArgb(Byte(.42f * zoneFade), 214, 64, 64) : Ink(.10f * zoneFade));
        using (var edge = new Pen(trashHot ? Color.FromArgb(Byte(.7f * zoneFade), 255, 140, 140) : Ink(.22f * zoneFade), 1 * s))
        using (var shape = Shapes.RoundRect(zone, zone.Height / 2))
            g.DrawPath(edge, shape);

        if (k is >= 0 and <= 1)
        {
            // 咽下去：一圈光从口上荡开
            float rr = r + 46 * s * (1 - (1 - k) * (1 - k));
            using var ring = new Pen(Ink(.38f * (1 - k) * a), (2.2f - 1.4f * k) * s);
            g.DrawEllipse(ring, m.X - rr, m.Y - rr, rr * 2, rr * 2);
        }

        using (var shadow = new SolidBrush(Color.FromArgb(Byte(.22f * fade), 0, 0, 0)))
            g.FillEllipse(shadow, m.X - r - 2 * s, m.Y - r - 1 * s, (r + 2 * s) * 2, (r + 2 * s) * 2);
        using (var hole = new SolidBrush(Color.FromArgb(Byte((trashHot ? .62f : .46f) * fade), 12, 10, 16)))
            g.FillEllipse(hole, m.X - r, m.Y - r, r * 2, r * 2);
        using (var rim = new Pen(Ink((trashHot ? .6f : .32f) * fade), 1 * s))
            g.DrawEllipse(rim, m.X - r, m.Y - r, r * 2, r * 2);

        // 小垃圾桶：桶身 + 盖子（张开时盖子往左上掀）
        using var pen = new Pen(Color.FromArgb(Byte(.88f * fade), 255, 255, 255), 1.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float bw = 7 * s, bh = 7.5f * s, top = m.Y - 1.5f * s;
        g.DrawLines(pen, new[] { new PointF(m.X - bw / 2, top), new PointF(m.X - bw / 2 + .8f * s, top + bh), new PointF(m.X + bw / 2 - .8f * s, top + bh), new PointF(m.X + bw / 2, top) });
        var state = g.Save();
        g.TranslateTransform(m.X - 5 * s, top - 2 * s);
        g.RotateTransform(open ? -28 : 0);
        g.DrawLine(pen, 0, 0, 10 * s, 0);
        g.DrawLine(pen, 3.5f * s, -1.8f * s, 6.5f * s, -1.8f * s);
        g.Restore(state);

        if (liftPath != null)
        {
            string label = trashHot ? L.T("松手，丢进回收站", "Release to recycle") : L.T("拖到这里丢进回收站", "Drag here to recycle");
            DrawTight(g, label, smallFont, trashHot ? Color.FromArgb(Byte(fade), 255, 255, 255) : Ink(.7f * fade), zone.X + 16 * s, m.Y);
        }

        // 飞进去的那个图标：越来越快、越转越小
        if (trashItem != null && trashSince >= 0 && now - trashSince < TrashFly && app.Icons.Get(trashItem.Path) is { } icon)
        {
            float t = (now - trashSince) / (float)TrashFly, e = t * t;
            var from = new PointF(trashFrom.X + trashFrom.Width / 2, trashFrom.Y + trashFrom.Height / 2);
            float x = from.X + (m.X - from.X) * e, y = from.Y + (m.Y - from.Y) * e - MathF.Sin(t * MathF.PI) * 24 * s;
            float size = trashFrom.Width * (1 - .82f * e);
            var spin = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(230 * e);
            DrawImage(g, icon, new RectangleF(-size / 2, -size / 2, size, size), a * (1 - .35f * e));
            g.Restore(spin);
        }
    }

    /// <summary>面板底下的一句提示：丢了什么、能不能撤销。</summary>
    void DrawTrashToast(Graphics g, RectangleF c, float a)
    {
        float s = S, fy = c.Bottom - 22 * s;
        bool undo = CanUndoTrash && trashToast.StartsWith(L.T("已丢进回收站", "Moved to Recycle Bin"));
        string name = undo ? $"「{Shorten(trashItem!.Name, 16)}」" : "";
        float x = c.X + 20 * s;
        DrawTight(g, trashToast + name, smallFont, Ink(.8f * a), x, fy);
        x += Measure(trashToast + name, smallFont) * s + 10 * s;
        if (undo)
        {
            float w = Measure(L.T("撤销", "Undo"), chipFont) * s + 20 * s;
            trashUndoRect = new RectangleF(x, fy - 12 * s, w, 24 * s);
            bool hot = trashUndoRect.Contains(PointToClient(Cursor.Position));
            FillRound(g, trashUndoRect, 12 * s, Ink((hot ? .3f : .18f) * a));
            DrawTight(g, L.T("撤销", "Undo"), chipFont, Ink(.95f * a), x + 10 * s, fy);
        }
        else trashUndoRect = RectangleF.Empty;
    }

    // ---------- 拖放 ----------

    void BeginLift(int i, Point press, Point at)
    {
        var r = TileRect(i);
        liftPath = shown[i].Path;
        liftGrab = new PointF(press.X - r.X, press.Y - r.Y); // 按下时点在图标的哪儿，拖起来就一直捏着那儿
        liftSince = Now;
        app.DraggingPath = liftPath;
        dragging = true;
        hover = -1;
        Cursor = Cursors.Default;
        MoveLift(at);
    }

    void MoveLift(Point p)
    {
        if (!CapsuleRect.Contains(p))
        {
            // 拖出胶囊：交给系统拖放，可以放进资源管理器、聊天窗口等别的程序
            int i = shown.FindIndex(item => item.Path.Equals(liftPath, StringComparison.OrdinalIgnoreCase));
            liftPath = null;
            liftSince = -1;
            dropChip = dropIndex = -1;
            if (i >= 0) StartDrag(i);
            else EndLift(drop: false);
            return;
        }
        LayoutPanel(CapsuleRect);
        dropChip = ChipDropTarget(p);
        trashHot = app.Config.TrashDrop && TrashMouthHit(p);
        bool inGrid = CanReorder && gridRect.Contains(p) && !trashHot;
        dropIndex = inGrid ? InsertIndex(p) : -1;
        if (inGrid) ScrollWhileDragging(p);
        Redraw();
    }

    void EndLift(bool drop)
    {
        var path = liftPath;
        if (path == null) return;
        int chip = dropChip, index = dropIndex;
        bool toTrash = drop && trashHot;
        int held = shown.FindIndex(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        liftPath = null;
        liftSince = -1;
        dropChip = dropIndex = -1;
        trashHot = false;
        app.DraggingPath = null;
        dragging = false;

        float s = S;
        var at = PointToClient(Cursor.Position);
        // 放在右下角的口上：从手里的位置飞进去
        if (toTrash && held >= 0 && ThrowAway(shown[held], new RectangleF(at.X - liftGrab.X, at.Y - liftGrab.Y, CellW * s, CellH * s))) return;

        // 从手里的位置滑进新格子（或者滑回原位）
        tilePos[path] = new PointF((at.X - liftGrab.X - gridLeft) / s, (at.Y - liftGrab.Y - gridRect.Y) / s + scroll);
        if (drop && chip >= 0 && pages[chip].Fence is { } fence) MoveToCategory(path, fence);
        else if (drop && index >= 0 && CurrentFence is { } page) app.MoveItem(path, page, index);
        Redraw();
    }

    /// <summary>
    /// 拖到别的分类标签上：放到那个分类的最前面（点过去马上看得到，还会亮一下），
    /// 底下提示一句移到了哪里——不然图标从这一页消失了，看起来像丢了。
    /// </summary>
    void MoveToCategory(string path, FenceConfig fence)
    {
        app.MoveItem(path, fence, 0);
        freshPath = path;
        freshSince = Now;
        trashToast = L.T($"已移到「{fence.Name}」", $"Moved to “{fence.Name}”");
        trashToastUntil = Now + 2400;
        Redraw();
    }

    /// <summary>手里拿着的那个图标：稍微放大、带阴影，压在所有东西上面。</summary>
    void DrawLifted(Graphics g, float a)
    {
        int i = shown.FindIndex(item => item.Path.Equals(liftPath, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        float s = S;
        var p = PointToClient(Cursor.Position); // 画的这一刻再读鼠标，跟手
        float t = Math.Clamp((Now - liftSince) / 160f, 0, 1);
        float ease = 1 - (1 - t) * (1 - t) * (1 - t);
        var r = new RectangleF(p.X - liftGrab.X, p.Y - liftGrab.Y, CellW * s, CellH * s);
        var card = RectangleF.Inflate(r, (-2 + 4 * ease) * s, (-2 + 4 * ease) * s);
        for (int k = 3; k >= 1; k--)
        {
            var shadow = RectangleF.Inflate(card, k * 2.5f * s, k * 2.5f * s);
            shadow.Offset(0, (4 + 3 * ease) * s);
            FillRound(g, shadow, (14 + k * 2.5f) * s, Color.FromArgb(Byte(.07f * ease * a), 0, 0, 0));
        }
        FillRound(g, card, 16 * s, Color.FromArgb(Byte(.8f * a), Mix(app.Tone.Tint, Color.Black, app.Tone.DarkInk ? .1f : .55f)));
        using (var edge = new Pen(Ink(.22f * a), 1 * s))
        using (var shape = Shapes.RoundRect(card, 16 * s))
            g.DrawPath(edge, shape);
        DrawTile(g, i, r, a, 1 + .1f * ease);
    }

    void StartDrag(int i)
    {
        var item = shown[i];
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { item.Path });
        using var image = DragImage(item);
        app.DraggingPath = item.Path;
        dragging = true;
        hover = -1;
        try
        {
            DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, image,
                new Point(image.Width / 2, (int)(28 * S)), false);
        }
        finally
        {
            app.DraggingPath = null;
            dragging = false;
            dropChip = -1;
            dropIndex = -1; // 没放进网格（拖到别的程序里了）：图标滑回原位
            Redraw();
        }
    }

    Bitmap DragImage(DesktopItem item)
    {
        float s = S;
        var bitmap = new Bitmap((int)(CellW * s), (int)(CellH * s), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var rect = new RectangleF(1, 1, bitmap.Width - 2, bitmap.Height - 2);
        FillRound(g, rect, 14 * s, Color.FromArgb(170, Mix(app.Tone.Tint, Color.Black, .55f)));
        int size = app.Icons.Size;
        if (app.Icons.Get(item.Path) is { } icon)
            g.DrawImage(icon, Shapes.Fit(icon.Size, new Rectangle((int)(rect.X + (rect.Width - size) / 2), (int)(10 * s), size, size)));
        using var brush = new SolidBrush(Color.White);
        g.DrawString(item.Name, labelFont, brush, new RectangleF(4 * s, 10 * s + size + 7 * s, rect.Width - 8 * s, labelFont.GetHeight(g) * 2 + 1), LabelFormat);
        return bitmap;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (app.DraggingPath == null && e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            dropFiles = files;
            if (mode != Mode.Open)
            {
                Cancel();
                SetMode(Mode.Receive);
            }
        }
        UpdateDrop(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        UpdateDrop(e);
    }

    void UpdateDrop(DragEventArgs e)
    {
        var at = PointToClient(new Point(e.X, e.Y));
        if (NearChipRow(at)) AutoScrollChips(.05f); // 系统拖放停着时大约每 50ms 来一次
        int chip = ChipAt(at);
        if (chip >= 0 && pages[chip].Fence == null) chip = -1; // “常用”“最近下载”是自动的，不能往里放
        if (chip != dropChip)
        {
            dropChip = chip;
            Redraw();
        }
        // 拖到网格里：别的图标滑开，空出要放的那一格
        bool inGrid = mode == Mode.Open && CanReorder && previewItem == null && !settingsOpen && InPanel(at) && gridRect.Contains(at)
                      && (app.DraggingPath != null || dropFiles.Length > 0);
        int index = inGrid ? InsertIndex(at) : -1;
        if (index != dropIndex)
        {
            dropIndex = index;
            Redraw();
        }
        if (inGrid) ScrollWhileDragging(at);

        if (app.DraggingPath != null)
        {
            // 面板里的图标拖到另一个分类上 = 换分类；在网格里拖 = 调整顺序
            bool toChip = chip >= 0 && (chip != tab || query.Length > 0);
            e.Effect = toChip || inGrid ? DragDropEffects.Move : DragDropEffects.None;
            e.DropImageType = toChip || inGrid ? DropImageType.Move : DropImageType.None;
            e.Message = toChip ? L.T("移到 %1", "Move to %1") : inGrid ? L.T("放在这里", "Place here") : "";
            e.MessageReplacementToken = toChip ? pages[chip].Name : "";
            return;
        }
        if (dropFiles.Length == 0)
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        // 只是收纳一个引用，不移动文件
        e.Effect = DragDropEffects.Link;
        e.DropImageType = DropImageType.Link;
        e.Message = L.T("收纳到 %1", "File into %1");
        e.MessageReplacementToken = chip >= 0 ? pages[chip].Name : inGrid ? CurrentPage!.Value.Name
            : dropFiles.Length == 1 ? app.CategoryNameFor(dropFiles[0]) : L.T("对应分类", "its category");
    }

    /// <summary>拖到网格最上面/最下面一条时自动滚动，可以拖到看不见的位置。</summary>
    void ScrollWhileDragging(Point at)
    {
        float edge = 28 * S, step = 0;
        if (at.Y < gridRect.Top + edge) step = -(gridRect.Top + edge - at.Y) / edge;
        else if (at.Y > gridRect.Bottom - edge) step = (at.Y - (gridRect.Bottom - edge)) / edge;
        if (step == 0) return;
        float next = Math.Clamp(scrollTarget + step * 18, 0, MaxScroll);
        if (next == scrollTarget) return;
        scrollTarget = next;
        Kick();
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        dropChip = -1;
        dropIndex = -1;
        if (mode == Mode.Receive && !CapsuleRect.Contains(PointToClient(Cursor.Position))) SetMode(Mode.Idle);
        Redraw();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        int chip = dropChip, index = dropIndex;
        dropChip = dropIndex = -1;
        var target = chip >= 0 ? pages[chip].Fence : null;
        var at = PointToClient(new Point(e.X, e.Y));
        if (app.DraggingPath is { } path)
        {
            if (target != null && (chip != tab || query.Length > 0)) MoveToCategory(path, target);
            else if (index >= 0 && CurrentFence is { } page)
            {
                // 放下的图标从松手的地方滑进那一格
                float s = S;
                tilePos[path] = new PointF((at.X - gridLeft) / s - CellW / 2, (at.Y - gridRect.Y) / s + scroll - 28);
                app.MoveItem(path, page, index);
            }
            return;
        }
        var files = dropFiles;
        dropFiles = Array.Empty<string>();
        if (index >= 0 && target == null && CurrentFence is { } here)
        {
            // 外面拖进来、放在网格里的某个位置：就收进这一页的这个位置
            app.AddPaths(files, here, index);
            ripple = Now;
            freshPath = files.Length > 0 ? files[0] : null;
            freshSince = Now;
            Kick();
            return;
        }
        var placed = app.Absorb(files, target);
        if (placed.Count == 0)
        {
            if (mode == Mode.Receive) SetMode(Mode.Idle);
            return;
        }
        var (item, fence) = placed[0];
        if (mode == Mode.Receive) mode = Mode.Idle; // 让 ShowFiled 走提示那条路
        ShowFiled(item, fence, placed.Count > 1 ? L.T($"收纳了 {placed.Count} 个文件", $"Filed {placed.Count} items into") : L.T("已收进", "Filed into"));
        ripple = Now;
        Kick();
    }

    // ---------- 窗口 ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AttachToDesktop();
        Draw();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) return;
        AttachToDesktop();
        Draw();
    }

    protected override void OnPaint(PaintEventArgs e) { } // 内容全靠 UpdateLayeredWindow

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = mode == Mode.Open ? MA_ACTIVATE : MA_NOACTIVATE;
                return;
            case WM_WINDOWPOSCHANGING:
                unsafe
                {
                    var pos = (WINDOWPOS*)m.LParam;
                    // 平时钉在桌面层最底下；Ctrl 呼出时（和收起动画播完之前）放开，让它浮在窗口上面
                    // 开了自动隐藏：一直在最上面
                    if ((pos->flags & SWP_NOZORDER) == 0 && Floating) pos->hwndInsertAfter = HWND_TOPMOST;
                    else if ((pos->flags & SWP_NOZORDER) == 0 && !summoned && !sinkPending) pos->hwndInsertAfter = HWND_BOTTOM;
                }
                break;
            // 胶囊里没有 WinForms 控件，激活和无障碍查询都不走 WinForms 那一套：
            // 它第一次处理激活要 28ms、建无障碍对象要 9ms，正好卡在展开的头几帧
            case WM_ACTIVATE:
                bool active = ((int)m.WParam & 0xFFFF) != 0;
                DefWndProc(ref m);
                OnActivated(active);
                return;
            case WM_GETOBJECT:
                DefWndProc(ref m);
                return;
            case WM_IME_STARTCOMPOSITION:
                PlaceImeWindow();
                break;
            case WM_FRAME:
                Interlocked.Exchange(ref framePending, 0);
                if (pacing) OnFrame();
                return;
            case WM_POWERBROADCAST when (int)m.WParam == PBT_APMPOWERSTATUSCHANGE:
                OnPowerChanged();
                break;
        }
        base.WndProc(ref m);
    }

    void OnActivated(bool active)
    {
        if (IsDisposed || Disposing) return; // 退出时窗口销毁前还会收到一次失去激活，字体已经释放了
        focused = active && mode == Mode.Open;
        caretOn = focused;
        if (focused) caret.Start();
        else caret.Stop();
        // 点到别处（别的窗口、桌面）就收起；弹右键菜单时菜单会抢走激活，不算
        if (!active && mode == Mode.Open && !menuOpen && !dragging) Collapse();
        Redraw();
    }

    /// <summary>中文输入法的候选窗跟着搜索框走。</summary>
    void PlaceImeWindow()
    {
        var context = ImmGetContext(Handle);
        if (context == IntPtr.Zero) return;
        LayoutPanel(CapsuleRect);
        var form = new COMPOSITIONFORM
        {
            dwStyle = CFS_POINT,
            ptCurrentPos = new POINT { X = (int)(searchRect.X + 36 * S), Y = (int)(searchRect.Bottom + 4 * S) },
        };
        ImmSetCompositionWindow(context, ref form);
        ImmReleaseContext(Handle, context);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        CreateFonts();
        BuildPages(); // 标签宽度跟字体走
        BuildPill();
        Redraw();
    }

    void CreateFonts()
    {
        foreach (var font in new[] { pillFont, boldFont, chipFont, smallFont, labelFont, searchFont, clockFont, titleFont, lyricFont, lyricNextFont, volumeFont }) font?.Dispose();
        fitCache.Clear();
        const string face = "Microsoft YaHei UI";
        pillFont = new Font(face, Px(13), FontStyle.Regular, GraphicsUnit.Pixel);
        boldFont = new Font(face, Px(13), FontStyle.Bold, GraphicsUnit.Pixel);
        chipFont = new Font(face, Px(13), FontStyle.Regular, GraphicsUnit.Pixel);
        smallFont = new Font(face, Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
        labelFont = new Font(face, Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
        searchFont = new Font(face, Px(14), FontStyle.Regular, GraphicsUnit.Pixel);
        clockFont = new Font(face, Px(20), FontStyle.Bold, GraphicsUnit.Pixel);
        titleFont = new Font(face, Px(17), FontStyle.Bold, GraphicsUnit.Pixel);
        lyricFont = new Font(face, Px(19), FontStyle.Bold, GraphicsUnit.Pixel);
        lyricNextFont = new Font(face, Px(14), FontStyle.Regular, GraphicsUnit.Pixel);
        volumeFont = new Font(face, Px(30), FontStyle.Bold, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 退出时还没到 5 秒的：照样丢进回收站（用户已经丢了）
            foreach (var path in trashQueue)
                try { Recycle(path); } catch (Exception ex) { Log.Error(ex); }
            trashQueue.Clear();
            foreach (var timer in new[] { frame, watch, later, caret, trim, chipHold, trashTimer, focusTick, mediaGap }) timer.Dispose();
            StopPacing();
            foreach (var font in new[] { pillFont, boldFont, chipFont, smallFont, labelFont, searchFont, clockFont, titleFont, lyricFont, lyricNextFont, volumeFont }) font.Dispose();
            lyricsCancel?.Cancel();
            ads?.Dispose();
            adBridge?.Dispose();
            floatWin?.Dispose();
            cardBackdrop.Clear();
            floatBackdrop.Clear();
            deckCache?.Dispose();
            labelFace?.Dispose();
            foreach (var path in lyricPaths.Values) path.Dispose();
            measure.Dispose();
            measureBitmap.Dispose();
            surface.Dispose();
            FreeGlass();
        }
        base.Dispose(disposing);
    }
}
