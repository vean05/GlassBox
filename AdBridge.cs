using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GlassBox;

/// <summary>
/// 和玻璃收纳自带的 Edge 扩展（edge-extension 文件夹）说话：扩展在 YouTube 页面里看到“跳过”按钮就来报一声，
/// 这边回它要不要按。只听本机 127.0.0.1 的一个端口，只认浏览器扩展发来的请求（网页伪造不了 Origin），
/// 只在打开“跳过广告”时开着；没广告时一点动静都没有。扩展在后台标签页、窗口最小化时也能用，
/// 所以有了它就不用再从外面去读网页（AdSkipper）。
/// </summary>
internal sealed class AdBridge : IDisposable
{
    public const int Port = 48613;
    /// <summary>跟这一版玻璃收纳配套的扩展版本；Edge 里还是旧的就提醒用户点“重新加载”。</summary>
    public const string ExtensionVersion = "1.6.0";
    /// <summary>
    /// 自带扩展的 ID：manifest.json 里写死了 key，谁装都是这个 ID。只听它的，别的扩展说话一概不理
    /// （网页伪造不了 Origin；不认 ID 的话，装着的任何扩展都能来报广告、问设置）。
    /// </summary>
    public const string ExtensionId = "hmjiiikcabacmnhhaagbehoblggahmak";
    const int GoneMs = 2500; // 这么久没再报“能跳”：标签页关了或者广告完了

    readonly TcpListener listener = new(IPAddress.Loopback, Port);
    readonly object gate = new();
    readonly Dictionary<int, long> skippableTabs = new(); // 标签页 → 最后一次报“能跳”的时间
    readonly System.Threading.Timer expire;
    bool showing, skipRequested, clickSent, disposed, foreignNoted;

    /// <summary>有标签页能跳了 / 都没了（在后台线程上调）。</summary>
    public event Action<bool>? Changed;
    /// <summary>扩展按了跳过，广告没了（在后台线程上调）。</summary>
    public event Action? Skipped;
    /// <summary>扩展按了但没跳成（在后台线程上调）：胶囊上的“跳过”要再出来。</summary>
    public event Action? Failed;
    /// <summary>头一次听到扩展说话（在后台线程上调）。</summary>
    public event Action? Connected;

    public volatile bool AutoSkip;
    /// <summary>开着“跳过广告”：关着时扩展报的广告不理（只帮“仍在观看吗？”传话时）。</summary>
    public volatile bool SkipAds = true;
    /// <summary>YouTube 问“仍在观看吗？”时让扩展点“继续”。</summary>
    public volatile bool KeepWatching;
    /// <summary>这次开机后扩展来过：说明装好了。</summary>
    public bool Seen { get; private set; }
    /// <summary>扩展报的版本（旧扩展不报，是空的）。</summary>
    public string Version { get; private set; } = "";
    public bool Outdated => Seen && Version != ExtensionVersion;
    /// <summary>扩展真的在帮忙（是我们自己的那个，不是只说过一声 hello 就被拒的旧副本）。</summary>
    public bool Working => Seen && Version != "other";

    AdBridge()
    {
        expire = new System.Threading.Timer(_ => Update(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>开始听；端口被别的程序占了就返回 null（这时只能用老办法）。</summary>
    public static AdBridge? Start()
    {
        var bridge = new AdBridge();
        try
        {
            bridge.listener.Start(8);
        }
        catch (SocketException ex)
        {
            Log.Info($"ad bridge: port {Port} unavailable ({ex.SocketErrorCode})");
            bridge.Dispose();
            return null;
        }
        _ = bridge.AcceptLoop();
        return bridge;
    }

    public void Skip()
    {
        lock (gate) skipRequested = true;
    }

    async Task AcceptLoop()
    {
        while (!disposed)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch { if (disposed) return; await Task.Delay(1000).ConfigureAwait(false); continue; }
            _ = Serve(client);
        }
    }

    async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = client.SendTimeout = 3000;
                var stream = client.GetStream();
                using var cts = new CancellationTokenSource(3000);
                var (head, body) = await ReadRequest(stream, cts.Token).ConfigureAwait(false);
                if (head == null) return;
                string reply = Handle(head, body, out int status);
                byte[] payload = Encoding.UTF8.GetBytes(reply);
                string header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "No")}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cts.Token).ConfigureAwait(false);
                await stream.WriteAsync(payload, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) { Log.Error(ex); }
        }
    }

    static async Task<(string? Head, string Body)> ReadRequest(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            int end = IndexOf(buffer, length);
            if (end < 0) continue;
            string head = Encoding.ASCII.GetString(buffer, 0, end);
            int want = 0;
            foreach (var line in head.Split("\r\n"))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out want);
            want = Math.Clamp(want, 0, buffer.Length - end - 4);
            while (length < end + 4 + want)
            {
                read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            return (head, Encoding.UTF8.GetString(buffer, end + 4, Math.Min(want, length - end - 4)));
        }
        return (null, "");
    }

    static int IndexOf(byte[] buffer, int length)
    {
        for (int i = 0; i + 3 < length; i++)
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
        return -1;
    }

    string Handle(string head, string body, out int status)
    {
        status = 403;
        var lines = head.Split("\r\n");
        var request = lines[0].Split(' ');
        string Header(string name) => lines.Skip(1).FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim() ?? "";
        // 只认本机地址（防有人用自己的域名指过来）和浏览器扩展（网页发的请求 Origin 是网站，改不了）
        string host = Header("Host");
        if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}") return "{}";
        string origin = Header("Origin");
        if (!origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) return "{}";
        if (request.Length < 2 || request[0] != "POST") return "{}";
        if (!origin.Equals("chrome-extension://" + ExtensionId, StringComparison.Ordinal))
        {
            // 不是我们的扩展。旧版自带扩展（1.6 以前没固定 ID）也会落到这里：只记一下“装了旧的”，好提醒重新加载，别的都不回
            if (request[1] == "/hello" && !foreignNoted)
            {
                foreignNoted = true;
                Log.Info($"ad bridge: ignored extension {origin} (not GlassBox's own; old copy needs reloading)");
                if (!Seen)
                {
                    Seen = true;
                    Version = "other";
                    Connected?.Invoke();
                }
            }
            return "{}";
        }
        status = 200;

        int tab = 0;
        bool skippable = false;
        string version = "", result = "", diag = "";
        try
        {
            using var json = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
            if (json.RootElement.TryGetProperty("tab", out var t) && t.TryGetInt32(out int id)) tab = id;
            if (json.RootElement.TryGetProperty("skippable", out var k)) skippable = k.ValueKind == JsonValueKind.True;
            if (json.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String) version = v.GetString() ?? "";
            if (json.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String) result = r.GetString() ?? "";
            if (json.RootElement.TryGetProperty("diag", out var d) && d.ValueKind == JsonValueKind.String) diag = d.GetString() ?? "";
        }
        catch (JsonException) { return "{}"; }

        if (request[1] == "/hello")
        {
            bool first = !Seen || version != Version;
            Seen = true;
            Version = version;
            if (first)
            {
                Log.Info($"ad bridge: extension connected, version {(version.Length > 0 ? version : "1.0 (old)")}");
                Connected?.Invoke();
            }
            return "{}";
        }
        if (!Seen)
        {
            Seen = true; // 旧扩展、或者装好后没刷新页面：没说 hello 也算连上了
            Connected?.Invoke();
        }
        if (request[1] == "/still")
        {
            // 扩展看到 YouTube 停下来问“仍在观看吗？”：问这边要不要点继续；点完再报一次结果（记进日志）
            if (result.Length > 0) Log.Info($"ad bridge: still-watching prompt, {result}" + (diag.Length > 0 ? " | " + diag[..Math.Min(diag.Length, 300)] : ""));
            return KeepWatching ? "{\"continue\":true}" : "{}";
        }
        if (request[1] != "/ad" || !SkipAds) return "{}";
        if (result.Length > 0)
        {
            // 扩展按完的结果：click = 按钮按成了，end = 按钮不理脚本、把广告放到了结尾，failed = 都没成
            Log.Info($"ad bridge: skip result {result}, still skippable {skippable}" + (diag.Length > 0 ? " | " + diag[..Math.Min(diag.Length, 500)] : ""));
            if (result == "failed" && skippable)
            {
                lock (gate) { clickSent = false; skippableTabs[tab] = Environment.TickCount64; }
                Failed?.Invoke();
                Update();
                return "{}";
            }
        }

        bool skip = false;
        lock (gate)
        {
            if (skippable)
            {
                skippableTabs[tab] = Environment.TickCount64;
                skip = skipRequested;
                skipRequested = false;
                if (skip || AutoSkip) clickSent = true;
            }
            else skippableTabs.Remove(tab);
        }
        Update();
        return skip ? "{\"skip\":true,\"auto\":" + (AutoSkip ? "true" : "false") + "}" : AutoSkip ? "{\"auto\":true}" : "{}";
    }

    /// <summary>重新算有没有标签页能跳；状态变了就报；还有能跳的就过一会儿再看（标签页可能直接关了）。</summary>
    void Update()
    {
        bool now, skipped = false, changed;
        lock (gate)
        {
            long t = Environment.TickCount64;
            foreach (var gone in skippableTabs.Where(p => t - p.Value > GoneMs).Select(p => p.Key).ToList()) skippableTabs.Remove(gone);
            now = skippableTabs.Count > 0;
            changed = now != showing;
            if (changed)
            {
                showing = now;
                if (!now)
                {
                    skipped = clickSent;
                    clickSent = false;
                    skipRequested = false;
                }
            }
            if (!disposed) expire.Change(now ? GoneMs + 100 : Timeout.Infinite, Timeout.Infinite);
        }
        if (!changed) return;
        Log.Info(now ? "ad bridge: skip button is up" : skipped ? "ad bridge: ad skipped" : "ad bridge: ad gone");
        if (skipped) Skipped?.Invoke();
        else Changed?.Invoke(now);
    }

    public void Dispose()
    {
        disposed = true;
        try { listener.Stop(); } catch { }
        expire.Dispose();
    }
}
