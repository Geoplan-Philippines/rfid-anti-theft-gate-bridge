// v2 mode in-process: the app's V2GateServer on 127.0.0.1 with the fake SDK, fake gates connecting over real TCP,
// and (where tags must reach it) the real AlarmBridge posting to the fake alarm server.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Xunit.Abstractions;

namespace GateTcpTest.Tests;

public class V2GateServerTests(ITestOutputHelper output)
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    static readonly Dictionary<string, string> Gates = new() { ["127.0.0.1"] = "GATE-1", ["127.0.0.2"] = "GATE-2" };

    static V2GateServer StartV2(LogLines log, Action<string, string> onTag, byte[]? channels = null, int replyTimeoutMs = 5000)
    {
        var server = new V2GateServer(new FakeHaxSdk(), new BridgeConfig
        {
            Gates = Gates,
            ListenIp = IPAddress.Loopback,
            ListenPort = 0, // any free port
            AlarmChannels = channels ?? new byte[] { 1, 1, 1, 1 },
            ReplyTimeout = TimeSpan.FromMilliseconds(replyTimeoutMs),
        }, onTag, log.Add);
        server.Start();
        return server;
    }

    // One recv is one packet, so like a real gate this waits until the app has handled a packet before sending the next.
    static async Task<FakeGate> ConnectReadyAsync(V2GateServer v2, LogLines log, string fromIp, string reader, int times = 1)
    {
        FakeGate gate = await FakeGate.ConnectAsync(v2.LocalEndpoint, fromIp);
        await gate.HandshakeAsync();
        await log.WaitForContaining($"STEP        reader={reader} alarm channels: ok", Wait, times);
        return gate;
    }

    // The app prints the TAG line, then hands the tag on: wait for the hand-off (onTag) separately where it matters.
    static async Task SendTagAsync(FakeGate gate, LogLines log, string reader, string epc)
    {
        string line = $"TAG         reader={reader} ant=1 epc={epc}";
        int before = log.CountContaining(line);
        await gate.SendAsync(FakeHaxSdk.TagPacket((1, epc)));
        await log.WaitForContaining(line, Wait, before + 1);
    }

    static async Task Eventually(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Gate_answers_both_steps_and_its_3_tag_packets_reach_the_bridge_queue()
    {
        await using var alarms = await FakeAlarmServer.StartAsync(TimeSpan.Zero);
        var log = new LogLines(output);
        var bridge = new AlarmBridge(alarms.Config(Gates), log.Add);
        int offered = 0;
        using V2GateServer v2 = StartV2(log, (reader, epc) =>
        {
            bridge.OfferForReader(reader, epc);
            Interlocked.Increment(ref offered);
        }, channels: new byte[] { 1, 0, 1, 1 });
        using FakeGate gate = await FakeGate.ConnectAsync(v2.LocalEndpoint);

        (byte[] control, byte[] alarm) = await gate.HandshakeAsync();
        await log.WaitForContaining("alarm channels: ok", Wait);
        string[] epcs = { "E28011700000020A1B2C3D01", "E28011700000020A1B2C3D02", "E28011700000020A1B2C3D03" };
        foreach (string epc in epcs) await SendTagAsync(gate, log, "GATE-1", epc);
        await alarms.WaitForPosts(3, Wait);
        await log.WaitFor("ALARM SENT", Wait, count: 3);

        await SendTagAsync(gate, log, "GATE-1", epcs[0]); // still in range: the bridge's cooldown holds it back
        await Eventually(() => Volatile.Read(ref offered) == 4, "4th tag not offered");
        bridge.Stop(TimeSpan.FromSeconds(3));

        Assert.Equal(new byte[] { 0xC8, 1 }, control);          // exactly what Device_PackServerControl(1, ...) built
        Assert.Equal(new byte[] { 0xC9, 1, 0, 1, 1 }, alarm);   // Device_PackDeviceAlarm with alarmChannels [1,0,1,1]
        Assert.Equal(epcs, alarms.Posts.Select(p => p.Epc));
        Assert.All(alarms.Posts, p => Assert.Equal("GATE-1", p.ReaderName));
        Assert.Equal(3, log.Count("ALARM SENT"));
        Assert.Equal(0, log.Count("GATE CLOSED"));
    }

    [Theory]
    [InlineData(new byte[] { 0xC8, 0 }, "device flag=0 fail, code 10")]
    [InlineData(new byte[] { 0xC8, 3 }, "device flag=3 duplicate, code 13")]
    [InlineData(new byte[] { 0x55, 0xAA, 0x01 }, "unpack failed (header/length/CRC), code 4")]
    public async Task Failing_server_control_reply_closes_the_connection(byte[] reply, string why)
    {
        var log = new LogLines(output);
        using V2GateServer v2 = StartV2(log, (_, _) => { });
        using FakeGate gate = await FakeGate.ConnectAsync(v2.LocalEndpoint);

        await gate.ReceiveAsync();
        await gate.SendAsync(reply);

        Assert.Empty(await gate.ReceiveAsync()); // closed; no alarm channels command came first
        await log.WaitFor("GATE CLOSED", Wait);
        Assert.Contains($"STEP FAILED reader=GATE-1 server control: {why}", log.All);
        Assert.EndsWith("closed by this app after the failed step", log.All.Last());
        Assert.Equal(0, log.CountContaining("alarm channels"));
    }

    [Fact]
    public async Task Failing_alarm_channels_reply_closes_the_connection()
    {
        var log = new LogLines(output);
        using V2GateServer v2 = StartV2(log, (_, _) => { });
        using FakeGate gate = await FakeGate.ConnectAsync(v2.LocalEndpoint);

        await gate.ReceiveAsync();
        await gate.SendAsync(FakeHaxSdk.Reply(FakeHaxSdk.ServerControl));
        await gate.ReceiveAsync();
        await gate.SendAsync(new byte[] { 0xC8, 1 }); // a reply, but to the wrong command

        Assert.Empty(await gate.ReceiveAsync());
        await log.WaitFor("GATE CLOSED", Wait);
        Assert.Contains("STEP FAILED reader=GATE-1 alarm channels: unpack failed (header/length/CRC), code 4", log.All);
    }

    [Fact]
    public async Task Gate_that_does_not_reply_in_time_is_closed()
    {
        var log = new LogLines(output);
        using V2GateServer v2 = StartV2(log, (_, _) => { }, replyTimeoutMs: 300);
        using FakeGate gate = await FakeGate.ConnectAsync(v2.LocalEndpoint);

        await gate.ReceiveAsync(); // and never answer
        Assert.Empty(await gate.ReceiveAsync());
        Assert.Contains("STEP FAILED reader=GATE-1 server control: no reply within 300 ms", log.All);
    }

    [Fact]
    public async Task Two_gates_connected_at_once_both_work_and_a_slow_one_does_not_hold_up_the_other()
    {
        await using var alarms = await FakeAlarmServer.StartAsync(TimeSpan.Zero);
        var log = new LogLines(output);
        var bridge = new AlarmBridge(alarms.Config(Gates), log.Add);
        using V2GateServer v2 = StartV2(log, bridge.OfferForReader);
        using FakeGate gate1 = await FakeGate.ConnectAsync(v2.LocalEndpoint, "127.0.0.1");
        using FakeGate gate2 = await FakeGate.ConnectAsync(v2.LocalEndpoint, "127.0.0.2");

        Assert.Equal(new byte[] { 0xC8, 1 }, await gate1.ReceiveAsync()); // gate 1 has its command and sits on it
        await gate2.HandshakeAsync();
        await log.WaitForContaining("STEP        reader=GATE-2 alarm channels: ok", Wait);
        await SendTagAsync(gate2, log, "GATE-2", "E28011700000020A1B2C3DB1");
        await alarms.WaitForPosts(1, Wait);

        await gate1.SendAsync(FakeHaxSdk.Reply(FakeHaxSdk.ServerControl)); // now gate 1 catches up
        await gate1.ReceiveAsync();
        await gate1.SendAsync(FakeHaxSdk.Reply(FakeHaxSdk.DeviceAlarm));
        await log.WaitForContaining("STEP        reader=GATE-1 alarm channels: ok", Wait);
        await SendTagAsync(gate1, log, "GATE-1", "E28011700000020A1B2C3DA1");
        await alarms.WaitForPosts(2, Wait); // two gates are two tasks: without this, B2 could overtake A1 in the queue
        await SendTagAsync(gate2, log, "GATE-2", "E28011700000020A1B2C3DB2");
        await alarms.WaitForPosts(3, Wait);
        await log.WaitFor("ALARM SENT", Wait, count: 3);
        bridge.Stop(TimeSpan.FromSeconds(3));

        Assert.Equal(
            new (string, string?)[] { ("E28011700000020A1B2C3DB1", "GATE-2"), ("E28011700000020A1B2C3DA1", "GATE-1"), ("E28011700000020A1B2C3DB2", "GATE-2") },
            alarms.Posts.Select(p => (p.Epc, p.ReaderName)));
        Assert.Equal(2, log.Count("GATE OPEN"));
        Assert.Equal(0, log.Count("GATE CLOSED"));
    }

    [Fact]
    public async Task Gate_that_disconnects_can_connect_again()
    {
        var log = new LogLines(output);
        var tags = new ConcurrentQueue<(string, string)>();
        using V2GateServer v2 = StartV2(log, (reader, epc) => tags.Enqueue((reader, epc)));

        using (FakeGate first = await ConnectReadyAsync(v2, log, "127.0.0.1", "GATE-1"))
            await SendTagAsync(first, log, "GATE-1", "E28011700000020A1B2C3DC1");
        await log.WaitFor("GATE CLOSED", Wait);
        Assert.Matches(@"^GATE CLOSED reader=GATE-1 remote=127\.0\.0\.1:\d+ the gate disconnected$", log.All.Last());

        using FakeGate second = await ConnectReadyAsync(v2, log, "127.0.0.1", "GATE-1", times: 2);
        await SendTagAsync(second, log, "GATE-1", "E28011700000020A1B2C3DC2");
        await Eventually(() => tags.Count == 2, "second tag not handed on");

        Assert.Equal(new[] { ("GATE-1", "E28011700000020A1B2C3DC1"), ("GATE-1", "E28011700000020A1B2C3DC2") }, tags);
    }

    [Fact]
    public async Task Packets_the_sdk_rejects_are_printed_and_the_connection_stays_open()
    {
        var log = new LogLines(output);
        var tags = new ConcurrentQueue<(string, string)>();
        using V2GateServer v2 = StartV2(log, (reader, epc) => tags.Enqueue((reader, epc)));
        using FakeGate gate = await ConnectReadyAsync(v2, log, "127.0.0.1", "GATE-1");

        await gate.SendAsync(new byte[] { 0x00, 0x01, 0x02 });
        await log.WaitFor("BAD PACKET", Wait, count: 1);
        await gate.SendAsync(FakeHaxSdk.CodePacket(HaxError.Network));
        await log.WaitFor("BAD PACKET", Wait, count: 2);
        await gate.SendAsync(FakeHaxSdk.RawJsonPacket("""{"ant_id":1"""));
        await log.WaitFor("BAD PACKET", Wait, count: 3);
        await SendTagAsync(gate, log, "GATE-1", "E28011700000020A1B2C3DD1");
        await Eventually(() => !tags.IsEmpty, "tag not handed on");

        string[] bad = log.All.Where(l => l.StartsWith("BAD PACKET", StringComparison.Ordinal)).ToArray();
        Assert.Equal("BAD PACKET  reader=GATE-1 unpack failed (header/length/CRC), code 4, 3 bytes: 00 01 02 (connection stays open)", bad[0]);
        Assert.Equal("BAD PACKET  reader=GATE-1 EPC verify could not reach the server, code 20, 2 bytes: EE 14 (connection stays open)", bad[1]);
        Assert.StartsWith("BAD PACKET  reader=GATE-1 tag JSON from the SDK: not valid JSON", bad[2]);
        Assert.Equal(new[] { ("GATE-1", "E28011700000020A1B2C3DD1") }, tags);
        Assert.Equal(0, log.Count("GATE CLOSED"));
    }

    [Fact]
    public async Task Stop_closes_the_listener_and_every_gate_socket()
    {
        var log = new LogLines(output);
        using V2GateServer v2 = StartV2(log, (_, _) => { }); // Stop runs once; the Dispose at the end does nothing
        IPEndPoint endpoint = v2.LocalEndpoint;
        using FakeGate reporting = await ConnectReadyAsync(v2, log, "127.0.0.1", "GATE-1"); // in the tag loop
        using FakeGate halfway = await FakeGate.ConnectAsync(endpoint, "127.0.0.2");
        await halfway.ReceiveAsync();                                                       // waiting for its reply

        v2.Stop(TimeSpan.FromSeconds(2));

        Assert.Empty(await reporting.ReceiveAsync());
        Assert.Empty(await halfway.ReceiveAsync());
        await Assert.ThrowsAnyAsync<SocketException>(() => FakeGate.ConnectAsync(endpoint));
        Assert.Equal(2, log.CountContaining("closed, the app is stopping"));
        Assert.Equal("V2 STOPPED  listener closed, 2 gate connection(s) closed", log.All.Last());
    }
}
