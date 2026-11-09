// Selective physical alarm for one gate connection: sold check -> decision -> alarm command -> gate's reply.
//
// - The socket reader only hands tags in (OfferTags) and replies in (OnReply); it never waits for HTTP or for a send.
// - One sold check at a time per gate. Tags that arrive meanwhile wait in a bounded LookupQueue; when it is full they
//   are decided as unknown at once (logged as QUEUE FULL), never dropped.
// - At most one alarm command waits for its reply. A decision to alarm while one waits is covered by it; if that
//   command then fails (refused, no reply, not sent), one more is sent for the covered decision.
// - Nothing here knows about the dashboard (AlarmBridge): no 30 s cooldown, no CCTV wait.
// Nothing in this file touches card_7k.dll, so the test project compiles it directly.
using System.Diagnostics;
using System.Net.Sockets;

namespace GateTcpTest;

sealed class GateAlarmCoordinator : IAsyncDisposable
{
    public const int MaxQueuedChecks = 4; // waiting checks per gate, each up to 100 EPCs
    const int MaxEpcsInLogLine = 5;

    readonly string reader;
    readonly ISoldStatusClient soldCheck;
    readonly SoldCheckFailurePolicy policy;
    readonly TimeSpan replyTimeout;
    readonly Func<CancellationToken, Task<bool>> sendAlarm; // builds and writes one alarm command; false = not sent (it logged why)
    readonly Action<string> log;
    readonly object sync = new();
    readonly LookupQueue queue = new(MaxQueuedChecks, HttpSoldStatusClient.MaxEpcsPerCheck);
    readonly SemaphoreSlim checkSignal = new(0);
    readonly SemaphoreSlim alarmSignal = new(0);
    readonly CancellationTokenSource stopping = new();
    readonly Task checkLoop;
    readonly Task alarmLoop;

    bool alarmWanted;            // decided, not sent yet
    bool outstanding;            // sent, reply not seen yet
    bool coveredWhileOutstanding;
    int generation;              // which command a reply timeout belongs to
    long sentAt;
    int stopped;

    public GateAlarmCoordinator(string reader, ISoldStatusClient soldCheck, SoldCheckFailurePolicy policy, TimeSpan replyTimeout,
        Func<CancellationToken, Task<bool>> sendAlarm, Action<string> log)
    {
        this.reader = reader;
        this.soldCheck = soldCheck;
        this.policy = policy;
        this.replyTimeout = replyTimeout;
        this.sendAlarm = sendAlarm;
        this.log = log;
        checkLoop = Task.Run(CheckLoopAsync);
        alarmLoop = Task.Run(AlarmLoopAsync);
    }

    public bool AlarmOutstanding
    {
        get { lock (sync) return outstanding; }
    }

    // From the socket reader: the EPCs of one tag packet, and how many of its tags had no readable EPC. Never blocks.
    public void OfferTags(IReadOnlyList<string> epcs, int unreadableTags)
    {
        IReadOnlyList<string> overflow;
        bool unreadableOverflow;
        lock (sync) (overflow, unreadableOverflow) = queue.Add(epcs, unreadableTags);
        checkSignal.Release();
        if (overflow.Count == 0 && !unreadableOverflow) return;

        log($"QUEUE FULL  reader={reader} {MaxQueuedChecks} sold checks already waiting; sold status unknown for " +
            $"{Describe(overflow)}{(unreadableOverflow ? (overflow.Count > 0 ? " and" : "") + " tags without a readable EPC" : "")}");
        Act(PhysicalAlarmRules.Decide(overflow.Select(e => new EpcCheck(e, EpcStatus.Unknown)).ToList(),
            unreadableOverflow ? 1 : 0, policy), overflow.Select(e => new EpcCheck(e, EpcStatus.Unknown)).ToList());
    }

    // From the socket reader: the gate's reply to an alarm command (Device_UnPackDeviceAlarm's code).
    public void OnReply(int code)
    {
        bool waited, again = false;
        double ms = 0;
        lock (sync)
        {
            waited = outstanding;
            if (outstanding)
            {
                outstanding = false;
                ms = Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;
                if (code != HaxError.Ok && coveredWhileOutstanding) again = alarmWanted = true;
                coveredWhileOutstanding = false;
            }
        }
        if (!waited)
            log($"ALARM LATE  reader={reader} reply with no alarm command waiting (after its timeout?): {HaxError.Text(code)}, code {code}");
        else if (code == HaxError.Ok)
            log($"ALARM OK    reader={reader} the gate accepted the alarm command ({ms:0} ms)");
        else
            log($"ALARM FAIL  reader={reader} the gate refused the alarm command: {HaxError.Text(code)}, code {code} ({ms:0} ms)");
        if (again) Resend("the refused command");
    }

    async Task CheckLoopAsync()
    {
        try
        {
            while (true)
            {
                await checkSignal.WaitAsync(stopping.Token);
                while (true)
                {
                    LookupBatch? batch;
                    lock (sync) batch = queue.Take();
                    if (batch == null) break;
                    SoldCheckResult result = await CheckAsync(batch);
                    LogCheck(batch, result);
                    Act(PhysicalAlarmRules.Decide(result.Checks, batch.UnreadableTags, policy), result.Checks);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    async Task<SoldCheckResult> CheckAsync(LookupBatch batch)
    {
        if (batch.Epcs.Count == 0) return new SoldCheckResult([], null, TimeSpan.Zero);
        var timer = Stopwatch.StartNew();
        try { return await soldCheck.CheckAsync(batch.Epcs, stopping.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            // The client never throws for server trouble; this is a bug. Still an unknown answer, never "unsold".
            return new SoldCheckResult(batch.Epcs.Select(e => new EpcCheck(e, EpcStatus.Unknown)).ToList(),
                $"sold check crashed: {ex.Message}", timer.Elapsed);
        }
    }

    void LogCheck(LookupBatch batch, SoldCheckResult result)
    {
        if (result.Checks.Count == 0) return;
        string took = $"{result.Took.TotalMilliseconds:0} ms";
        if (result.Failure != null)
            log($"CHECK FAIL  reader={reader} {result.Failure}; sold status unknown for " +
                $"{Describe(result.Checks.Where(c => c.Status == EpcStatus.Unknown).Select(c => c.Epc).ToList())} ({took})");
        int sold = result.Checks.Count(c => c.Status == EpcStatus.Sold);
        int unsold = result.Checks.Count(c => c.Status == EpcStatus.Unsold);
        int unknown = result.Checks.Count - sold - unsold;
        log($"SOLD CHECK  reader={reader} {result.Checks.Count} tag(s): {sold} sold, {unsold} unsold, {unknown} unknown ({took})");
    }

    void Act(AlarmDecision decision, IReadOnlyList<EpcCheck> checks)
    {
        string unsold = Describe(checks.Where(c => c.Status == EpcStatus.Unsold).Select(c => c.Epc).ToList());
        switch (decision.Reason)
        {
            case AlarmReason.Unsold:
                RequestAlarm($"unsold: {unsold}");
                break;
            case AlarmReason.Unknown:
                RequestAlarm($"sold status unknown for {decision.Unknown} tag(s), onSoldCheckFailure is alarm");
                break;
            case AlarmReason.UnknownButSilent:
                log($"NO ALARM    reader={reader} sold status unknown for {decision.Unknown} tag(s), onSoldCheckFailure is silent");
                break;
            case AlarmReason.AllSold:
                log($"NO ALARM    reader={reader} all {decision.Sold} tag(s) sold");
                break;
        }
    }

    void RequestAlarm(string why)
    {
        bool busy, queued;
        lock (sync)
        {
            busy = outstanding;
            queued = alarmWanted;
            if (busy) coveredWhileOutstanding = true;
            else alarmWanted = true;
        }
        if (busy)
        {
            log($"ALARM BUSY  reader={reader} {why}; an alarm command is still waiting for its reply, so this one is covered by it");
            return;
        }
        log($"GATE ALARM  reader={reader} {why}");
        if (!queued) alarmSignal.Release();
    }

    void Resend(string what)
    {
        log($"GATE ALARM  reader={reader} sending again: an alarm was decided while {what} was waiting for its reply");
        alarmSignal.Release();
    }

    async Task AlarmLoopAsync()
    {
        try
        {
            while (true)
            {
                await alarmSignal.WaitAsync(stopping.Token);
                int mine;
                lock (sync)
                {
                    if (!alarmWanted || outstanding) continue;
                    alarmWanted = false;
                    outstanding = true;
                    coveredWhileOutstanding = false;
                    mine = ++generation;
                    sentAt = Stopwatch.GetTimestamp(); // before the write: the reply can beat WriteAsync's return
                }

                bool sent;
                try { sent = await sendAlarm(stopping.Token); }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    log($"ALARM FAIL  reader={reader} the alarm command could not be sent: {ex.Message}");
                    sent = false;
                }
                if (sent) _ = WatchReplyAsync(mine);
                else lock (sync) if (generation == mine) outstanding = coveredWhileOutstanding = false;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    async Task WatchReplyAsync(int mine)
    {
        try { await Task.Delay(replyTimeout, stopping.Token); }
        catch (OperationCanceledException) { return; }

        bool again;
        lock (sync)
        {
            if (!outstanding || generation != mine) return;
            outstanding = false;
            again = alarmWanted = coveredWhileOutstanding;
            coveredWhileOutstanding = false;
        }
        log($"ALARM FAIL  reader={reader} no reply to the alarm command within {replyTimeout.TotalMilliseconds:0} ms " +
            "(whether the gate sounded is unknown)");
        if (again) Resend("the unanswered command");
    }

    static string Describe(IReadOnlyList<string> epcs) =>
        epcs.Count <= MaxEpcsInLogLine
            ? string.Join(", ", epcs)
            : $"{string.Join(", ", epcs.Take(MaxEpcsInLogLine))} and {epcs.Count - MaxEpcsInLogLine} more";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 1) return;
        stopping.Cancel();
        await Task.WhenAll(checkLoop, alarmLoop);
        int left;
        lock (sync) left = queue.WaitingEpcs;
        if (left > 0) log($"SOLD CHECK  reader={reader} {left} tag(s) not checked: the connection closed");
        stopping.Dispose();
    }
}
