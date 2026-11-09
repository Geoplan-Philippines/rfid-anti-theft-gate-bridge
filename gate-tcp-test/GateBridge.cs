// Gate bridge: sends each tag the gate reports to the anti-theft server, which decides whether it is an alarm
// (POST {serverUrl}/api/v1/alarms). Nothing in this file touches card_7k.dll, so the 64-bit test project can
// compile it directly (the app itself is 32-bit and cannot be loaded there).
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace GateTcpTest;

// legacy: card_7k.dll runs the TCP server and calls OutputTags. v2: this app runs the server (V2GateServer.cs).
enum SdkMode { Legacy, V2 }

// v2 only. Off: no per-tag alarm commands; the alarm channels command is still sent once when a gate connects, as
// before. Selective: nothing at connect; one alarm command per checked batch that holds an unsold (or, per policy,
// unknown) tag. See GateAlarmCoordinator.cs.
enum PhysicalAlarmMode { Off, Selective }

// What the gate does when the sold check gives no answer for a tag (timeout, 429, 5xx, bad or missing answer).
enum SoldCheckFailurePolicy { Alarm, Silent }

// A gate-bridge.json value that would make the app talk to the gate wrongly. Unlike a warning, it stops the app at startup.
sealed class ConfigException(string message) : Exception(message);

// gate-bridge.json, read once at startup. AlarmUrl == null means log-only: tags are printed but never sent.
sealed class BridgeConfig
{
    public const string FileName = "gate-bridge.json";
    public const int DefaultCooldownSeconds = 30;
    public const int DefaultListenPort = 9000;
    public const int DefaultReplyTimeoutMs = 5000;
    public const int DefaultSoldCheckTimeoutMs = 800;
    public const string SoldCheckPath = "/api/v1/anti-theft/known-epcs/check";
    public static readonly IReadOnlyList<byte> TestedAlarmChannels = new byte[] { 1, 1, 1, 1 };

    public Uri? AlarmUrl { get; init; }
    public Uri? SoldCheckUrl { get; init; } // set whenever AlarmUrl is; only used in selective mode
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(DefaultCooldownSeconds);
    public IReadOnlyDictionary<string, string> Gates { get; init; } = new Dictionary<string, string>();

    // Used by v2 mode only; legacy mode ignores them.
    public SdkMode SdkMode { get; init; } = SdkMode.Legacy;
    public IPAddress ListenIp { get; init; } = IPAddress.Any;
    public int ListenPort { get; init; } = DefaultListenPort;
    public IReadOnlyList<byte> AlarmChannels { get; init; } = new byte[] { 1, 1, 1, 1 }; // channel_1..channel_4, 0 = off, 1 = on
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromMilliseconds(DefaultReplyTimeoutMs);
    public PhysicalAlarmMode PhysicalAlarm { get; init; } = PhysicalAlarmMode.Off;
    public TimeSpan SoldCheckTimeout { get; init; } = TimeSpan.FromMilliseconds(DefaultSoldCheckTimeoutMs);
    public SoldCheckFailurePolicy OnSoldCheckFailure { get; init; } = SoldCheckFailurePolicy.Alarm;

    sealed class FileModel
    {
        public string? ServerUrl { get; set; }
        public int? CooldownSeconds { get; set; }
        public Dictionary<string, string>? Gates { get; set; }
        // Read as raw JSON and checked by hand, so a wrong type ("listenPort": "9000") gets the same clear message
        // as a wrong value, instead of falling back to legacy mode like an unreadable file does.
        public JsonElement? SdkMode { get; set; }
        public JsonElement? ListenIp { get; set; }
        public JsonElement? ListenPort { get; set; }
        public JsonElement? AlarmChannels { get; set; }
        public JsonElement? ReplyTimeoutMs { get; set; }
        public JsonElement? PhysicalAlarm { get; set; }
        public JsonElement? SoldCheckTimeoutMs { get; set; }
        public JsonElement? OnSoldCheckFailure { get; set; }
    }

    static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // a misspelt key is reported, not silently ignored
    };

    // Every problem is reported through warn and leaves the app running in log-only (legacy) mode, except a bad value
    // for one of the v2 settings, which throws ConfigException.
    public static BridgeConfig Load(string path, Action<string> warn)
    {
        if (!File.Exists(path))
        {
            warn($"{FileName} not found at {path}");
            return new BridgeConfig();
        }
        try { return Parse(File.ReadAllText(path), warn); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warn($"{FileName} could not be read: {ex.Message}");
            return new BridgeConfig();
        }
    }

    public static BridgeConfig Parse(string json, Action<string> warn)
    {
        FileModel file = JsonSerializer.Deserialize<FileModel>(json, FileOptions) ?? new FileModel();

        SdkMode mode = ReadSdkMode(file.SdkMode);
        IPAddress listenIp = ReadListenIp(file.ListenIp);
        int listenPort = ReadWholeNumber(file.ListenPort, "listenPort", DefaultListenPort, 1, 65535,
            "Use a port number from 1 to 65535, for example 9000.");
        byte[] alarmChannels = ReadAlarmChannels(file.AlarmChannels);
        int replyTimeoutMs = ReadWholeNumber(file.ReplyTimeoutMs, "replyTimeoutMs", DefaultReplyTimeoutMs, 1, 600_000,
            "Use a number of milliseconds from 1 to 600000, for example 5000.");
        PhysicalAlarmMode physicalAlarm = ReadChoice(file.PhysicalAlarm, "physicalAlarm", PhysicalAlarmMode.Off,
            ("off", PhysicalAlarmMode.Off), ("selective", PhysicalAlarmMode.Selective));
        int soldCheckTimeoutMs = ReadWholeNumber(file.SoldCheckTimeoutMs, "soldCheckTimeoutMs", DefaultSoldCheckTimeoutMs, 1, 60_000,
            "Use a number of milliseconds from 1 to 60000, for example 800.");
        SoldCheckFailurePolicy onFailure = ReadChoice(file.OnSoldCheckFailure, "onSoldCheckFailure", SoldCheckFailurePolicy.Alarm,
            ("alarm", SoldCheckFailurePolicy.Alarm), ("silent", SoldCheckFailurePolicy.Silent));

        var gates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ip, name) in file.Gates ?? new())
        {
            if (string.IsNullOrWhiteSpace(name)) warn($"gate {ip} has an empty name, so it will use its IP");
            else gates[ip.Trim()] = name.Trim();
        }

        TimeSpan cooldown = TimeSpan.FromSeconds(DefaultCooldownSeconds);
        if (file.CooldownSeconds is int seconds)
        {
            if (seconds >= 0) cooldown = TimeSpan.FromSeconds(seconds);
            else warn($"cooldownSeconds {seconds} is negative, using {DefaultCooldownSeconds}");
        }

        Uri? alarmUrl = null;
        string serverUrl = file.ServerUrl?.Trim() ?? "";
        if (serverUrl.Length == 0)
            warn("serverUrl is missing");
        else if (!Uri.TryCreate(serverUrl.TrimEnd('/') + "/api/v1/alarms", UriKind.Absolute, out alarmUrl)
                 || alarmUrl.Scheme is not ("http" or "https"))
        {
            warn($"serverUrl \"{serverUrl}\" is not an http:// or https:// address");
            alarmUrl = null;
        }
        Uri? soldCheckUrl = alarmUrl == null ? null : new Uri(serverUrl.TrimEnd('/') + SoldCheckPath);

        if (physicalAlarm == PhysicalAlarmMode.Selective)
        {
            if (soldCheckUrl == null)
                throw new ConfigException("physicalAlarm \"selective\" needs serverUrl: the gate only sounds after the server " +
                                          "says whether each tag is sold. Set serverUrl, for example \"http://localhost:8000\".");
            if (mode != SdkMode.V2)
                warn("physicalAlarm only applies when sdkMode is \"v2\"; legacy mode ignores it");
            else if (!alarmChannels.SequenceEqual(TestedAlarmChannels))
                warn($"alarmChannels [{string.Join(",", alarmChannels)}] has not been tested on the gate; only [1,1,1,1] has");
        }

        return new BridgeConfig
        {
            AlarmUrl = alarmUrl, SoldCheckUrl = soldCheckUrl, Cooldown = cooldown, Gates = gates,
            SdkMode = mode, ListenIp = listenIp, ListenPort = listenPort, AlarmChannels = alarmChannels,
            ReplyTimeout = TimeSpan.FromMilliseconds(replyTimeoutMs),
            PhysicalAlarm = physicalAlarm, SoldCheckTimeout = TimeSpan.FromMilliseconds(soldCheckTimeoutMs),
            OnSoldCheckFailure = onFailure,
        };
    }

    static T ReadChoice<T>(JsonElement? value, string key, T fallback, params (string Text, T Value)[] choices)
    {
        if (Given(value) is not JsonElement v) return fallback;
        string? text = v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
        foreach ((string name, T choice) in choices)
            if (string.Equals(text, name, StringComparison.OrdinalIgnoreCase)) return choice;
        throw new ConfigException($"{key} {v.GetRawText()} is not valid. Use " +
                                  string.Join(" or ", choices.Select(c => $"\"{c.Text}\"")) + ".");
    }

    // A missing key or null means "use the default".
    static JsonElement? Given(JsonElement? value) =>
        value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } v ? v : null;

    static SdkMode ReadSdkMode(JsonElement? value)
    {
        if (Given(value) is not JsonElement v) return SdkMode.Legacy;
        string? text = v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
        if (string.Equals(text, "legacy", StringComparison.OrdinalIgnoreCase)) return SdkMode.Legacy;
        if (string.Equals(text, "v2", StringComparison.OrdinalIgnoreCase)) return SdkMode.V2;
        throw new ConfigException($"sdkMode {v.GetRawText()} is not valid. Use \"legacy\" (the old card_7k.dll runs the TCP server) " +
                                  "or \"v2\" (this app runs it, needs HAX's updated card_7k.dll).");
    }

    static IPAddress ReadListenIp(JsonElement? value)
    {
        if (Given(value) is not JsonElement v) return IPAddress.Any;
        string text = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        // IPAddress.TryParse also takes shorthands like "9000" (= 0.0.35.40), so an IPv4 address must have all four parts.
        if (IPAddress.TryParse(text, out IPAddress? ip) && (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || text.Count(c => c == '.') == 3))
            return ip;
        throw new ConfigException($"listenIp {v.GetRawText()} is not an IP address. Use \"0.0.0.0\" (listen on every network " +
                                  "card of this PC) or one of this PC's own IP addresses.");
    }

    static int ReadWholeNumber(JsonElement? value, string key, int fallback, int min, int max, string hint)
    {
        if (Given(value) is not JsonElement v) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) && n >= min && n <= max) return n;
        throw new ConfigException($"{key} {v.GetRawText()} is not valid. {hint}");
    }

    static byte[] ReadAlarmChannels(JsonElement? value)
    {
        if (Given(value) is not JsonElement v) return new byte[] { 1, 1, 1, 1 };
        if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 4
            && v.EnumerateArray().All(c => c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int n) && n is 0 or 1))
            return v.EnumerateArray().Select(c => (byte)c.GetInt32()).ToArray();
        throw new ConfigException($"alarmChannels {v.GetRawText()} is not valid. Use a list of 4 numbers, one per alarm " +
                                  "channel, each 0 (off) or 1 (on), for example [1,1,1,1].");
    }
}

enum PostAction { Done, Retry, Fail }

static class AlarmRules
{
    // card_7k.dll's connID is "ip:port" and the port changes on every reconnect, so gates are known by IP only.
    public static string IpOf(string connId)
    {
        int colon = connId.LastIndexOf(':');
        return (colon > 0 ? connId[..colon] : connId).Trim().Trim('[', ']');
    }

    public static string ReaderName(string connId, IReadOnlyDictionary<string, string> gates)
    {
        string ip = IpOf(connId);
        return gates.TryGetValue(ip, out string? name) ? name : ip;
    }

    // 201 = alarm recorded, 200 = sold tag or duplicate. 400 means our body is wrong, so resending cannot help;
    // the same goes for any other 4xx. 429 and 5xx are the server being busy or broken right now.
    public static PostAction ActionFor(int status) => status switch
    {
        >= 200 and < 300 => PostAction.Done,
        429 or >= 500 => PostAction.Retry,
        _ => PostAction.Fail,
    };

    // Wait before retry number retry + 1: 1 s, 2 s, 4 s, or longer if the server sent Retry-After (at most 30 s).
    public static TimeSpan RetryDelay(int retry, TimeSpan? retryAfter)
    {
        TimeSpan backoff = TimeSpan.FromSeconds(1 << retry);
        return retryAfter is TimeSpan ra && ra > backoff ? TimeSpan.FromSeconds(Math.Min(ra.TotalSeconds, 30)) : backoff;
    }

    // UTC with milliseconds and a Z, e.g. 2026-11-02T10:14:20.000Z.
    public static string FormatScannedAt(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // The server wraps answers as { statusCode, message, data: { outcome } }; errors carry message as a string or a list.
    public static (string? Outcome, string? Message) ReadReply(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            string? outcome = root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("outcome", out JsonElement o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            string? message = !root.TryGetProperty("message", out JsonElement m) ? null : m.ValueKind switch
            {
                JsonValueKind.String => m.GetString(),
                JsonValueKind.Array => string.Join("; ", m.EnumerateArray().Select(x => x.ToString())),
                _ => null,
            };
            return (outcome, message);
        }
        catch (JsonException) { return (null, null); } // not JSON, e.g. a proxy's HTML error page
    }
}

// Remembers when each EPC was last queued, so a tag that stays in range (re-reported every 2.5-3 s) is sent once
// per window. Times are monotonic clock readings, so a change to the PC clock cannot block or flood tags.
sealed class Cooldown(TimeSpan window)
{
    readonly Dictionary<string, TimeSpan> lastQueued = new();
    readonly object sync = new();
    TimeSpan lastPrune;

    public int Count { get { lock (sync) return lastQueued.Count; } }

    // True, and records now, when epc was not queued within the window.
    public bool TryStart(string epc, TimeSpan now)
    {
        lock (sync)
        {
            if (now - lastPrune >= window)
            {
                foreach (var (key, queued) in lastQueued) // Dictionary allows Remove while enumerating
                    if (now - queued >= window) lastQueued.Remove(key);
                lastPrune = now;
            }
            if (lastQueued.TryGetValue(epc, out TimeSpan last) && now - last < window) return false;
            lastQueued[epc] = now;
            return true;
        }
    }

    // Lets the next report of epc queue it again. Does nothing if epc was queued again since queuedAt.
    public void Clear(string epc, TimeSpan queuedAt)
    {
        lock (sync)
            if (lastQueued.TryGetValue(epc, out TimeSpan last) && last == queuedAt) lastQueued.Remove(epc);
    }
}

record AlarmItem(string Epc, string ReaderName, DateTime ScannedAt, TimeSpan QueuedAt);

// Offer() runs on card_7k.dll's callback thread (OfferForReader() on a v2 gate's task) and only does a cooldown
// check and a non-blocking queue write.
// One background worker posts the queued items, one at a time, with a single shared HttpClient.
sealed class AlarmBridge
{
    public const int MaxRetries = 3;
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);
    const int QueueCapacity = 1000;

    readonly Uri alarmUrl;
    readonly IReadOnlyDictionary<string, string> gates;
    readonly Action<string> log;
    readonly Cooldown cooldown;
    readonly Channel<AlarmItem> queue = Channel.CreateBounded<AlarmItem>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    // The server can take ~10 s to answer (it grabs a CCTV snapshot first), so allow well over that.
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly CancellationTokenSource abort = new();
    readonly Task worker;
    volatile bool stopped;
    int pending; // queued or being sent

    public AlarmBridge(BridgeConfig config, Action<string> log)
    {
        alarmUrl = config.AlarmUrl ?? throw new ArgumentException("AlarmUrl is required", nameof(config));
        gates = config.Gates;
        this.log = log;
        cooldown = new Cooldown(config.Cooldown);
        worker = Task.Run(RunAsync);
    }

    static TimeSpan Now() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    // Legacy mode: card_7k.dll's connID ("ip:port") is turned into the reader name here.
    public void Offer(string connId, string epc) => OfferForReader(AlarmRules.ReaderName(connId, gates), epc);

    // v2 mode: V2GateServer already turned the gate socket's remote IP into the reader name, by the same rule.
    public void OfferForReader(string readerName, string epc)
    {
        TimeSpan now = Now();
        if (!cooldown.TryStart(epc, now)) return;
        Interlocked.Increment(ref pending);
        if (queue.Writer.TryWrite(new AlarmItem(epc, readerName, DateTime.UtcNow, now))) return;
        Interlocked.Decrement(ref pending);
        cooldown.Clear(epc, now);
        if (!stopped) log($"SEND FAILED epc={epc} not queued, {QueueCapacity} alarms are already waiting");
    }

    // Stops accepting tags, gives the worker up to grace to empty the queue, then abandons whatever is left.
    public void Stop(TimeSpan grace)
    {
        stopped = true;
        queue.Writer.TryComplete();
        if (Volatile.Read(ref pending) > 0)
            log($"BRIDGE      stopping, waiting up to {grace.TotalSeconds:0} s for {pending} unsent alarm(s)");
        if (worker.Wait(grace)) return;
        abort.Cancel();
        worker.Wait(TimeSpan.FromSeconds(1));
        log($"BRIDGE      stopped with {Volatile.Read(ref pending)} alarm(s) not sent");
    }

    async Task RunAsync()
    {
        try
        {
            await foreach (AlarmItem item in queue.Reader.ReadAllAsync(abort.Token))
            {
                try { await SendAsync(item); }
                catch (Exception ex) when (ex is not OperationCanceledException || !abort.IsCancellationRequested)
                {
                    GiveUp(item, $"SEND FAILED epc={item.Epc} reader={item.ReaderName} {ex.Message}"); // one bad item must not stop the worker
                }
                Interlocked.Decrement(ref pending);
            }
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
    }

    async Task SendAsync(AlarmItem item)
    {
        string who = $"epc={item.Epc} reader={item.ReaderName}";
        for (int retry = 0; ; retry++)
        {
            if (Now() - item.QueuedAt > MaxAge)
            {
                GiveUp(item, $"SEND FAILED {who} dropped, still unsent after {MaxAge.TotalMinutes:0} minutes");
                return;
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();
            (int? status, string? outcome, string? detail, TimeSpan? retryAfter) = await PostAsync(item);
            string took = $"{timer.Elapsed.TotalSeconds:0.0} s";
            PostAction action = status is int s ? AlarmRules.ActionFor(s) : PostAction.Retry; // no answer: network error

            if (action == PostAction.Done)
            {
                string reply = $"(HTTP {status}{(outcome is null ? "" : $" {outcome}")}, {took})";
                log((status, outcome) switch
                {
                    (201, _) => $"ALARM SENT  {who} alarm recorded {reply}",
                    (_, "ignored_sold") => $"SOLD TAG    {who} ignored, the tag is sold {reply}",
                    (_, "duplicate") => $"DUPLICATE   {who} already alarmed in the last 30 s {reply}",
                    _ => $"SENT        {who} unexpected answer {reply}",
                });
                return;
            }

            string why = status is int code ? $"HTTP {code}{(detail is null ? "" : $" {detail}")}" : detail ?? "no answer";
            if (action == PostAction.Fail)
            {
                GiveUp(item, $"SEND FAILED {who} {why}, not retried");
                return;
            }
            if (retry == MaxRetries)
            {
                GiveUp(item, $"SEND FAILED {who} {why}, gave up after {MaxRetries} retries");
                return;
            }
            TimeSpan delay = AlarmRules.RetryDelay(retry, retryAfter);
            log($"RETRY       {who} {why} ({took}), retry {retry + 1} of {MaxRetries} in {delay.TotalSeconds:0} s");
            await Task.Delay(delay, abort.Token);
        }
    }

    void GiveUp(AlarmItem item, string line)
    {
        cooldown.Clear(item.Epc, item.QueuedAt); // the gate's next report of this tag tries again
        log(line);
    }

    // Status null means no HTTP answer at all; detail then says why.
    async Task<(int? Status, string? Outcome, string? Detail, TimeSpan? RetryAfter)> PostAsync(AlarmItem item)
    {
        string json = JsonSerializer.Serialize(new
        {
            epc = item.Epc,
            readerName = item.ReaderName,
            scannedAt = AlarmRules.FormatScannedAt(item.ScannedAt),
        });
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await http.PostAsync(alarmUrl, content, abort.Token);
            string body = await response.Content.ReadAsStringAsync(abort.Token);
            (string? outcome, string? message) = AlarmRules.ReadReply(body);
            return ((int)response.StatusCode, outcome, message, response.Headers.RetryAfter?.Delta);
        }
        catch (Exception ex) when (!abort.IsCancellationRequested) // no connection, connection dropped, or HttpClient.Timeout
        {
            return (null, null, ex is TaskCanceledException ? $"no answer within {http.Timeout.TotalSeconds:0} s" : ex.Message, null);
        }
    }
}
