// v2 mode: this app owns the TCP listener and every gate socket; card_7k.dll only builds and parses packets
// (IHaxSdk). Follows HAX's sdk-v2\main.cpp, except that every gate gets its own task, so several can connect at once.
// Per gate: 1. take server control, 2. set the alarm channels, 3. turn every tag packet into tags for the bridge.
// Only bytes the SDK built are ever sent. Nothing here touches card_7k.dll, so the test project compiles it directly.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GateTcpTest;

delegate int SdkPack(Span<byte> outBuf, out int outLen);
delegate int SdkUnpack(ReadOnlySpan<byte> reply);

sealed class V2GateServer : IDisposable
{
    public const int PacketBufferSize = 256;  // a command we send, and the gate's reply to it: one recv
    public const int TagBufferSize = 10240;   // one tag packet: one recv, there is no frame header or length field
    public const int JsonBufferSize = 10240;  // first try for Device_UnPackEpcId; it says when it needs more
    const int MaxJsonBytes = 16 << 20;        // never allocate more than this, whatever size the SDK asks for
    const int MaxHexBytes = 256;              // longest packet printed in full on a BAD PACKET line

    readonly IHaxSdk sdk;
    readonly IPEndPoint listenOn;
    readonly DeviceAlarmConfig alarmChannels;
    readonly TimeSpan replyTimeout;
    readonly IReadOnlyDictionary<string, string> gates;
    readonly Action<string, string> onTag; // (readerName, epc), never blocks
    readonly Action<string> log;
    readonly CancellationTokenSource stopping = new();
    readonly ConcurrentDictionary<int, TcpClient> connections = new();
    readonly ConcurrentDictionary<int, Task> gateTasks = new();
    TcpListener? listener;
    Task acceptLoop = Task.CompletedTask;
    int lastId;
    int stopped;

    public V2GateServer(IHaxSdk sdk, BridgeConfig config, Action<string, string> onTag, Action<string> log)
    {
        this.sdk = sdk;
        listenOn = new IPEndPoint(config.ListenIp, config.ListenPort);
        alarmChannels = DeviceAlarmConfig.From(config.AlarmChannels);
        replyTimeout = config.ReplyTimeout;
        gates = config.Gates;
        this.onTag = onTag;
        this.log = log;
    }

    // Where it really listens (port 0 in the config means any free port, used by the tests).
    public IPEndPoint LocalEndpoint => (IPEndPoint)(listener ?? throw new InvalidOperationException("not started")).LocalEndpoint;

    // Throws SocketException when the port is taken or listenIp is not an address of this PC.
    public void Start()
    {
        listener = new TcpListener(listenOn);
        listener.Start();
        acceptLoop = AcceptLoopAsync(listener);
    }

    // The Q key: stop listening, close every gate socket, wait up to `wait` for the gate tasks to end.
    public void Stop(TimeSpan wait)
    {
        if (Interlocked.Exchange(ref stopped, 1) == 1) return;
        stopping.Cancel();
        listener?.Stop();
        int open = 0;
        foreach (TcpClient client in connections.Values)
        {
            client.Dispose(); // the gate task's pending read fails, so the task ends
            open++;
        }
        try { Task.WaitAll(gateTasks.Values.Append(acceptLoop).ToArray(), wait); }
        catch (AggregateException) { } // the tasks catch everything themselves; nothing to add here
        log($"V2 STOPPED  listener closed, {open} gate connection(s) closed");
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(2));

    // Same rule as legacy: the gate's IP (never the port, it changes on every reconnect), through the gates map.
    public static string ReaderNameFor(EndPoint? remote, IReadOnlyDictionary<string, string> gates)
    {
        if (remote is not IPEndPoint ep) return remote?.ToString() ?? "unknown";
        IPAddress ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        return AlarmRules.ReaderName(new IPEndPoint(ip, ep.Port).ToString(), gates);
    }

    async Task AcceptLoopAsync(TcpListener server)
    {
        while (true)
        {
            TcpClient client;
            try { client = await server.AcceptTcpClientAsync(stopping.Token); }
            catch (Exception) when (stopping.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                log($"V2 ACCEPT   failed: {ex.Message}"); // e.g. a gate that reset before it was accepted; keep listening
                await Task.Delay(100);
                continue;
            }
            catch (Exception ex)
            {
                log($"V2 ACCEPT   stopped, no new gate can connect until the app is restarted: {ex}");
                return;
            }

            int id = Interlocked.Increment(ref lastId);
            connections[id] = client;
            if (stopping.IsCancellationRequested) // Stop ran between the accept and the line above
            {
                connections.TryRemove(id, out _);
                client.Dispose();
                return;
            }
            Task task = Task.Run(() => RunGateAsync(id, client));
            gateTasks[id] = task;
            _ = task.ContinueWith(t => gateTasks.TryRemove(id, out _), TaskScheduler.Default);
        }
    }

    // One gate, from connect to disconnect. Catches everything: one gate's trouble never reaches another gate.
    async Task RunGateAsync(int id, TcpClient client)
    {
        string remote = "?", reader = "?";
        try
        {
            EndPoint? ep = client.Client.RemoteEndPoint;
            remote = ep?.ToString() ?? "?";
            reader = ReaderNameFor(ep, gates);
            client.NoDelay = true; // each SDK packet goes out as soon as it is written
            log($"GATE OPEN   reader={reader} remote={remote}");
            NetworkStream stream = client.GetStream();

            if (!await StepAsync(stream, reader, "server control",
                    (Span<byte> buf, out int len) => sdk.PackServerControl(1, buf, out len), sdk.UnPackServerControl)
                || !await StepAsync(stream, reader, "alarm channels",
                    (Span<byte> buf, out int len) => sdk.PackDeviceAlarm(alarmChannels, buf, out len), sdk.UnPackDeviceAlarm))
            {
                log($"GATE CLOSED reader={reader} remote={remote} closed by this app after the failed step");
                return;
            }

            var data = new byte[TagBufferSize];
            var json = new byte[JsonBufferSize];
            while (true)
            {
                int n = await stream.ReadAsync(data, stopping.Token);
                if (n == 0)
                {
                    log($"GATE CLOSED reader={reader} remote={remote} the gate disconnected");
                    return;
                }
                HandleTagPacket(reader, data.AsSpan(0, n), json);
            }
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            log($"GATE CLOSED reader={reader} remote={remote} closed, the app is stopping");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            log($"GATE CLOSED reader={reader} remote={remote} connection lost: {ex.Message}");
        }
        catch (Exception ex)
        {
            log($"GATE CLOSED reader={reader} remote={remote} unexpected error: {ex}");
        }
        finally
        {
            connections.TryRemove(id, out _);
            client.Dispose();
        }
    }

    // Send the packet the SDK builds, read one reply (one recv), and let the SDK check it. False closes the gate.
    async Task<bool> StepAsync(NetworkStream stream, string reader, string step, SdkPack pack, SdkUnpack unpack)
    {
        var packet = new byte[PacketBufferSize];
        int rc = pack(packet, out int len);
        if (rc != HaxError.Ok)
            return Failed(reader, step, $"{HaxError.Text(rc)} while building the packet, code {rc}");
        if (len <= 0 || len > packet.Length)
            return Failed(reader, step, $"the SDK built {len} bytes into a {packet.Length}-byte buffer, nothing sent");

        log($"TO GATE     reader={reader} {step}, {len} bytes: {Hex(packet.AsSpan(0, len))}");
        await stream.WriteAsync(packet.AsMemory(0, len), stopping.Token);

        var reply = new byte[PacketBufferSize];
        int n;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
        {
            timeout.CancelAfter(replyTimeout);
            try { n = await stream.ReadAsync(reply, timeout.Token); }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                return Failed(reader, step, $"no reply within {replyTimeout.TotalMilliseconds:0} ms");
            }
        }
        if (n == 0) return Failed(reader, step, "the gate disconnected without replying");

        log($"FROM GATE   reader={reader} {step}, {n} bytes: {Hex(reply.AsSpan(0, n))}");
        rc = unpack(reply.AsSpan(0, n));
        if (rc != HaxError.Ok) return Failed(reader, step, $"{HaxError.Text(rc)}, code {rc}");
        log($"STEP        reader={reader} {step}: ok");
        return true;
    }

    bool Failed(string reader, string step, string why)
    {
        log($"STEP FAILED reader={reader} {step}: {why}");
        return false;
    }

    // One recv = one packet. A packet the SDK rejects is printed and skipped; the connection stays open.
    void HandleTagPacket(string reader, ReadOnlySpan<byte> packet, byte[] jsonBuf)
    {
        int rc = UnpackEpcJson(sdk, packet, jsonBuf, out string json);
        if (rc != HaxError.Ok)
        {
            log($"BAD PACKET  reader={reader} {HaxError.Text(rc)}, code {rc}, {packet.Length} bytes: {Hex(packet, MaxHexBytes)} (connection stays open)");
            return;
        }

        (IReadOnlyList<EpcTag> tags, string? problem) = EpcJson.Parse(json);
        if (problem != null)
            log($"BAD PACKET  reader={reader} tag JSON from the SDK: {problem}: {Shorten(json)} (connection stays open)");
        foreach (EpcTag tag in tags)
        {
            log($"TAG         reader={reader} ant={tag.AntId?.ToString() ?? "?"} epc={tag.Epc}");
            try { onTag(reader, tag.Epc); }
            catch (Exception ex) { log($"ERROR       reader={reader} epc={tag.Epc} not handed to the bridge: {ex.Message}"); }
        }
    }

    // Device_UnPackEpcId into jsonBuf. When the SDK answers "buffer too small", once more into a buffer of the size
    // it asked for (json_len, which counts the NUL), as HAX's readme says.
    public static int UnpackEpcJson(IHaxSdk sdk, ReadOnlySpan<byte> packet, byte[] jsonBuf, out string json)
    {
        json = "";
        int rc = sdk.UnPackEpcId(packet, jsonBuf, out int len);
        if (rc == HaxError.BufferTooSmall && len > jsonBuf.Length && len <= MaxJsonBytes)
        {
            jsonBuf = new byte[len];
            rc = sdk.UnPackEpcId(packet, jsonBuf, out len);
        }
        if (rc != HaxError.Ok) return rc;

        // json_len excludes the NUL. Never read past the buffer or a NUL, whatever length the DLL reports.
        ReadOnlySpan<byte> text = jsonBuf.AsSpan(0, Math.Clamp(len, 0, jsonBuf.Length));
        int nul = text.IndexOf((byte)0);
        json = Encoding.UTF8.GetString(nul >= 0 ? text[..nul] : text);
        return HaxError.Ok;
    }

    // "C8 01 FF", and at most max bytes of it.
    public static string Hex(ReadOnlySpan<byte> bytes, int max = int.MaxValue)
    {
        int shown = Math.Min(bytes.Length, max);
        var sb = new StringBuilder(shown * 3 + 24);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        if (shown < bytes.Length) sb.Append($" ... ({bytes.Length - shown} more bytes)");
        return sb.ToString();
    }

    static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + $"... ({text.Length - 200} more characters)";
}
