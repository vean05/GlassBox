using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlassBox;

/// <summary>一句里的一个字（或一个词）从什么时候唱到什么时候。只有网易云的逐字歌词才有。</summary>
internal sealed record LyricWord(TimeSpan Start, TimeSpan Length, string Text);

/// <param name="Translation">这句的中文翻译（网易云带的；没有是 null）。</param>
internal sealed record LyricLine(TimeSpan Time, string Text, LyricWord[]? Words = null, string? Translation = null);

/// <summary>
/// 一首歌的同步歌词；Instrumental 是纯音乐（没有词）。Source 是从哪里找到的，OffsetMs 是自己调过的提早/延后，
/// Choices &gt; 0 时表示这是手动“换一个版本”挑出来的第 Choice 个（一共 Choices 个）。
/// </summary>
/// <param name="Scale">时间拉长了几倍（歌是加速/降速版，或者自己调过“越唱越快/越慢”）。</param>
/// <param name="SourceSeconds">歌词那边记的这首歌有多长（不知道是 0）。</param>
internal sealed record SongLyrics(LyricLine[] Lines, bool Instrumental, string Source = "lrclib", int OffsetMs = 0, int Choice = 0, int Choices = 0,
    double Scale = 1, double SourceSeconds = 0)
{
    /// <summary>这个时间点在唱第几句（还没开始唱是 -1）。</summary>
    public int IndexAt(TimeSpan position)
    {
        int lo = 0, hi = Lines.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Lines[mid].Time <= position) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>整首往后挪 ms 毫秒（负数 = 提早）。</summary>
    public SongLyrics Shifted(int ms)
    {
        var d = TimeSpan.FromMilliseconds(ms);
        var lines = Lines.Select(l => l with { Time = l.Time + d, Words = l.Words?.Select(w => w with { Start = w.Start + d }).ToArray() }).ToArray();
        return this with { Lines = lines, OffsetMs = OffsetMs + ms };
    }

    /// <summary>整首的时间拉长 factor 倍（&gt;1 = 歌词放慢），自己调过的提早/延后不跟着拉。</summary>
    public SongLyrics Stretched(double factor)
    {
        var off = TimeSpan.FromMilliseconds(OffsetMs);
        TimeSpan At(TimeSpan t) => (t - off) * factor + off;
        var lines = Lines.Select(l => l with
        {
            Time = At(l.Time),
            Words = l.Words?.Select(w => w with { Start = At(w.Start), Length = w.Length * factor }).ToArray(),
        }).ToArray();
        return this with { Lines = lines, Scale = Scale * factor };
    }
}

/// <param name="Netease">也去网易云找（中文歌多，很多有逐字时间）。</param>
/// <param name="Choice">手动“换一个版本”：把所有找得到的都列出来，取第几个。</param>
/// <param name="Search">手动搜索：用这几个字去搜，不用歌名。</param>
/// <param name="Translate">要显示翻译：以前存的网易云歌词还不带翻译的，重新找一次。</param>
internal sealed record LyricsQuery(string Title, string Artist, TimeSpan Duration, bool Netease, int Choice = 0, string? Search = null, bool Translate = false);

/// <summary>
/// 找带时间轴的歌词：lrclib.net（免费、不用账号的公开歌词库），打开了的话先问网易云音乐（非官方接口，可能哪天就不能用了）。
/// 只发歌名、歌手（和 lrclib 的时长），找到的存到 %LOCALAPPDATA%\GlassBox\lyrics，同一首歌以后不再联网。设置里默认关着。
/// YouTube 的视频标题乱七八糟（“歌手 - 歌名 (Official Video) [4K]”、频道叫“xxx - Topic”），先洗一遍再搜。
/// </summary>
internal static class LyricsSource
{
    static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBox", "lyrics");
    static HttpClient? http, netease;

    static readonly Regex Brackets = new(@"\s*[\(\[【（「『〔].*?[\)\]】）」』〕]");
    static readonly Regex Noise = new(@"(?i)\b(official\s*(music\s*)?(video|audio|mv|visualizer|lyric\s*video)|lyrics?\s*video|music\s*video|visualizer|lyrics?|m/v|mv|hd|hq|4k|audio|sped\s*up|speed\s*up|slowed|reverb(ed)?|nightcore)\b|官方(完整)?版?|歌词版?|动态歌词|完整版|高音质|无损|[加降减慢]速版?|倍速");
    // 加速版、降速版：跟原曲一样的词，只是整首快了或慢了，歌词时间要跟着拉
    static readonly Regex SpeedVariant = new(@"(?i)sped\s*up|speed\s*up|slowed|nightcore|[加降减慢]速|倍速|\b[0-2]\.\d+\s*x\b|\bx\s*[0-2]\.\d+\b");

    public static bool IsSpeedVariant(string title) => SpeedVariant.IsMatch(title);
    static readonly Regex Feat = new(@"(?i)\s+(ft\.?|feat\.?|featuring)\s+.*$");
    static readonly Regex Channel = new(@"(?i)\s*-\s*topic$|vevo$|\s*official$|官方频道$|\s*music$");
    static readonly Regex Dash = new(@"\s+[-–—~]\s+");
    static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]");
    // 网易云逐字歌词：[行开始毫秒,行长](字开始,字长,0)字(字开始,字长,0)字…
    static readonly Regex YrcLine = new(@"^\[(\d+),(\d+)\](.*)$");
    static readonly Regex YrcWord = new(@"\((\d+),(\d+),-?\d+\)");

    static readonly Regex SquareBrackets = new(@"\s*[\[【「『〔].*?[\]】」』〕]");
    static readonly Regex Artists = new(@"\s*(?:/|,|，|&|、|\sx\s|\s×\s)\s*");

    /// <summary>
    /// 洗掉标题里的杂质，给几个候选歌名（按可能性排）：书名号里的；去掉所有括号的；留着圆括号的（库里常叫“潮汐(Natural)”）；
    /// 【】里的（“周杰伦【晴天】Official MV”）。YouTube 那种“歌手 - 歌名”拆开，歌手换成标题里写的。
    /// </summary>
    public static (List<string> Titles, string Artist) Candidates(string title, string artist)
    {
        string a = Channel.Replace(artist.Trim(), "").Trim();
        var titles = new List<string>();
        var quoted = Regex.Match(title, @"《(.+?)》");
        if (quoted.Success) titles.Add(Tidy(quoted.Groups[1].Value));
        string body = Regex.Replace(title, @"\s*[|｜].*$", "");
        var parts = Dash.Split(body, 2);
        if (!quoted.Success && parts.Length == 2)
        {
            string singer = Tidy(Brackets.Replace(parts[0], " "));
            if (singer.Length > 0 && parts[1].Trim().Length > 0)
            {
                a = singer;
                body = parts[1];
            }
        }
        titles.Add(Tidy(Brackets.Replace(body, " ")));
        titles.Add(Tidy(SquareBrackets.Replace(body, " ")));
        var inner = Regex.Match(title, @"[【\[「『](.+?)[】\]」』]");
        if (inner.Success) titles.Add(Tidy(inner.Groups[1].Value));
        // 中英双语的（“晴天 Sunny Day”）：库里多半只有中文那半
        for (int i = titles.Count - 1; i >= 0; i--)
            if (ChineseOnly(titles[i]) is { } zh) titles.Insert(i + 1, zh);
        a = Feat.Replace(a, "").Trim();
        titles = titles.Where(t => t.Length > 0 && !t.Equals(a, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (titles.Count == 0) titles.Add(title.Trim());
        return (titles, a);
    }

    /// <summary>又有汉字又有英文时只留汉字那部分；不是混着的返回 null。</summary>
    static string? ChineseOnly(string text)
    {
        if (!Regex.IsMatch(text, @"\p{IsCJKUnifiedIdeographs}") || !Regex.IsMatch(text, "[A-Za-z]")) return null;
        string zh = Tidy(Regex.Replace(text, @"[A-Za-z][A-Za-z0-9 .'&]*", " "));
        return zh.Length > 0 ? zh : null;
    }

    static string Tidy(string t)
    {
        t = Feat.Replace(Noise.Replace(t, " "), "");
        t = Regex.Replace(t, @"[""“”]|\(\s*\)|（\s*）", " ");
        return Regex.Replace(t, @"\s{2,}", " ").Trim(' ', '-', '–', '—', '·', ':', '：');
    }

    const int CacheMax = 800;

    /// <summary>歌词缓存最多留 800 首，多了删最早存的（每首几 KB，一共几 MB）。</summary>
    static void PruneCache()
    {
        var files = new DirectoryInfo(CacheDir).GetFiles("*.lrc");
        if (files.Length <= CacheMax) return;
        foreach (var old in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(CacheMax))
            try { old.Delete(); } catch (IOException) { }
    }

    static string CacheFile(string rawTitle, string rawArtist)
    {
        var (titles, artist) = Candidates(rawTitle, rawArtist);
        // 加速版、降速版单独存：在它上面调的快慢别影响原曲
        string variant = IsSpeedVariant(rawTitle) ? "\nspeed" : "";
        return Path.Combine(CacheDir, Hash(titles[0].ToLowerInvariant() + "\n" + artist.ToLowerInvariant() + variant) + ".lrc");
    }

    /// <summary>
    /// 找歌词：先查本地缓存，再联网。库里没有返回 null（记下来，7 天内不再问；后来打开了网易云的会再问一次）；
    /// 连不上、服务太忙抛 <see cref="LyricsUnavailableException"/>，这种不记，下次再试。
    /// 手动换版本、手动搜索不看缓存，挑中的那个存起来，以后就用它。
    /// </summary>
    public static async Task<SongLyrics?> FindAsync(LyricsQuery q, CancellationToken cancel)
    {
        var (titles, artist) = Candidates(q.Title, q.Artist);
        string file = CacheFile(q.Title, q.Artist);
        bool manual = q.Choice > 0 || q.Search != null;
        string? old = null; // 以前存的网易云歌词，存的时候还不带翻译：重新找一次，没找到带翻译的就还用它
        if (!manual)
        {
            try
            {
                if (File.Exists(file))
                {
                    string cached = await File.ReadAllTextAsync(file, cancel);
                    if (cached.StartsWith("#none"))
                    {
                        bool askedAll = !q.Netease || cached.Contains("netease");
                        if (askedAll && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(7)) return null;
                    }
                    else if (q.Translate && q.Netease && WithoutTranslation(cached)) old = cached;
                    else return FitSpeed(Parse(cached), q);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { }
        }

        // 加速/降速版跟原曲时长对不上：不按时长挑，找到后再按时长把歌词拉过去
        double seconds = IsSpeedVariant(q.Title) ? 0 : q.Duration.TotalSeconds;
        var (found, failed) = await Collect(titles, artist, seconds, q.Netease, q.Search, stopAtFirst: !manual, cancel);
        if (old != null)
        {
            if (found.Count > 0 && found[0].Contains("\n#tlyric\n"))
            {
                // 找到带翻译的：换上，自己调过的提早/延后、快慢留着
                string kept = string.Concat(Heads(old).Where(h => h.StartsWith("#offset ") || h.StartsWith("#scale ")).Select(h => h + "\n"));
                Save(file, kept + found[0]);
                return FitSpeed(Parse(kept + found[0]), q);
            }
            if (!failed) Save(file, "#notr\n" + old); // 这首网易云也没有翻译：记下来，以后不再问
            return FitSpeed(Parse(old), q);
        }
        if (found.Count == 0)
        {
            if (failed) throw new LyricsUnavailableException();
            if (!manual) Save(file, q.Netease ? "#none netease" : "#none");
            return null;
        }
        int pick = manual ? q.Choice % found.Count : 0;
        Save(file, found[pick]);
        var result = FitSpeed(Parse(found[pick]), q);
        return manual ? result with { Choice = pick, Choices = found.Count } : result;
    }

    /// <summary>
    /// 加速版/降速版（标题里写着 sped up、slowed、加速版……）：按两边的时长把歌词整首拉长或压短。
    /// 自己调过“越唱越快/越慢”的（缓存里有 #scale）就按自己调的，不再自动拉。
    /// </summary>
    static SongLyrics FitSpeed(SongLyrics lyrics, LyricsQuery q)
    {
        if (lyrics.Scale != 1 || lyrics.SourceSeconds <= 0 || q.Duration <= TimeSpan.Zero || !IsSpeedVariant(q.Title)) return lyrics;
        double factor = q.Duration.TotalSeconds / lyrics.SourceSeconds;
        return factor is > .55 and < 1.8 && Math.Abs(factor - 1) > .015 ? lyrics.Stretched(factor) : lyrics;
    }

    /// <summary>自己调过提早/延后：记在这首歌的缓存里，下次打开还是对的。</summary>
    public static void SaveOffset(string rawTitle, string rawArtist, int ms) =>
        SaveHeader(rawTitle, rawArtist, "#offset", ms != 0 ? ms.ToString() : null);

    /// <summary>自己调过“越唱越快/越慢”：整首拉长了几倍，也记在缓存里。</summary>
    public static void SaveScale(string rawTitle, string rawArtist, double scale) =>
        SaveHeader(rawTitle, rawArtist, "#scale", Math.Abs(scale - 1) > .001 ? scale.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : null);

    static void SaveHeader(string rawTitle, string rawArtist, string name, string? value)
    {
        string file = CacheFile(rawTitle, rawArtist);
        try
        {
            if (!File.Exists(file)) return;
            var lines = File.ReadAllText(file).Split('\n').Where(l => !l.StartsWith(name + " ")).ToList();
            if (value != null) lines.Insert(0, $"{name} {value}");
            File.WriteAllText(file, string.Join('\n', lines));
        }
        catch (Exception) { }
    }

    /// <summary>缓存开头的 # 说明行。</summary>
    static IEnumerable<string> Heads(string content) =>
        content.Split('\n').Select(l => l.TrimEnd('\r')).TakeWhile(l => l.StartsWith('#'));

    /// <summary>网易云的歌词、存的时候还没有翻译这回事（新存的要么带 #tlyric，要么标着 #notr）。</summary>
    static bool WithoutTranslation(string content)
    {
        var heads = Heads(content).ToList();
        return heads.Contains("#src 网易云") && !heads.Contains("#notr") && !content.Contains("\n#tlyric\n");
    }

    static void Save(string file, string content)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(file, content);
            PruneCache();
        }
        catch (Exception) { }
    }

    /// <summary>
    /// 按顺序问：网易云（打开了的话）→ lrclib 精确查（歌名 + 歌手 + 时长）→ lrclib 每个候选歌名 + 第一个歌手 → 只按歌名。
    /// stopAtFirst：找到一个就停（平时）；否则把能对上的都列出来（换版本用）。返回缓存格式的歌词，和有没有哪边连不上。
    /// </summary>
    static async Task<(List<string> Found, bool Failed)> Collect(List<string> titles, string artist, double seconds, bool useNetease, string? search, bool stopAtFirst, CancellationToken cancel)
    {
        var found = new List<string>();
        bool failed = false;
        string first = Artists.Split(artist)[0].Trim(); // “IN-K/安蘇羽/傅夢彤”：库里一般只记第一个
        first = ChineseOnly(first) ?? first;           // “周杰倫 Jay Chou” → “周杰倫”
        // 手动搜的：时长不管差多少都算，按差得少的排前面
        double loose = search != null ? 1e6 : 20, strict = search != null ? 1e6 : 3;
        void Add(string? content)
        {
            if (content != null && !found.Contains(content)) found.Add(content);
        }

        if (useNetease)
        {
            var queries = search != null ? new List<string> { search } : titles.Take(2).Select(t => first.Length > 0 ? t + " " + first : t).ToList();
            foreach (var query in queries)
            {
                var result = await Netease(query, search != null ? "" : first, search != null ? "" : titles[0], seconds, stopAtFirst ? 1 : 4, cancel);
                if (result == null) { failed = true; break; }
                foreach (var content in result) Add(content);
                if (stopAtFirst && found.Count > 0) return (found, false);
            }
        }

        var tries = new List<(string Path, double Gap)>();
        string E(string v) => Uri.EscapeDataString(v);
        if (search != null) tries.Add(($"search?q={E(search)}", loose));
        else
        {
            if (artist.Length > 0 && seconds > 0) tries.Add(($"get?track_name={E(titles[0])}&artist_name={E(artist)}&duration={Math.Round(seconds)}", 0));
            if (first.Length > 0)
                foreach (var t in titles) tries.Add(($"search?track_name={E(t)}&artist_name={E(first)}", loose));
            if (seconds > 0)
                foreach (var t in titles) tries.Add(($"search?track_name={E(t)}", strict));
        }
        var client = http ??= Client();
        foreach (var (path, gap) in tries.Take(8))
        {
            using var doc = await Get(client, path, cancel);
            if (doc == null)
            {
                failed = true;
                break; // 服务忙或者断网：后面的也不用问了
            }
            if (path.StartsWith("get?")) Add(Read(doc.RootElement));
            else foreach (var content in Pick(doc, seconds, gap, first)) Add(content);
            if (stopAtFirst && found.Count > 0) break;
        }
        return (found, failed && found.Count == 0);
    }

    static HttpClient Client()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://lrclib.net/api/"), Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GlassBox/0.1 (desktop organizer)"); // lrclib 希望带上程序名
        return client;
    }

    /// <summary>问一次。404（精确查没有这首）给一个空的回答；断网、超时、服务忙（5xx、429）返回 null。</summary>
    static async Task<JsonDocument?> Get(HttpClient client, string path, CancellationToken cancel)
    {
        try
        {
            using var response = await client.GetAsync(path, cancel);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return JsonDocument.Parse("{}");
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancel);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>搜到好几条：时长差 maxGap 秒以内、带时间轴的，按差得少排；歌手对不上的往后放。</summary>
    static IEnumerable<string> Pick(JsonDocument doc, double seconds, double maxGap, string artist)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;
        var ok = new List<(string Content, double Gap)>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var content = Read(item);
            if (content == null) continue;
            double length = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            // 不知道时长就照搜出来的顺序；知道的话差太多（MV 片头片尾一般不超过 20 秒）就不是这首
            double gap = seconds > 0 && length > 0 ? Math.Abs(length - seconds) : 0;
            if (seconds > 0 && length > 0 && gap > maxGap) continue;
            string name = item.TryGetProperty("artistName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            if (!SameArtist(name, artist)) gap += 5; // 歌手对不上：同名同长的才轮到它
            ok.Add((content, gap + ok.Count * .001));
        }
        foreach (var (content, _) in ok.OrderBy(o => o.Gap)) yield return content;
    }

    static bool SameArtist(string name, string artist) =>
        artist.Length == 0 || name.Length > 0 && (name.Contains(artist, StringComparison.OrdinalIgnoreCase) || artist.Contains(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>lrclib 的一条：带时间轴的歌词（缓存格式），纯音乐是 "#instrumental"，都没有就 null。</summary>
    static string? Read(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        bool instrumental = item.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
        string? synced = item.TryGetProperty("syncedLyrics", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        double length = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
        if (!string.IsNullOrWhiteSpace(synced) && Stamp.IsMatch(synced)) return "#src lrclib\n" + LengthHeader(length) + synced;
        return instrumental ? "#instrumental" : null;
    }

    // ---------- 网易云音乐 ----------

    static HttpClient NeteaseClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://music.163.com/api/"), Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return client;
    }

    /// <summary>
    /// 在网易云搜歌，挑歌名、歌手、时长对得上的（最多 max 首），取它们的歌词：有逐字的用逐字，没有用普通的。
    /// 连不上返回 null。翻唱、伴奏、钢琴版很多，所以歌手对不上时时长要差 3 秒以内，歌名也要对得上。
    /// </summary>
    static async Task<List<string>?> Netease(string query, string artist, string title, double seconds, int max, CancellationToken cancel)
    {
        var client = netease ??= NeteaseClient();
        using var doc = await Get(client, $"cloudsearch/pc?s={Uri.EscapeDataString(query)}&type=1&limit=12", cancel);
        if (doc == null) return null;
        if (!doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
            return new();
        var picks = new List<(long Id, double Score, double Length)>();
        string want = Plain(title);
        foreach (var song in songs.EnumerateArray())
        {
            if (!song.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
            string name = song.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            double length = song.TryGetProperty("dt", out var dt) && dt.ValueKind == JsonValueKind.Number ? dt.GetDouble() / 1000 : 0;
            bool artistOk = artist.Length == 0;
            if (song.TryGetProperty("ar", out var ar) && ar.ValueKind == JsonValueKind.Array)
                foreach (var a in ar.EnumerateArray())
                    if (a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String && SameArtist(an.GetString() ?? "", artist)) artistOk = true;
            string got = Plain(name);
            bool nameOk = want.Length == 0 || got.Length > 0 && (got == want || got.Contains(want) || want.Contains(got));
            if (!nameOk) continue;
            double gap = seconds > 0 && length > 0 ? Math.Abs(length - seconds) : 0;
            if (gap > (artistOk ? 20 : 3) && want.Length > 0) continue;
            picks.Add((id.GetInt64(), gap + (artistOk ? 0 : 5) + (got == want ? 0 : 2) + picks.Count * .001, length));
        }
        var found = new List<string>();
        foreach (var (id, _, length) in picks.OrderBy(p => p.Score).Take(max))
        {
            using var lyric = await Get(client, $"song/lyric/v1?id={id}&lv=-1&kv=-1&tv=-1&rv=-1&yv=-1&ytv=-1&yrv=-1", cancel);
            if (lyric == null) return found.Count > 0 ? found : null;
            var root = lyric.RootElement;
            if (root.TryGetProperty("pureMusic", out var pure) && pure.ValueKind == JsonValueKind.True || root.TryGetProperty("nolyric", out var none) && none.ValueKind == JsonValueKind.True)
            {
                if (seconds > 0) found.Add("#instrumental");
                continue;
            }
            string yrc = Field(root, "yrc"), lrc = Field(root, "lrc"), tr = Field(root, "tlyric");
            // 外文歌常带中文翻译：放在同一个文件的最后（#tlyric 后面），没有的标 #notr
            string extra = Stamp.IsMatch(tr) ? "\n#tlyric\n" + tr : "";
            string head = "#src 网易云\n" + (extra.Length == 0 ? "#notr\n" : "") + LengthHeader(length);
            if (yrc.Split('\n').Any(l => YrcLine.IsMatch(l.TrimEnd('\r')))) found.Add(head + "#yrc\n" + yrc.TrimEnd() + extra);
            else if (Stamp.IsMatch(lrc)) found.Add(head + lrc.TrimEnd() + extra);
        }
        return found;
    }

    /// <summary>歌词那边记的歌长（秒），存进缓存，加速版要按它拉歌词。</summary>
    static string LengthHeader(double seconds) =>
        seconds > 0 ? "#length " + seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "\n" : "";

    static string Field(JsonElement root, string name) =>
        root.TryGetProperty(name, out var part) && part.ValueKind == JsonValueKind.Object && part.TryGetProperty("lyric", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? "" : "";

    /// <summary>比较歌名用：去掉括号里的、空格和标点，转小写，繁简不管。</summary>
    static string Plain(string name) => Regex.Replace(Brackets.Replace(name, ""), @"[\s\p{P}]", "").ToLowerInvariant();

    // ---------- 读缓存格式 ----------

    /// <summary>
    /// 缓存格式：开头几行 # 是说明（#src 哪来的、#offset 调过多少毫秒、#yrc 逐字格式、#instrumental 纯音乐），后面是歌词本身。
    /// LRC：每行前面一个或几个 [分:秒.百分秒]。网易云的说明行（{"t":0,"c":[…]}，作词作曲）不是歌词，跳过。
    /// </summary>
    public static SongLyrics Parse(string content)
    {
        string source = "lrclib";
        int offset = 0;
        double scale = 1, length = 0;
        bool yrc = false;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var rows = content.Split('\n');
        int start = 0;
        for (; start < rows.Length && rows[start].StartsWith('#'); start++)
        {
            string head = rows[start].TrimEnd('\r');
            if (head.StartsWith("#instrumental")) return new SongLyrics(Array.Empty<LyricLine>(), true);
            if (head.StartsWith("#src ")) source = head[5..].Trim();
            else if (head.StartsWith("#offset ") && int.TryParse(head[8..].Trim(), out int ms)) offset = ms;
            else if (head == "#yrc") yrc = true;
            else if (head.StartsWith("#scale ") && double.TryParse(head[7..].Trim(), System.Globalization.NumberStyles.Float, invariant, out double k) && k is > .3 and < 3) scale = k;
            else if (head.StartsWith("#length ") && double.TryParse(head[8..].Trim(), System.Globalization.NumberStyles.Float, invariant, out double sec)) length = sec;
        }
        var lines = new List<LyricLine>();
        var translations = new List<(TimeSpan Time, string Text)>();
        bool inTranslation = false;
        for (int r = start; r < rows.Length; r++)
        {
            var line = rows[r].TrimEnd('\r');
            if (line == "#tlyric") { inTranslation = true; continue; }
            if (inTranslation)
            {
                var (at, said) = Stamped(line);
                foreach (var t in at) translations.Add((t, said));
                continue;
            }
            if (yrc)
            {
                if (ParseYrc(line) is { } parsed) lines.Add(parsed);
                continue;
            }
            var (times, text) = Stamped(line);
            foreach (var t in times) lines.Add(new LyricLine(t, text));
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        // 翻译按时间对到原句上（网易云两边的时间基本一样，逐字歌词的会差一点点）
        if (translations.Count > 0)
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Text.Length == 0) continue;
                var near = translations.Where(x => x.Text.Length > 0 && x.Text != "//" && Math.Abs((x.Time - lines[i].Time).TotalMilliseconds) < 1000)
                    .OrderBy(x => Math.Abs((x.Time - lines[i].Time).TotalMilliseconds)).Select(x => x.Text).FirstOrDefault();
                if (near != null && near != lines[i].Text) lines[i] = lines[i] with { Translation = near };
            }
        var result = new SongLyrics(lines.ToArray(), false, source, SourceSeconds: length);
        if (scale != 1) result = result.Stretched(scale);
        return offset != 0 ? result.Shifted(offset) : result;
    }

    /// <summary>LRC 的一行：行首连着的一个或几个时间，和后面的字。</summary>
    static (List<TimeSpan> Times, string Text) Stamped(string line)
    {
        int end = 0;
        var times = new List<TimeSpan>();
        foreach (Match m in Stamp.Matches(line))
        {
            if (m.Index != end) break; // 只认行首连着的时间
            end = m.Index + m.Length;
            double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), System.Globalization.CultureInfo.InvariantCulture);
            times.Add(TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value) * 60 + sec));
        }
        return (times, times.Count == 0 ? "" : line[end..].Trim());
    }

    static LyricLine? ParseYrc(string line)
    {
        var m = YrcLine.Match(line);
        if (!m.Success) return null;
        string body = m.Groups[3].Value;
        var words = new List<LyricWord>();
        var marks = YrcWord.Matches(body);
        for (int i = 0; i < marks.Count; i++)
        {
            int from = marks[i].Index + marks[i].Length, to = i + 1 < marks.Count ? marks[i + 1].Index : body.Length;
            string text = body[from..to];
            if (text.Length == 0) continue;
            words.Add(new LyricWord(TimeSpan.FromMilliseconds(long.Parse(marks[i].Groups[1].Value)), TimeSpan.FromMilliseconds(long.Parse(marks[i].Groups[2].Value)), text));
        }
        string whole = string.Concat(words.Select(w => w.Text));
        if (marks.Count == 0) whole = body;
        return new LyricLine(TimeSpan.FromMilliseconds(long.Parse(m.Groups[1].Value)), whole.Trim(), words.Count > 0 ? words.ToArray() : null);
    }

    static string Hash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
}

/// <summary>歌词服务这会儿连不上（断网、太忙）。</summary>
internal sealed class LyricsUnavailableException : Exception;
