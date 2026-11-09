// Test stand-ins for v2 mode: the SDK (HAX's updated card_7k.dll) and a gate that connects to the app over TCP.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace GateTcpTest.Tests;

// Behaves like the five Device_* functions, but its packets are short readable stand-ins, not HAX's real
// (encrypted) bytes. Every packet is self-delimiting, the way the real DLL's header/length/CRC check is documented:
//   server control command  C8 <value>                     the gate's reply  C8 <flag>
//   alarm channels command  C9 <ch1> <ch2> <ch3> <ch4>      the gate's reply  C9 <flag>
//   tag packet              DB <count hi> <count lo>, then per tag: <ant_id> <EPC length> <EPC bytes>
//   test-only tag packets   DE <length hi> <length lo> <UTF-8 text>   UnPackEpcId hands that text back as the JSON
//                           EE <code>                                   UnPackEpcId returns that code
// Reply flags unpack like the real ones: 1 -> ok, 0 -> 10, 2 -> 12, 3 -> 13, any other -> 14. A reply of the wrong
// command, or bytes missing or left over -> 4 (unpack failed). Thread-safe: several gates call it at once.
// lenient = true models a DLL that ignores bytes after a valid packet and reads it as alone, as HAX's 2.1.2.0 DLL was
// seen to do with the captured packets. acceptTruncated = true models a DLL that reads a cut-off tag packet as fewer
// tags, which SdkSelfCheck must catch. None of this proves anything about HAX's real DLL: SdkSelfCheck on it does.
sealed class FakeHaxSdk(bool lenient = false, bool acceptTruncated = false) : IHaxSdk
{
    public const byte ServerControl = 0xC8, DeviceAlarm = 0xC9, Tags = 0xDB, RawJson = 0xDE, Code = 0xEE;

    // The jsonBuf size of every UnPackEpcId call, in order.
    public ConcurrentQueue<int> JsonBufferSizes { get; } = new();

    public int PackServerControl(int value, Span<byte> outBuf, out int outLen) =>
        Pack([ServerControl, (byte)value], outBuf, out outLen);

    public int PackDeviceAlarm(DeviceAlarmConfig c, Span<byte> outBuf, out int outLen) =>
        Pack([DeviceAlarm, c.Channel1, c.Channel2, c.Channel3, c.Channel4], outBuf, out outLen);

    public int UnPackServerControl(ReadOnlySpan<byte> reply) => UnpackFlag(ServerControl, reply);

    public int UnPackDeviceAlarm(ReadOnlySpan<byte> reply) => UnpackFlag(DeviceAlarm, reply);

    public int UnPackEpcId(ReadOnlySpan<byte> packet, Span<byte> jsonBuf, out int jsonLen)
    {
        JsonBufferSizes.Enqueue(jsonBuf.Length);
        jsonLen = 0;
        if ((packet.Length == 2 || (lenient && packet.Length > 2)) && packet[0] == Code) return packet[1];
        string? json = packet.Length == 0 ? null : packet[0] switch
        {
            Tags => DecodeTags(packet[1..]),
            RawJson => DecodeText(packet[1..]),
            _ => null,
        };
        if (json == null) return HaxError.UnpackFailed;

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        if (jsonBuf.Length < bytes.Length + 1)
        {
            jsonLen = bytes.Length + 1; // the size needed, NUL included, as the header says
            return HaxError.BufferTooSmall;
        }
        bytes.CopyTo(jsonBuf);
        jsonBuf[bytes.Length] = 0;
        jsonLen = bytes.Length;
        return HaxError.Ok;
    }

    public static byte[] Reply(byte command, byte flag = 1) => [command, flag];

    public static byte[] AlarmCommand(byte c1 = 1, byte c2 = 1, byte c3 = 1, byte c4 = 1) => [DeviceAlarm, c1, c2, c3, c4];

    public static byte[] TagPacket(params (byte Ant, string Epc)[] tags) =>
        [Tags, (byte)(tags.Length >> 8), (byte)tags.Length, .. tags.SelectMany(t => new[] { t.Ant, (byte)(t.Epc.Length / 2) }.Concat(Convert.FromHexString(t.Epc)))];

    public static byte[] TagPacket(params string[] epcs) => TagPacket(epcs.Select(e => ((byte)4, e)).ToArray());

    public static byte[] RawJsonPacket(string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        return [RawJson, (byte)(body.Length >> 8), (byte)body.Length, .. body];
    }

    public static byte[] CodePacket(byte code) => [Code, code];

    // The same checks SdkSelfCheck runs on HAX's captured bytes, on this fake's own packets.
    public static SdkFixtures Fixtures(string epc = "E28069950000AA039398B5EA") => new(
        new DeviceAlarmConfig { Channel1 = 1, Channel2 = 1, Channel3 = 1, Channel4 = 1 },
        AlarmCommand(), Reply(DeviceAlarm), TagPacket(epc), 4, epc);

    static int Pack(byte[] packet, Span<byte> outBuf, out int outLen)
    {
        outLen = 0;
        if (outBuf.Length < packet.Length) return HaxError.BufferTooSmall;
        packet.CopyTo(outBuf);
        outLen = packet.Length;
        return HaxError.Ok;
    }

    int UnpackFlag(byte command, ReadOnlySpan<byte> reply) =>
        (reply.Length != 2 && !(lenient && reply.Length > 2)) || reply[0] != command ? HaxError.UnpackFailed : reply[1] switch
        {
            1 => HaxError.Ok,
            0 => HaxError.FlagFail,
            2 => HaxError.FlagAbnormal,
            3 => HaxError.FlagDuplicate,
            _ => HaxError.FlagUnknown,
        };

    string? DecodeTags(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) return null;
        int count = (p[0] << 8) | p[1];
        p = p[2..];
        var tags = new List<object>();
        for (int i = 0; i < count; i++)
        {
            if (p.Length < 2 || p.Length < 2 + p[1])
            {
                if (acceptTruncated) break;
                return null;
            }
            tags.Add(new { ant_id = (int)p[0], epc_id = Convert.ToHexString(p.Slice(2, p[1])) });
            p = p[(2 + p[1])..];
        }
        if (p.Length > 0 && !lenient) return null; // bytes left over: not one packet
        return JsonSerializer.Serialize(tags);
    }

    string? DecodeText(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) return null;
        int length = (p[0] << 8) | p[1];
        if (p.Length - 2 < length || (p.Length - 2 > length && !lenient)) return null;
        return Encoding.UTF8.GetString(p.Slice(2, length));
    }
}

// A gate as v2 mode sees it: a TCP client that connects to the app, answers its commands, then sends tag packets.
// A background loop reads everything the app sends, so a test can wait for a command with any timeout.
sealed class FakeGate : IDisposable
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    readonly TcpClient client;
    readonly NetworkStream stream;
    readonly Channel<byte[]> received = Channel.CreateUnbounded<byte[]>();

    FakeGate(TcpClient client)
    {
        this.client = client;
        stream = client.GetStream();
        _ = Task.Run(ReceiveLoopAsync);
    }

    // fromIp is the gate's own address. Windows answers on every 127.x.x.x, so two gates can have different IPs.
    public static async Task<FakeGate> ConnectAsync(IPEndPoint app, string fromIp = "127.0.0.1")
    {
        var client = new TcpClient(new IPEndPoint(IPAddress.Parse(fromIp), 0)) { NoDelay = true };
        try { await client.ConnectAsync(app); }
        catch
        {
            client.Dispose();
            throw;
        }
        return new FakeGate(client);
    }

    async Task ReceiveLoopAsync()
    {
        var buf = new byte[V2GateServer.TagBufferSize];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buf);
                if (n == 0) break;
                received.Writer.TryWrite(buf[..n]);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { } // reset or disposed
        received.Writer.TryWrite([]); // closed
        received.Writer.TryComplete();
    }

    // One read, like the real gate. Empty when the app closed the connection.
    public async Task<byte[]> ReceiveAsync() =>
        await TryReceiveAsync(Timeout) ?? throw new TimeoutException($"the app sent nothing for {Timeout.TotalSeconds} s");

    // Null when the app sent nothing within wait.
    public async Task<byte[]?> TryReceiveAsync(TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        try { return await received.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException) { return []; }
    }

    public Task SendAsync(byte[] packet) => stream.WriteAsync(packet).AsTask();

    // Receives the server control command and accepts it, then the same for the alarm channels command (mode off).
    public async Task<(byte[] ServerControl, byte[] AlarmChannels)> HandshakeAsync()
    {
        byte[] control = await ReceiveAsync();
        await SendAsync(FakeHaxSdk.Reply(FakeHaxSdk.ServerControl));
        byte[] alarm = await ReceiveAsync();
        await SendAsync(FakeHaxSdk.Reply(FakeHaxSdk.DeviceAlarm));
        return (control, alarm);
    }

    // Selective mode: only the server control command comes at connect.
    public async Task<byte[]> ServerControlOnlyAsync()
    {
        byte[] control = await ReceiveAsync();
        await SendAsync(FakeHaxSdk.Reply(FakeHaxSdk.ServerControl));
        return control;
    }

    public void Dispose() => client.Dispose();
}
