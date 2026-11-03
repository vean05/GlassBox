using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlassBox;

internal sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>一首歌的同步歌词；Instrumental 是纯音乐（没有词）。</summary>
internal sealed record SongLyrics(LyricLine[] Lines, bool Instrumental)
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
}

/// <summary>
/// 从 lrclib.net（免费、不用账号的公开歌词库）找带时间轴的歌词。只发歌名、歌手和时长，
/// 找到的存到 %LOCALAPPDATA%\GlassBox\lyrics，同一首歌以后不再联网。设置里默认关着。
/// YouTube 的视频标题乱七八糟（“歌手 - 歌名 (Official Video) [4K]”、频道叫“xxx - Topic”），先洗一遍再搜。
/// </summary>
internal static class LyricsSource
{
    static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBox", "lyrics");
    static HttpClient? http;

    static readonly Regex Brackets = new(@"\s*[\(\[【（「『〔].*?[\)\]】）」』〕]");
    static readonly Regex Noise = new(@"(?i)\b(official\s*(music\s*)?(video|audio|mv|visualizer|lyric\s*video)|lyrics?\s*video|music\s*video|visualizer|lyrics?|m/v|mv|hd|hq|4k|audio)\b|官方(完整)?版?|歌词版?|动态歌词|完整版|高音质|无损");
    static readonly Regex Feat = new(@"(?i)\s+(ft\.?|feat\.?|featuring)\s+.*$");
    static readonly Regex Channel = new(@"(?i)\s*-\s*topic$|vevo$|\s*official$|官方频道$|\s*music$");
    static readonly Regex Dash = new(@"\s+[-–—~]\s+");
    static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]");

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

    /// <summary>
    /// 找歌词：先查本地缓存，再联网。库里没有返回 null（记下来，7 天内不再问）；
    /// 连不上、服务太忙抛 <see cref="LyricsUnavailableException"/>，这种不记，下次再试。
    /// </summary>
    const int CacheMax = 800;

    /// <summary>歌词缓存最多留 800 首，多了删最早存的（每首几 KB，一共几 MB）。</summary>
    static void PruneCache()
    {
        var files = new DirectoryInfo(CacheDir).GetFiles("*.lrc");
        if (files.Length <= CacheMax) return;
        foreach (var old in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(CacheMax))
            try { old.Delete(); } catch (IOException) { }
    }

    public static async Task<SongLyrics?> FindAsync(string rawTitle, string rawArtist, TimeSpan duration, CancellationToken cancel)
    {
        var (titles, artist) = Candidates(rawTitle, rawArtist);
        string file = Path.Combine(CacheDir, Hash(titles[0].ToLowerInvariant() + "\n" + artist.ToLowerInvariant()) + ".lrc");
        try
        {
            if (File.Exists(file))
            {
                string cached = await File.ReadAllTextAsync(file, cancel);
                if (cached.StartsWith("#none"))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(7)) return null;
                }
                else return cached.StartsWith("#instrumental") ? new SongLyrics(Array.Empty<LyricLine>(), true) : Parse(cached);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { }

        double seconds = duration.TotalSeconds;
        string first = Artists.Split(artist)[0].Trim(); // “IN-K/安蘇羽/傅夢彤”：库里一般只记第一个
        first = ChineseOnly(first) ?? first;           // “周杰倫 Jay Chou” → “周杰倫”
        // 按顺序试，找到就停，最多问 8 次：精确查（歌名 + 歌手 + 时长差 2 秒内）→ 每个候选歌名 + 第一个歌手
        // → 只按歌名（时长要差 3 秒内才算）
        var tries = new List<(string Path, bool Strict)>();
        string E(string v) => Uri.EscapeDataString(v);
        if (artist.Length > 0 && seconds > 0) tries.Add(($"get?track_name={E(titles[0])}&artist_name={E(artist)}&duration={Math.Round(seconds)}", false));
        if (first.Length > 0)
            foreach (var t in titles) tries.Add(($"search?track_name={E(t)}&artist_name={E(first)}", false));
        if (seconds > 0)
            foreach (var t in titles) tries.Add(($"search?track_name={E(t)}", true));

        string? lrc = null;
        bool instrumental = false, failed = false;
        var client = http ??= Client();
        foreach (var (path, strict) in tries.Take(8))
        {
            using var doc = await Get(client, path, cancel);
            if (doc == null)
            {
                failed = true;
                break; // 服务忙或者断网：后面的也不用问了
            }
            if (path.StartsWith("get?")) (lrc, instrumental) = Read(doc.RootElement);
            else (lrc, instrumental) = Pick(doc, seconds, strict ? 3 : 20, first);
            if (lrc != null || instrumental) break;
        }
        if (failed && lrc == null && !instrumental) throw new LyricsUnavailableException();

        SongLyrics? result = instrumental ? new SongLyrics(Array.Empty<LyricLine>(), true) : lrc != null ? Parse(lrc) : null;
        if (result is { Lines.Length: 0, Instrumental: false }) result = null;
        try
        {
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllTextAsync(file, instrumental ? "#instrumental" : result != null ? lrc! : "#none", CancellationToken.None);
            PruneCache();
        }
        catch (Exception) { }
        return result;
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

    /// <summary>搜到好几条：时长差 maxGap 秒以内、带时间轴的里面挑最接近的；歌手对得上的优先。</summary>
    static (string? Lrc, bool Instrumental) Pick(JsonDocument doc, double seconds, double maxGap, string artist)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return (null, false);
        (string? Lrc, bool Instrumental) best = (null, false);
        double bestGap = double.MaxValue;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var found = Read(item);
            if (found.Lrc == null && !found.Instrumental) continue;
            double length = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            // 不知道时长就取第一条；知道的话差太多（MV 片头片尾一般不超过 20 秒）就不是这首
            double gap = seconds > 0 && length > 0 ? Math.Abs(length - seconds) : 0;
            if (seconds > 0 && length > 0 && gap > maxGap) continue;
            string name = item.TryGetProperty("artistName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            if (artist.Length > 0 && !name.Contains(artist, StringComparison.OrdinalIgnoreCase) && !artist.Contains(name, StringComparison.OrdinalIgnoreCase))
                gap += 5; // 歌手对不上：同名同长的才轮到它
            if (gap < bestGap)
            {
                bestGap = gap;
                best = found;
            }
        }
        return best;
    }

    static (string? Lrc, bool Instrumental) Read(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return (null, false);
        bool instrumental = item.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
        string? synced = item.TryGetProperty("syncedLyrics", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        return (string.IsNullOrWhiteSpace(synced) ? null : synced, instrumental && string.IsNullOrWhiteSpace(synced));
    }

    /// <summary>LRC：每行前面一个或几个 [分:秒.百分秒]。</summary>
    public static SongLyrics Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int end = 0;
            var times = new List<TimeSpan>();
            foreach (Match m in Stamp.Matches(line))
            {
                if (m.Index != end) break; // 只认行首连着的时间
                end = m.Index + m.Length;
                double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), System.Globalization.CultureInfo.InvariantCulture);
                times.Add(TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value) * 60 + sec));
            }
            if (times.Count == 0) continue;
            string text = line[end..].Trim();
            foreach (var t in times) lines.Add(new LyricLine(t, text));
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return new SongLyrics(lines.ToArray(), false);
    }

    static string Hash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
}

/// <summary>歌词服务这会儿连不上（断网、太忙）。</summary>
internal sealed class LyricsUnavailableException : Exception;
