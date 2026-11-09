// The sold check on its own: the decision rule, the bounded queue, and the HTTP client against the fake server.
using System.Net;
using System.Net.Sockets;

namespace GateTcpTest.Tests;

public class SoldCheckTests
{
    const string Sold = "E28069950000AA039398B5EA";
    const string Unsold = "3BE100003744AA2700000769";
    static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(800);

    static EpcCheck C(string epc, EpcStatus s) => new(epc, s);

    [Fact]
    public void All_sold_is_silent()
    {
        AlarmDecision d = PhysicalAlarmRules.Decide([C("A", EpcStatus.Sold), C("B", EpcStatus.Sold)], 0, SoldCheckFailurePolicy.Alarm);
        Assert.False(d.Alarm);
        Assert.Equal(AlarmReason.AllSold, d.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Any_unsold_alarms_whatever_the_policy_and_whatever_else_is_in_the_batch(bool alarmOnFailure)
    {
        SoldCheckFailurePolicy policy = alarmOnFailure ? SoldCheckFailurePolicy.Alarm : SoldCheckFailurePolicy.Silent;
        AlarmDecision d = PhysicalAlarmRules.Decide(
            [C("A", EpcStatus.Sold), C("B", EpcStatus.Unsold), C("C", EpcStatus.Unknown)], 1, policy);
        Assert.True(d.Alarm);
        Assert.Equal(AlarmReason.Unsold, d.Reason);
        Assert.Equal((1, 1, 2), (d.Sold, d.Unsold, d.Unknown));
    }

    [Fact]
    public void Unknown_alarms_by_default_and_stays_unknown()
    {
        AlarmDecision d = PhysicalAlarmRules.Decide([C("A", EpcStatus.Sold), C("B", EpcStatus.Unknown)], 0, SoldCheckFailurePolicy.Alarm);
        Assert.True(d.Alarm);
        Assert.Equal(AlarmReason.Unknown, d.Reason);
        Assert.Equal(0, d.Unsold); // a failed check is never counted as unsold
    }

    [Fact]
    public void Unknown_is_silent_only_when_the_policy_says_so()
    {
        AlarmDecision d = PhysicalAlarmRules.Decide([C("B", EpcStatus.Unknown)], 0, SoldCheckFailurePolicy.Silent);
        Assert.False(d.Alarm);
        Assert.Equal(AlarmReason.UnknownButSilent, d.Reason);
    }

    [Fact]
    public void Tags_without_a_readable_epc_count_as_unknown()
    {
        Assert.Equal(AlarmReason.Unknown, PhysicalAlarmRules.Decide([C("A", EpcStatus.Sold)], 1, SoldCheckFailurePolicy.Alarm).Reason);
        Assert.Equal(AlarmReason.None, PhysicalAlarmRules.Decide([], 0, SoldCheckFailurePolicy.Alarm).Reason);
    }

    [Fact]
    public void Queue_merges_repeats_fills_batches_of_the_limit_and_hands_back_what_does_not_fit()
    {
        var q = new LookupQueue(maxBatches: 2, maxEpcs: 3);

        Assert.Empty(q.Add(["A", "B", "A"], 0).Overflow);
        Assert.Empty(q.Add(["B", "C", "D"], 0).Overflow);        // B already waits; C fills batch 1, D opens batch 2
        (IReadOnlyList<string> overflow, bool unreadable) = q.Add(["E", "F", "G", "H"], 1);

        Assert.Equal(["G", "H"], overflow);                        // handed back: the caller decides them as unknown
        Assert.False(unreadable);                                  // unreadable tags need no EPC room: they join batch 2
        Assert.Equal(["A", "B", "C"], q.Take()!.Epcs);
        LookupBatch second = q.Take()!;
        Assert.Equal(["D", "E", "F"], second.Epcs);
        Assert.Equal(1, second.UnreadableTags);
        Assert.Null(q.Take());
        Assert.Empty(q.Add(["A"], 0).Overflow);                    // taken EPCs can be queued again
    }

    [Fact]
    public void Queue_keeps_unreadable_tags_on_a_batch_that_has_room()
    {
        var q = new LookupQueue(maxBatches: 1, maxEpcs: 3);
        Assert.False(q.Add(["A"], 2).UnreadableOverflow);
        Assert.Equal(2, q.Take()!.UnreadableTags);
    }

    [Fact]
    public void Answer_reading_maps_every_requested_epc_and_normalizes()
    {
        string body = """{"statusCode":200,"message":"Success","data":{"results":[{"epc":"E28069950000AA039398B5EA","sold":true},{"epc":"3BE100003744AA2700000769","sold":false}]}}""";

        SoldCheckResult r = HttpSoldStatusClient.Read(["e2806995 0000AA039398B5EA", Unsold], body, TimeSpan.Zero);

        Assert.Null(r.Failure);
        Assert.Equal([EpcStatus.Sold, EpcStatus.Unsold], r.Checks.Select(c => c.Status));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"statusCode":200}""")]
    [InlineData("""{"data":{"results":"nope"}}""")]
    [InlineData("""[1,2]""")]
    public void A_malformed_answer_is_unknown_for_every_tag(string body)
    {
        SoldCheckResult r = HttpSoldStatusClient.Read([Sold, Unsold], body, TimeSpan.Zero);
        Assert.StartsWith("malformed answer", r.Failure);
        Assert.All(r.Checks, c => Assert.Equal(EpcStatus.Unknown, c.Status));
    }

    [Theory]
    [InlineData("""{"data":{"results":[{"epc":"E28069950000AA039398B5EA","sold":true}]}}""")]                          // missing
    [InlineData("""{"data":{"results":[{"epc":"E28069950000AA039398B5EA","sold":true},{"epc":"3BE100003744AA2700000769","sold":"no"}]}}""")]
    [InlineData("""{"data":{"results":[{"epc":"E28069950000AA039398B5EA","sold":true},{"epc":"3BE100003744AA2700000769"}]}}""")]
    [InlineData("""{"data":{"results":[{"epc":"E28069950000AA039398B5EA","sold":true},{"epc":"3BE100003744AA2700000769","sold":false},{"epc":"3BE100003744AA2700000769","sold":true}]}}""")]
    public void A_missing_broken_or_contradicting_result_leaves_that_tag_unknown_never_unsold(string body)
    {
        SoldCheckResult r = HttpSoldStatusClient.Read([Sold, Unsold], body, TimeSpan.Zero);
        Assert.Equal("no usable answer for 1 of 2 EPC(s)", r.Failure);
        Assert.Equal([EpcStatus.Sold, EpcStatus.Unknown], r.Checks.Select(c => c.Status));
    }

    [Fact]
    public async Task Client_sends_the_batch_and_reads_the_servers_answer()
    {
        await using var server = await FakeAlarmServer.StartAsync(TimeSpan.Zero);
        server.SoldEpcs[Sold] = true;
        using var client = new HttpSoldStatusClient(new Uri(server.BaseUrl, BridgeConfig.SoldCheckPath), Timeout);

        SoldCheckResult r = await client.CheckAsync([Sold, Unsold], CancellationToken.None);

        Assert.Null(r.Failure);
        Assert.Equal([C(Sold, EpcStatus.Sold), C(Unsold, EpcStatus.Unsold)], r.Checks);
        Assert.Equal([Sold, Unsold], Assert.Single(server.Checks));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(400)]
    public async Task Client_turns_an_error_status_into_unknown(int status)
    {
        await using var server = await FakeAlarmServer.StartAsync(TimeSpan.Zero);
        server.Check = _ => new CheckAnswer(status, """{"statusCode":0,"message":"busy","error":"x"}""");
        using var client = new HttpSoldStatusClient(new Uri(server.BaseUrl, BridgeConfig.SoldCheckPath), Timeout);

        SoldCheckResult r = await client.CheckAsync([Unsold], CancellationToken.None);

        Assert.Equal($"HTTP {status} busy", r.Failure);
        Assert.Equal(EpcStatus.Unknown, Assert.Single(r.Checks).Status);
    }

    [Fact]
    public async Task Client_gives_up_at_its_timeout_with_unknown()
    {
        await using var server = await FakeAlarmServer.StartAsync(TimeSpan.Zero);
        server.Check = _ => new CheckAnswer(Delay: TimeSpan.FromSeconds(5));
        using var client = new HttpSoldStatusClient(new Uri(server.BaseUrl, BridgeConfig.SoldCheckPath), TimeSpan.FromMilliseconds(200));

        SoldCheckResult r = await client.CheckAsync([Unsold], CancellationToken.None);

        Assert.Equal("no answer within 200 ms", r.Failure);
        Assert.Equal(EpcStatus.Unknown, Assert.Single(r.Checks).Status);
        Assert.True(r.Took < TimeSpan.FromSeconds(2), $"took {r.Took}");
    }

    [Fact]
    public async Task Client_turns_a_dropped_or_refused_connection_into_unknown()
    {
        await using var server = await FakeAlarmServer.StartAsync(TimeSpan.Zero);
        server.Check = _ => new CheckAnswer(Abort: true);
        using var dropped = new HttpSoldStatusClient(new Uri(server.BaseUrl, BridgeConfig.SoldCheckPath), Timeout);
        Assert.Equal(EpcStatus.Unknown, Assert.Single((await dropped.CheckAsync([Unsold], CancellationToken.None)).Checks).Status);

        var unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        int port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        using var refused = new HttpSoldStatusClient(new Uri($"http://127.0.0.1:{port}{BridgeConfig.SoldCheckPath}"), Timeout);
        SoldCheckResult r = await refused.CheckAsync([Unsold], CancellationToken.None);
        Assert.NotNull(r.Failure);
        Assert.Equal(EpcStatus.Unknown, Assert.Single(r.Checks).Status);
    }
}
