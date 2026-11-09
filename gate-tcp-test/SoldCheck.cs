// Sold check for the physical alarm (selective mode): asks the server which tags of a batch are sold
// (POST {serverUrl}/api/v1/anti-theft/known-epcs/check), then decides whether the gate should sound.
// Separate from AlarmBridge (GateBridge.cs), which reports tags to the dashboard: different client, no cooldown, no
// retries, no waiting on CCTV. Nothing in this file touches card_7k.dll, so the test project compiles it directly.
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GateTcpTest;

// Unknown is "no answer", never "unsold": a failed check is reported as a failure, and only the failure policy turns
// it into an alarm.
enum EpcStatus { Sold, Unsold, Unknown }

record EpcCheck(string Epc, EpcStatus Status);

// Failure is null when the server answered for every EPC.
record SoldCheckResult(IReadOnlyList<EpcCheck> Checks, string? Failure, TimeSpan Took);

interface ISoldStatusClient
{
    // Never throws for a server or network problem: those come back as Unknown with Failure set.
    Task<SoldCheckResult> CheckAsync(IReadOnlyList<string> epcs, CancellationToken cancel);
}

static class EpcText
{
    // The server's rule (alarms.service.ts / anti-theft.service.ts): strip whitespace, uppercase.
    public static string Normalize(string epc) => Regex.Replace(epc, @"\s", "").ToUpperInvariant();
}

sealed class HttpSoldStatusClient : ISoldStatusClient, IDisposable
{
    public const int MaxEpcsPerCheck = 100; // the server's limit (CheckKnownEpcsDto)

    readonly Uri url;
    readonly TimeSpan timeout;
    // Keep-alive connections: a check must not pay for a new TCP connection each time.
    readonly HttpClient http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
        Timeout = Timeout.InfiniteTimeSpan, // each check has its own deadline, below
    };

    public HttpSoldStatusClient(Uri url, TimeSpan timeout)
    {
        this.url = url;
        this.timeout = timeout;
    }

    public async Task<SoldCheckResult> CheckAsync(IReadOnlyList<string> epcs, CancellationToken cancel)
    {
        if (epcs.Count is 0 or > MaxEpcsPerCheck)
            throw new ArgumentOutOfRangeException(nameof(epcs), epcs.Count, $"1 to {MaxEpcsPerCheck} EPCs per check");

        var timer = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout);
        string body;
        HttpStatusCode status;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { epcs }), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await http.PostAsync(url, content, deadline.Token);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return AllUnknown(epcs, $"no answer within {timeout.TotalMilliseconds:0} ms", timer.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return AllUnknown(epcs, ex.Message, timer.Elapsed);
        }

        if (status != HttpStatusCode.OK)
            return AllUnknown(epcs, $"HTTP {(int)status}{Detail(body)}", timer.Elapsed);
        return Read(epcs, body, timer.Elapsed);
    }

    // The server's answer: { data: { results: [ { epc, sold }, ... ] } }. Anything else is no answer.
    public static SoldCheckResult Read(IReadOnlyList<string> epcs, string body, TimeSpan took)
    {
        var sold = new Dictionary<string, bool>(StringComparer.Ordinal);
        var conflicting = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
                return AllUnknown(epcs, "malformed answer: no data.results list", took);

            foreach (JsonElement r in results.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object
                    || !r.TryGetProperty("epc", out JsonElement e) || e.ValueKind != JsonValueKind.String
                    || !r.TryGetProperty("sold", out JsonElement s) || s.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    continue; // a broken entry answers nothing; its EPC ends up unknown below
                string epc = EpcText.Normalize(e.GetString()!);
                bool value = s.GetBoolean();
                if (sold.TryGetValue(epc, out bool earlier) && earlier != value) conflicting.Add(epc);
                sold[epc] = value;
            }
        }
        catch (JsonException ex)
        {
            return AllUnknown(epcs, $"malformed answer: {ex.Message}", took);
        }

        var checks = new List<EpcCheck>(epcs.Count);
        int missing = 0;
        foreach (string epc in epcs)
        {
            string key = EpcText.Normalize(epc);
            if (!conflicting.Contains(key) && sold.TryGetValue(key, out bool isSold))
                checks.Add(new EpcCheck(epc, isSold ? EpcStatus.Sold : EpcStatus.Unsold));
            else
            {
                checks.Add(new EpcCheck(epc, EpcStatus.Unknown));
                missing++;
            }
        }
        return new SoldCheckResult(checks, missing == 0 ? null : $"no usable answer for {missing} of {epcs.Count} EPC(s)", took);
    }

    static SoldCheckResult AllUnknown(IReadOnlyList<string> epcs, string why, TimeSpan took) =>
        new(epcs.Select(e => new EpcCheck(e, EpcStatus.Unknown)).ToList(), why, took);

    static string Detail(string body)
    {
        (_, string? message) = AlarmRules.ReadReply(body);
        return message is null ? "" : $" {message}";
    }

    public void Dispose() => http.Dispose();
}

enum AlarmReason { None, Unsold, Unknown, UnknownButSilent, AllSold }

record AlarmDecision(bool Alarm, AlarmReason Reason, int Sold, int Unsold, int Unknown);

static class PhysicalAlarmRules
{
    // One decision per checked batch. Any confirmed unsold tag sounds the gate once. All sold: silent. Otherwise an
    // unknown tag (failed check, or tag data the SDK could not fully read) follows the failure policy.
    public static AlarmDecision Decide(IReadOnlyList<EpcCheck> checks, int unreadableTags, SoldCheckFailurePolicy policy)
    {
        int sold = checks.Count(c => c.Status == EpcStatus.Sold);
        int unsold = checks.Count(c => c.Status == EpcStatus.Unsold);
        int unknown = checks.Count(c => c.Status == EpcStatus.Unknown) + unreadableTags;
        AlarmReason reason =
            unsold > 0 ? AlarmReason.Unsold
            : unknown > 0 ? (policy == SoldCheckFailurePolicy.Alarm ? AlarmReason.Unknown : AlarmReason.UnknownButSilent)
            : sold > 0 ? AlarmReason.AllSold
            : AlarmReason.None;
        return new AlarmDecision(reason is AlarmReason.Unsold or AlarmReason.Unknown, reason, sold, unsold, unknown);
    }
}

// One batch waiting for a sold check: distinct EPCs in first-seen order, plus tags the SDK reported but whose EPC
// could not be read (they have no EPC to look up, so they are unknown from the start).
sealed class LookupBatch
{
    public List<string> Epcs { get; } = new();
    public int UnreadableTags { get; set; }
}

// The per-gate queue between the socket reader and the sold check. Bounded: at most maxBatches batches of at most
// maxEpcs EPCs. An EPC already waiting is not queued twice (its check has not started yet, so it is still fresh).
// When the queue is full, Add hands the rest back: the caller decides those as unknown, never drops them.
sealed class LookupQueue(int maxBatches, int maxEpcs)
{
    readonly LinkedList<LookupBatch> batches = new();
    readonly HashSet<string> waiting = new(StringComparer.Ordinal);

    public int Batches => batches.Count;
    public int WaitingEpcs => waiting.Count;

    public (IReadOnlyList<string> Overflow, bool UnreadableOverflow) Add(IReadOnlyList<string> epcs, int unreadableTags)
    {
        var overflow = new List<string>();
        foreach (string epc in epcs)
        {
            if (waiting.Contains(epc)) continue;
            LookupBatch? batch = Room();
            if (batch == null)
            {
                overflow.Add(epc);
                continue;
            }
            batch.Epcs.Add(epc);
            waiting.Add(epc);
        }

        bool unreadableOverflow = false;
        if (unreadableTags > 0)
        {
            LookupBatch? batch = batches.Last?.Value ?? Room();
            if (batch == null) unreadableOverflow = true;
            else batch.UnreadableTags += unreadableTags;
        }
        return (overflow, unreadableOverflow);
    }

    public LookupBatch? Take()
    {
        if (batches.First is not { } first) return null;
        batches.RemoveFirst();
        foreach (string epc in first.Value.Epcs) waiting.Remove(epc);
        return first.Value;
    }

    LookupBatch? Room()
    {
        if (batches.Last is { } last && last.Value.Epcs.Count < maxEpcs) return last.Value;
        if (batches.Count >= maxBatches) return null;
        return batches.AddLast(new LookupBatch()).Value;
    }
}
