// A stand-in for POST /api/v1/alarms and POST /api/v1/anti-theft/known-epcs/check on 127.0.0.1 that records every
// request and answers however a test says.
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GateTcpTest.Tests;

record ReceivedPost(DateTime At, string ContentType, string Epc, string? ReaderName, string? ScannedAt);

// Status and outcome for a reply, or Abort to cut the connection without answering (a network error).
record Reply(int Status, string? Outcome = null, string? Message = null, bool Abort = false)
{
    public static readonly Reply AlarmCreated = new(201, "alarm_created");
}

// An answer to a sold check: Body as it is (null = the server's normal answer from the sold list), after Delay.
record CheckAnswer(int Status = 200, string? Body = null, TimeSpan Delay = default, bool Abort = false)
{
    public static readonly CheckAnswer FromSoldList = new();
}

sealed class FakeAlarmServer : IAsyncDisposable
{
    readonly WebApplication app;
    int answered;

    public ConcurrentQueue<ReceivedPost> Posts { get; } = new();

    // Sold checks: the EPCs of each request, in order. SoldEpcs is the sold list; Check says how to answer.
    public ConcurrentQueue<string[]> Checks { get; } = new();
    public ConcurrentDictionary<string, bool> SoldEpcs { get; } = new(StringComparer.Ordinal);
    public Func<string[], CheckAnswer> Check { get; set; } = _ => CheckAnswer.FromSoldList;
    public int Answered => Volatile.Read(ref answered);
    public Uri BaseUrl { get; private set; } = null!;

    // reply gets the post number (1 = first) and the EPC, and says how to answer after delay.
    FakeAlarmServer(TimeSpan delay, Func<int, string, Reply> reply)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        app = builder.Build();
        app.MapPost("/api/v1/alarms", async (HttpContext ctx) =>
        {
            using JsonDocument body = await JsonDocument.ParseAsync(ctx.Request.Body);
            JsonElement b = body.RootElement;
            string? Str(string name) => b.TryGetProperty(name, out JsonElement v) ? v.GetString() : null;
            Posts.Enqueue(new ReceivedPost(DateTime.UtcNow, ctx.Request.ContentType ?? "", Str("epc") ?? "", Str("readerName"), Str("scannedAt")));
            Reply r = reply(Posts.Count, Str("epc") ?? "");

            await Task.Delay(delay, ctx.RequestAborted);
            Interlocked.Increment(ref answered);
            if (r.Abort)
            {
                ctx.Abort();
                return;
            }
            ctx.Response.StatusCode = r.Status;
            await ctx.Response.WriteAsJsonAsync(r.Status < 400
                ? new { statusCode = r.Status, message = "Success", data = new { outcome = r.Outcome } }
                : (object)new { statusCode = r.Status, message = r.Message ?? "error", error = "Error" });
        });
        app.MapPost("/api/v1/anti-theft/known-epcs/check", async (HttpContext ctx) =>
        {
            using JsonDocument body = await JsonDocument.ParseAsync(ctx.Request.Body);
            string[] epcs = body.RootElement.GetProperty("epcs").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
            Checks.Enqueue(epcs);
            CheckAnswer answer = Check(epcs);

            await Task.Delay(answer.Delay, ctx.RequestAborted);
            if (answer.Abort)
            {
                ctx.Abort();
                return;
            }
            ctx.Response.StatusCode = answer.Status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(answer.Body ?? SoldListAnswer(epcs));
        });
    }

    // What the real server answers: one result per normalized EPC, first-seen order.
    string SoldListAnswer(string[] epcs) => JsonSerializer.Serialize(new
    {
        statusCode = 200,
        message = "Success",
        data = new
        {
            results = epcs.Select(EpcText.Normalize).Distinct().Select(e => new { epc = e, sold = SoldEpcs.ContainsKey(e) }),
        },
    });

    public static async Task<FakeAlarmServer> StartAsync(TimeSpan delay, Func<int, string, Reply>? reply = null)
    {
        var server = new FakeAlarmServer(delay, reply ?? ((_, _) => Reply.AlarmCreated));
        await server.app.StartAsync();
        server.BaseUrl = new Uri(server.app.Urls.First());
        return server;
    }

    public async Task WaitForPosts(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Posts.Count < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{Posts.Count} of {count} posts after {timeout.TotalSeconds} s");
            await Task.Delay(10);
        }
    }

    // Selective physical alarm on 127.0.0.1, any free port, with this server for both the dashboard and the sold check.
    public BridgeConfig SelectiveConfig(IReadOnlyDictionary<string, string> gates, int soldCheckTimeoutMs = 800,
        SoldCheckFailurePolicy onFailure = SoldCheckFailurePolicy.Alarm, int replyTimeoutMs = 5000) => new()
    {
        AlarmUrl = new Uri(BaseUrl, "/api/v1/alarms"),
        SoldCheckUrl = new Uri(BaseUrl, BridgeConfig.SoldCheckPath),
        Gates = gates,
        SdkMode = SdkMode.V2,
        ListenIp = System.Net.IPAddress.Loopback,
        ListenPort = 0,
        ReplyTimeout = TimeSpan.FromMilliseconds(replyTimeoutMs),
        PhysicalAlarm = PhysicalAlarmMode.Selective,
        SoldCheckTimeout = TimeSpan.FromMilliseconds(soldCheckTimeoutMs),
        OnSoldCheckFailure = onFailure,
    };

    public BridgeConfig Config(IReadOnlyDictionary<string, string>? gates = null) => BridgeConfig.Parse(
        JsonSerializer.Serialize(new { serverUrl = BaseUrl.ToString(), gates = gates ?? new Dictionary<string, string> { ["169.254.68.93"] = "GATE-1" } }),
        w => throw new InvalidOperationException(w));

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await app.StopAsync(cts.Token); } catch (OperationCanceledException) { }
        await app.DisposeAsync();
    }
}

// Collects the printed lines (they go to the console in the real app). With echo, each line is also written to the
// test output, timestamped like the app's console.
sealed class LogLines(Xunit.Abstractions.ITestOutputHelper? echo = null)
{
    readonly ConcurrentQueue<string> lines = new();

    public void Add(string line)
    {
        lines.Enqueue(line);
        try { echo?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}"); }
        catch (InvalidOperationException) { } // xunit refuses output once the test has ended; the line is still kept here
    }

    public IReadOnlyList<string> All => lines.ToArray();
    public int Count(string prefix) => lines.Count(l => l.StartsWith(prefix, StringComparison.Ordinal));
    public int CountContaining(string text) => lines.Count(l => l.Contains(text, StringComparison.Ordinal));

    public Task WaitFor(string prefix, TimeSpan timeout, int count = 1) =>
        Until(() => Count(prefix) >= count, $"\"{prefix}\" line", timeout);

    public Task WaitForContaining(string text, TimeSpan timeout, int count = 1) =>
        Until(() => CountContaining(text) >= count, $"line containing \"{text}\"", timeout);

    async Task Until(Func<bool> done, string what, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"no {what} after {timeout.TotalSeconds} s. Lines:\n{string.Join("\n", All)}");
            await Task.Delay(25);
        }
    }
}
