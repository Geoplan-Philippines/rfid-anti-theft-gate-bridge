// Selective physical alarm end to end, in-process: V2GateServer on 127.0.0.1 with the fake SDK, fake gates over real
// TCP, the real HttpSoldStatusClient and AlarmBridge against the fake server. The fake SDK's packets are stand-ins,
// so these tests prove the bridge's logic, not HAX's protocol (SdkSelfCheck on the real DLL does that).
using System.Diagnostics;
using System.Net;
using Xunit.Abstractions;

namespace GateTcpTest.Tests;

public class SelectiveAlarmTests(ITestOutputHelper output)
{
    // EPCs made of decimal digits only, so no byte inside them looks like the start of a fake packet.
    const string A = "E28011700000020000000001";
    const string B = "E28011700000020000000002";
    const string C = "E28011700000020000000003";
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(700);
    static readonly Dictionary<string, string> Gates = new() { ["127.0.0.1"] = "GATE-1", ["127.0.0.2"] = "GATE-2" };
    static readonly byte[] Command = FakeHaxSdk.AlarmCommand();
    static readonly byte[] Accepted = FakeHaxSdk.Reply(FakeHaxSdk.DeviceAlarm);
    static readonly byte[] Refused = FakeHaxSdk.Reply(FakeHaxSdk.DeviceAlarm, 0);

    sealed class Harness : IAsyncDisposable
    {
        public required FakeAlarmServer Server { get; init; }
        public required LogLines Log { get; init; }
        public required AlarmBridge Bridge { get; init; }
        public required V2GateServer V2 { get; init; }
        public required HttpSoldStatusClient Client { get; init; }
        readonly Dictionary<string, int> connects = new();

        public async Task<FakeGate> ConnectAsync(string fromIp = "127.0.0.1")
        {
            string reader = Gates[fromIp];
            FakeGate gate = await FakeGate.ConnectAsync(V2.LocalEndpoint, fromIp);
            Assert.Equal(new byte[] { 0xC8, 1 }, await gate.ServerControlOnlyAsync());
            connects[reader] = connects.GetValueOrDefault(reader) + 1;
            await Log.WaitForContaining($"STEP        reader={reader} server control: ok", Wait, connects[reader]);
            return gate;
        }

        public async ValueTask DisposeAsync()
        {
            V2.Stop(TimeSpan.FromSeconds(2));
            Bridge.Stop(TimeSpan.FromSeconds(1));
            Client.Dispose();
            await Server.DisposeAsync();
        }
    }

    async Task<Harness> StartAsync(TimeSpan? dashboardDelay = null, int soldCheckTimeoutMs = 800,
        SoldCheckFailurePolicy policy = SoldCheckFailurePolicy.Alarm, int replyTimeoutMs = 5000, bool reassemble = false,
        FakeHaxSdk? sdk = null)
    {
        FakeAlarmServer? server = null;
        server = await FakeAlarmServer.StartAsync(dashboardDelay ?? TimeSpan.Zero,
            (_, epc) => server!.SoldEpcs.ContainsKey(epc) ? new Reply(200, "ignored_sold") : Reply.AlarmCreated);
        var log = new LogLines(output);
        BridgeConfig config = server.SelectiveConfig(Gates, soldCheckTimeoutMs, policy, replyTimeoutMs);
        var bridge = new AlarmBridge(config, log.Add);
        var client = new HttpSoldStatusClient(config.SoldCheckUrl!, config.SoldCheckTimeout);
        var v2 = new V2GateServer(sdk ?? new FakeHaxSdk(), config, bridge.OfferForReader, log.Add, client, reassemble);
        v2.Start();
        return new Harness { Server = server, Log = log, Bridge = bridge, V2 = v2, Client = client };
    }

    static async Task ExpectCommandAsync(FakeGate gate) => Assert.Equal(Command, await gate.ReceiveAsync());

    static async Task ExpectNothingAsync(FakeGate gate) => Assert.Null(await gate.TryReceiveAsync(Quiet));

    // ---------------------------------------------------------------- the decision

    [Fact]
    public async Task Selective_mode_sends_no_alarm_command_at_connect()
    {
        await using Harness h = await StartAsync();
        using FakeGate gate = await h.ConnectAsync();

        await ExpectNothingAsync(gate);
        Assert.Equal(0, h.Log.CountContaining("alarm channels"));
    }

    [Fact]
    public async Task All_sold_sends_no_alarm_and_both_tags_still_reach_the_dashboard()
    {
        await using Harness h = await StartAsync();
        h.Server.SoldEpcs[A] = h.Server.SoldEpcs[B] = true;
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.TagPacket(A, B));
        await h.Log.WaitFor("NO ALARM", Wait);
        await ExpectNothingAsync(gate);
        await h.Server.WaitForPosts(2, Wait);

        Assert.Contains("NO ALARM    reader=GATE-1 all 2 tag(s) sold", h.Log.All);
        Assert.Equal([A, B], Assert.Single(h.Server.Checks));
        Assert.Equal([A, B], h.Server.Posts.Select(p => p.Epc).Order());
    }

    [Fact]
    public async Task One_unsold_tag_sends_exactly_one_command_with_the_tested_channels()
    {
        await using Harness h = await StartAsync();
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.TagPacket(A));
        await ExpectCommandAsync(gate);
        await gate.SendAsync(Accepted);
        await h.Log.WaitFor("ALARM OK", Wait);
        await ExpectNothingAsync(gate);

        Assert.Contains($"GATE ALARM  reader=GATE-1 unsold: {A}", h.Log.All);
        Assert.Contains("ALARM CMD   reader=GATE-1 channels [1,1,1,1], 5 bytes: C9 01 01 01 01", h.Log.All);
    }

    [Fact]
    public async Task A_mixed_batch_sends_one_command_for_all_its_unsold_tags()
    {
        await using Harness h = await StartAsync();
        h.Server.SoldEpcs[A] = true;
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.TagPacket(A, B, C));
        await ExpectCommandAsync(gate);
        await gate.SendAsync(Accepted);
        await h.Log.WaitFor("ALARM OK", Wait);
        await ExpectNothingAsync(gate);

        Assert.Contains($"GATE ALARM  reader=GATE-1 unsold: {B}, {C}", h.Log.All);
        Assert.Contains(h.Log.All, l => l.StartsWith("SOLD CHECK  reader=GATE-1 3 tag(s): 1 sold, 2 unsold, 0 unknown", StringComparison.Ordinal));
        await h.Server.WaitForPosts(3, Wait);
    }

    [Fact]
    public async Task Repeated_and_differently_written_epcs_in_one_packet_are_checked_once()
    {
        await using Harness h = await StartAsync();
        h.Server.SoldEpcs[A] = true;
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.RawJsonPacket(
            $$"""[{"ant_id":4,"epc_id":" {{A.ToLowerInvariant()}} "},{"ant_id":1,"epc_id":"{{A}}"},{"ant_id":4,"epc_id":"{{A}}"}]"""));
        await h.Log.WaitFor("NO ALARM", Wait);

        Assert.Equal([A], Assert.Single(h.Server.Checks));
        Assert.Contains("NO ALARM    reader=GATE-1 all 1 tag(s) sold", h.Log.All);
    }

    // ---------------------------------------------------------------- the sold check fails

    public static TheoryData<string> Failures => new() { "429", "500", "503", "not json", "no results", "missing epc", "timeout", "dropped" };

    static CheckAnswer FailureAnswer(string kind) => kind switch
    {
        "429" => new CheckAnswer(429, """{"statusCode":429,"message":"ThrottlerException: Too Many Requests"}"""),
        "500" => new CheckAnswer(500, """{"statusCode":500,"message":"Internal server error"}"""),
        "503" => new CheckAnswer(503, "Service Unavailable"),
        "not json" => new CheckAnswer(200, "<html>proxy error</html>"),
        "no results" => new CheckAnswer(200, """{"statusCode":200,"message":"Success","data":{}}"""),
        "missing epc" => new CheckAnswer(200, """{"statusCode":200,"message":"Success","data":{"results":[]}}"""),
        "timeout" => new CheckAnswer(Delay: TimeSpan.FromSeconds(3)),
        "dropped" => new CheckAnswer(Abort: true),
        _ => throw new ArgumentException(kind),
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_failed_check_sounds_the_gate_logs_the_failure_and_never_calls_the_tag_unsold(string kind)
    {
        await using Harness h = await StartAsync(soldCheckTimeoutMs: 300);
        h.Server.Check = _ => FailureAnswer(kind);
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.TagPacket(A));
        await ExpectCommandAsync(gate);
        await gate.SendAsync(Accepted);
        await h.Log.WaitFor("ALARM OK", Wait);
        await h.Server.WaitForPosts(1, Wait); // the dashboard report goes on regardless

        Assert.Single(h.Log.All, l => l.StartsWith("CHECK FAIL  reader=GATE-1 ", StringComparison.Ordinal) && l.Contains($"sold status unknown for {A}"));
        Assert.Contains("GATE ALARM  reader=GATE-1 sold status unknown for 1 tag(s), onSoldCheckFailure is alarm", h.Log.All);
        Assert.Contains(h.Log.All, l => l.StartsWith("SOLD CHECK  reader=GATE-1 1 tag(s): 0 sold, 0 unsold, 1 unknown", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Log.All, l => l.Contains("unsold: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_the_silent_policy_a_failed_check_is_logged_and_the_gate_stays_quiet()
    {
        await using Harness h = await StartAsync(policy: SoldCheckFailurePolicy.Silent);
        h.Server.Check = _ => FailureAnswer("503");
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.TagPacket(A));
        await h.Log.WaitFor("NO ALARM", Wait);
        await ExpectNothingAsync(gate);
        Assert.Contains("NO ALARM    reader=GATE-1 sold status unknown for 1 tag(s), onSoldCheckFailure is silent", h.Log.All);
    }

    [Fact]
    public async Task A_tag_whose_epc_the_sdk_could_not_read_counts_as_unknown()
    {
        await using Harness h = await StartAsync();
        h.Server.SoldEpcs[A] = true;
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.RawJsonPacket($$"""[{"ant_id":4,"epc_id":"{{A}}"},{"ant_id":4}]"""));
        await ExpectCommandAsync(gate);
        Assert.Contains(h.Log.All, l => l.StartsWith("BAD PACKET  reader=GATE-1 tag JSON from the SDK: item 2 of 2 has no epc_id", StringComparison.Ordinal));
        await h.Log.WaitForContaining("GATE ALARM  reader=GATE-1 sold status unknown for 1 tag(s)", Wait);
    }

    // ---------------------------------------------------------------- independence from the dashboard

    [Fact]
    public async Task A_slow_dashboard_and_camera_do_not_delay_the_gate()
    {
        await using Harness h = await StartAsync(dashboardDelay: TimeSpan.FromSeconds(10));
        using FakeGate gate = await h.ConnectAsync();

        var timer = Stopwatch.StartNew();
        await gate.SendAsync(FakeHaxSdk.TagPacket(A));
        await ExpectCommandAsync(gate);
        timer.Stop();
        await h.Server.WaitForPosts(1, Wait); // the dashboard post is in flight, answering only after 10 s

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"the command took {timer.Elapsed}");
        Assert.Equal(0, h.Server.Answered);
    }

    [Fact]
    public async Task Every_pass_inside_the_dashboard_cooldown_sounds_the_gate_again_but_posts_once()
    {
        await using Harness h = await StartAsync();
        using FakeGate gate = await h.ConnectAsync();

        for (int pass = 1; pass <= 3; pass++)
        {
            await gate.SendAsync(FakeHaxSdk.TagPacket(B));
            await ExpectCommandAsync(gate);
            await gate.SendAsync(Accepted);
            await h.Log.WaitFor("ALARM OK", Wait, count: pass);
        }
        await h.Server.WaitForPosts(1, Wait);
        await Task.Delay(300);

        Assert.Equal(3, h.Server.Checks.Count);
        Assert.Single(h.Server.Posts); // the 30 s dashboard cooldown, untouched
    }

    [Fact]
    public async Task Only_the_gate_that_read_the_unsold_tag_sounds()
    {
        await using Harness h = await StartAsync();
        h.Server.SoldEpcs[A] = true;
        using FakeGate gate1 = await h.ConnectAsync("127.0.0.1");
        using FakeGate gate2 = await h.ConnectAsync("127.0.0.2");

        await gate2.SendAsync(FakeHaxSdk.TagPacket(B));
        await ExpectCommandAsync(gate2);
        await gate1.SendAsync(FakeHaxSdk.TagPacket(A));
        await h.Log.WaitForContaining("NO ALARM    reader=GATE-1", Wait);

        await ExpectNothingAsync(gate1);
        Assert.Contains($"GATE ALARM  reader=GATE-2 unsold: {B}", h.Log.All);
        Assert.DoesNotContain(h.Log.All, l => l.StartsWith("ALARM CMD   reader=GATE-1", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the gate's reply

    [Fact]
    public async Task A_refused_command_is_logged_and_the_next_unsold_read_tries_again()
    {
        await using Harness h = await StartAsync();
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.TagPacket(A));
        await ExpectCommandAsync(gate);
        await gate.SendAsync(Refused);
        await h.Log.WaitFor("ALARM FAIL", Wait);
        Assert.StartsWith("ALARM FAIL  reader=GATE-1 the gate refused the alarm command: device flag=0 fail, code 10",
            h.Log.All.Single(l => l.StartsWith("ALARM FAIL", StringComparison.Ordinal)));

        await gate.SendAsync(FakeHaxSdk.TagPacket(A));
        await ExpectCommandAsync(gate);
        Assert.Equal(0, h.Log.Count("GATE CLOSED"));
    }

    [Fact]
    public async Task A_missing_reply_times_out_the_connection_stays_open_and_a_late_reply_is_named()
    {
        await using Harness h = await StartAsync(replyTimeoutMs: 300);
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.TagPacket(A));
        await ExpectCommandAsync(gate);
        await h.Log.WaitFor("ALARM FAIL", Wait);
        Assert.Contains("ALARM FAIL  reader=GATE-1 no reply to the alarm command within 300 ms (whether the gate sounded is unknown)", h.Log.All);

        await gate.SendAsync(Accepted); // too late
        await h.Log.WaitFor("ALARM LATE", Wait);

        await gate.SendAsync(FakeHaxSdk.TagPacket(A));
        await ExpectCommandAsync(gate);
        Assert.Equal(0, h.Log.Count("GATE CLOSED"));
    }

    [Fact]
    public async Task Tags_that_arrive_before_the_reply_are_handled_and_an_alarm_decided_meanwhile_is_covered()
    {
        await using Harness h = await StartAsync();
        h.Server.SoldEpcs[A] = true;
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.TagPacket(B));
        await ExpectCommandAsync(gate);
        await gate.SendAsync(FakeHaxSdk.TagPacket(A));           // a sold tag before the reply
        await h.Log.WaitForContaining("NO ALARM    reader=GATE-1 all 1 tag(s) sold", Wait);
        await gate.SendAsync(FakeHaxSdk.TagPacket(B));           // and the unsold one again
        await h.Log.WaitFor("ALARM BUSY", Wait);
        await gate.SendAsync(Accepted);
        await h.Log.WaitFor("ALARM OK", Wait);

        await ExpectNothingAsync(gate);                            // the accepted command covered the second decision
        Assert.Equal(2, h.Log.CountContaining($"TAG         reader=GATE-1 ant=4 epc={B}"));
        Assert.Equal(1, h.Log.CountContaining($"TAG         reader=GATE-1 ant=4 epc={A}"));
    }

    [Fact]
    public async Task A_decision_covered_by_a_command_that_then_fails_is_sent_once_more()
    {
        await using Harness h = await StartAsync();
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.TagPacket(B));
        await ExpectCommandAsync(gate);
        await gate.SendAsync(FakeHaxSdk.TagPacket(B));
        await h.Log.WaitFor("ALARM BUSY", Wait);
        await gate.SendAsync(Refused);

        await ExpectCommandAsync(gate);
        await gate.SendAsync(Accepted);
        await h.Log.WaitFor("ALARM OK", Wait);
        await ExpectNothingAsync(gate);
    }

    // ---------------------------------------------------------------- TCP reads that are not packets

    [Fact]
    public async Task With_verified_boundaries_a_split_packet_and_a_reply_joined_to_a_tag_packet_are_both_handled()
    {
        await using Harness h = await StartAsync(reassemble: true);
        h.Server.SoldEpcs[A] = true;
        using FakeGate gate = await h.ConnectAsync();

        byte[] tag = FakeHaxSdk.TagPacket(B);
        await gate.SendAsync(tag[..6]);
        await Task.Delay(100);
        await gate.SendAsync(tag[6..]);
        await ExpectCommandAsync(gate);

        await gate.SendAsync([.. Accepted, .. FakeHaxSdk.TagPacket(A)]); // reply and the next tag packet in one write
        await h.Log.WaitFor("ALARM OK", Wait);
        await h.Log.WaitForContaining("NO ALARM    reader=GATE-1 all 1 tag(s) sold", Wait);

        Assert.Single(h.Server.Checks, c => c.SequenceEqual([B]));
        Assert.Equal(0, h.Log.Count("BAD PACKET"));
    }

    [Fact]
    public async Task With_an_sdk_that_ignores_bytes_after_a_packet_a_tag_packet_joined_to_the_reply_is_still_read()
    {
        await using Harness h = await StartAsync(reassemble: true, sdk: new FakeHaxSdk(lenient: true));
        h.Server.SoldEpcs[A] = true;
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.TagPacket(B));
        await ExpectCommandAsync(gate);
        await gate.SendAsync([.. FakeHaxSdk.TagPacket(A), .. Accepted]); // the next tag packet, then the reply, in one write
        await h.Log.WaitFor("ALARM OK", Wait);
        await h.Log.WaitForContaining("NO ALARM    reader=GATE-1 all 1 tag(s) sold", Wait);
        Assert.Equal(0, h.Log.Count("BAD PACKET"));
    }

    [Fact]
    public async Task Without_verified_boundaries_a_joined_read_is_reported_not_guessed()
    {
        await using Harness h = await StartAsync(replyTimeoutMs: 500, reassemble: false);
        h.Server.SoldEpcs[A] = true;
        using FakeGate gate = await h.ConnectAsync();

        await gate.SendAsync(FakeHaxSdk.TagPacket(B));
        await ExpectCommandAsync(gate);
        await gate.SendAsync([.. Accepted, .. FakeHaxSdk.TagPacket(A)]);
        await h.Log.WaitFor("BAD PACKET", Wait);
        await h.Log.WaitFor("ALARM FAIL", Wait); // the reply inside it was not taken: the command ends unconfirmed

        Assert.Equal(0, h.Log.Count("ALARM OK"));
        Assert.Equal(0, h.Log.CountContaining($"epc={A}"));
    }

    // ---------------------------------------------------------------- bounds

    [Fact]
    public async Task When_the_check_queue_is_full_the_rest_is_decided_unknown_never_dropped()
    {
        await using Harness h = await StartAsync(soldCheckTimeoutMs: 5000, reassemble: true);
        h.Server.Check = _ => new CheckAnswer(Delay: TimeSpan.FromSeconds(1)); // every tag sold, slowly
        using FakeGate gate = await h.ConnectAsync();
        for (int packet = 0; packet < 3; packet++)
        {
            string[] epcs = Enumerable.Range(packet * 200, 200).Select(i => $"E2801170000002{i:D10}").ToArray();
            foreach (string e in epcs) h.Server.SoldEpcs[e] = true;
            await gate.SendAsync(FakeHaxSdk.TagPacket(epcs));
            await h.Log.WaitForContaining($"epc=E2801170000002{packet * 200 + 199:D10}", Wait);
        }

        await ExpectCommandAsync(gate);
        await h.Log.WaitFor("QUEUE FULL", Wait);
        Assert.StartsWith($"QUEUE FULL  reader=GATE-1 {GateAlarmCoordinator.MaxQueuedChecks} sold checks already waiting; sold status unknown for ",
            h.Log.All.First(l => l.StartsWith("QUEUE FULL", StringComparison.Ordinal)));
        Assert.Contains(h.Log.All, l => l.StartsWith("GATE ALARM  reader=GATE-1 sold status unknown for ", StringComparison.Ordinal));
        Assert.All(h.Server.Checks, c => Assert.True(c.Length <= HttpSoldStatusClient.MaxEpcsPerCheck));
    }

    // ---------------------------------------------------------------- mode off stays as before

    [Fact]
    public async Task Mode_off_sets_the_alarm_channels_at_connect_and_never_checks_or_sends_per_tag()
    {
        await using var server = await FakeAlarmServer.StartAsync(TimeSpan.Zero);
        var log = new LogLines(output);
        BridgeConfig config = server.SelectiveConfig(Gates);
        config = new BridgeConfig
        {
            AlarmUrl = config.AlarmUrl, SoldCheckUrl = config.SoldCheckUrl, Gates = Gates, SdkMode = SdkMode.V2,
            ListenIp = IPAddress.Loopback, ListenPort = 0, PhysicalAlarm = PhysicalAlarmMode.Off,
        };
        var bridge = new AlarmBridge(config, log.Add);
        using var v2 = new V2GateServer(new FakeHaxSdk(), config, bridge.OfferForReader, log.Add);
        v2.Start();
        using FakeGate gate = await FakeGate.ConnectAsync(v2.LocalEndpoint);

        (byte[] control, byte[] alarm) = await gate.HandshakeAsync();
        await log.WaitForContaining("alarm channels: ok", Wait);
        await gate.SendAsync(FakeHaxSdk.TagPacket(A));
        await server.WaitForPosts(1, Wait);
        await ExpectNothingAsync(gate);
        bridge.Stop(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 0xC8, 1 }, control);
        Assert.Equal(Command, alarm);
        Assert.Empty(server.Checks);
        Assert.Equal(0, log.Count("ALARM CMD"));
    }
}
