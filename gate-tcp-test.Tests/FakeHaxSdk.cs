// Test stand-ins for v2 mode: the SDK (HAX's updated card_7k.dll) and a gate that connects to the app over TCP.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GateTcpTest.Tests;

// Behaves like the five Device_* functions, but its packets are short readable stand-ins, not HAX's real
// (encrypted) bytes:
//   server control command  C8 <value>                     the gate's reply  C8 <flag>
//   alarm channels command  C9 <ch1> <ch2> <ch3> <ch4>      the gate's reply  C9 <flag>
//   tag packet              DB, then per tag: <ant_id> <EPC length> <EPC bytes>
//   test-only tag packets   DE <UTF-8 text>  UnPackEpcId hands that text back as the JSON, as it is
//                           EE <code>        UnPackEpcId returns that code
// Reply flags unpack like the real ones: 1 -> ok, 0 -> 10, 2 -> 12, 3 -> 13, any other -> 14. A reply of the wrong
// command or length -> 4 (unpack failed). Thread-safe: several gates call it at once.
sealed class FakeHaxSdk : IHaxSdk
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
        if (packet.Length == 2 && packet[0] == Code) return packet[1];
        string? json = packet.Length == 0 ? null : packet[0] switch
        {
            Tags => DecodeTags(packet[1..]),
            RawJson => Encoding.UTF8.GetString(packet[1..]),
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

    public static byte[] TagPacket(params (byte Ant, string Epc)[] tags) =>
        [Tags, .. tags.SelectMany(t => new[] { t.Ant, (byte)(t.Epc.Length / 2) }.Concat(Convert.FromHexString(t.Epc)))];

    public static byte[] RawJsonPacket(string text) => [RawJson, .. Encoding.UTF8.GetBytes(text)];

    public static byte[] CodePacket(byte code) => [Code, code];

    static int Pack(byte[] packet, Span<byte> outBuf, out int outLen)
    {
        outLen = 0;
        if (outBuf.Length < packet.Length) return HaxError.BufferTooSmall;
        packet.CopyTo(outBuf);
        outLen = packet.Length;
        return HaxError.Ok;
    }

    static int UnpackFlag(byte command, ReadOnlySpan<byte> reply) =>
        reply.Length != 2 || reply[0] != command ? HaxError.UnpackFailed : reply[1] switch
        {
            1 => HaxError.Ok,
            0 => HaxError.FlagFail,
            2 => HaxError.FlagAbnormal,
            3 => HaxError.FlagDuplicate,
            _ => HaxError.FlagUnknown,
        };

    static string? DecodeTags(ReadOnlySpan<byte> p)
    {
        var tags = new List<object>();
        while (p.Length > 0)
        {
            if (p.Length < 2 || p.Length < 2 + p[1]) return null;
            tags.Add(new { ant_id = (int)p[0], epc_id = Convert.ToHexString(p.Slice(2, p[1])) });
            p = p[(2 + p[1])..];
        }
        return JsonSerializer.Serialize(tags);
    }
}

// A gate as v2 mode sees it: a TCP client that connects to the app, answers its two commands, then sends tag packets.
sealed class FakeGate : IDisposable
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    readonly TcpClient client;
    readonly NetworkStream stream;

    FakeGate(TcpClient client)
    {
        this.client = client;
        stream = client.GetStream();
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

    // One recv, like the real gate. Empty when the app closed the connection.
    public async Task<byte[]> ReceiveAsync()
    {
        var buf = new byte[V2GateServer.TagBufferSize];
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            int n = await stream.ReadAsync(buf, cts.Token);
            return buf[..n];
        }
        catch (IOException) { return []; } // reset by the app: closed as well
        catch (OperationCanceledException) { throw new TimeoutException($"the app sent nothing for {Timeout.TotalSeconds} s"); }
    }

    public Task SendAsync(byte[] packet) => stream.WriteAsync(packet).AsTask();

    // Receives the server control command and accepts it, then the same for the alarm channels command.
    public async Task<(byte[] ServerControl, byte[] AlarmChannels)> HandshakeAsync()
    {
        byte[] control = await ReceiveAsync();
        await SendAsync(FakeHaxSdk.Reply(FakeHaxSdk.ServerControl));
        byte[] alarm = await ReceiveAsync();
        await SendAsync(FakeHaxSdk.Reply(FakeHaxSdk.DeviceAlarm));
        return (control, alarm);
    }

    public void Dispose() => client.Dispose();
}
