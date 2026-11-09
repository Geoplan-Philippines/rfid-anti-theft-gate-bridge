// v2 mode: this app owns the TCP listener and every gate socket; card_7k.dll only builds and parses packets
// (IHaxSdk). Follows HAX's main.cpp, except that every gate gets its own task, so several can connect at once.
// Per gate: 1. take server control, 2. (physicalAlarm "off" only) set the alarm channels once, as before,
// 3. read the socket with one reader (PacketAssembler, GateStream.cs): every tag goes to the dashboard bridge (onTag),
// and in selective mode also to the gate's GateAlarmCoordinator, which decides per batch whether the gate sounds.
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
    // How long bytes the SDK rejected wait for the rest of their packet (SDK-validated reassembly only).
    public static readonly TimeSpan FragmentWait = TimeSpan.FromMilliseconds(500);

    readonly IHaxSdk sdk;
    readonly IPEndPoint listenOn;
    readonly DeviceAlarmConfig alarmChannels;
    readonly TimeSpan replyTimeout;
    readonly IReadOnlyDictionary<string, string> gates;
    readonly Action<string, string> onTag; // (readerName, epc), never blocks
    readonly Action<string> log;
    readonly PhysicalAlarmMode physicalAlarm;
    readonly SoldCheckFailurePolicy onSoldCheckFailure;
    readonly ISoldStatusClient? soldCheck;
    readonly bool reassemble;
    readonly CancellationTokenSource stopping = new();
    readonly ConcurrentDictionary<int, TcpClient> connections = new();
    readonly ConcurrentDictionary<int, Task> gateTasks = new();
    TcpListener? listener;
    Task acceptLoop = Task.CompletedTask;
    int lastId;
    int stopped;

    // soldCheck is required when config.PhysicalAlarm is Selective. reassemble: SDK-validated reassembly of split and
    // joined TCP reads (GateStream.cs); only true when SdkSelfCheck verified the loaded DLL.
    public V2GateServer(IHaxSdk sdk, BridgeConfig config, Action<string, string> onTag, Action<string> log,
        ISoldStatusClient? soldCheck = null, bool reassemble = false)
    {
        this.sdk = sdk;
        listenOn = new IPEndPoint(config.ListenIp, config.ListenPort);
        alarmChannels = DeviceAlarmConfig.From(config.AlarmChannels);
        replyTimeout = config.ReplyTimeout;
        gates = config.Gates;
        this.onTag = onTag;
        this.log = log;
        physicalAlarm = config.PhysicalAlarm;
        onSoldCheckFailure = config.OnSoldCheckFailure;
        this.soldCheck = soldCheck;
        this.reassemble = reassemble;
        if (physicalAlarm == PhysicalAlarmMode.Selective && soldCheck == null)
            throw new ArgumentException("selective physical alarm needs a sold check client", nameof(soldCheck));
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

            // Selective mode sends no alarm command at connect: the gate only sounds for an unsold (or unknown) tag.
            if (!await StepAsync(stream, reader, "server control",
                    (Span<byte> buf, out int len) => sdk.PackServerControl(1, buf, out len), sdk.UnPackServerControl)
                || (physicalAlarm == PhysicalAlarmMode.Off && !await StepAsync(stream, reader, "alarm channels",
                    (Span<byte> buf, out int len) => sdk.PackDeviceAlarm(alarmChannels, buf, out len), sdk.UnPackDeviceAlarm)))
            {
                log($"GATE CLOSED reader={reader} remote={remote} closed by this app after the failed step");
                return;
            }

            GateAlarmCoordinator? alarms = physicalAlarm == PhysicalAlarmMode.Selective
                ? new GateAlarmCoordinator(reader, soldCheck!, onSoldCheckFailure, replyTimeout,
                    cancel => SendAlarmAsync(stream, reader, cancel), log)
                : null;
            try { await ReadLoopAsync(stream, reader, remote, alarms); }
            finally
            {
                if (alarms != null) await alarms.DisposeAsync();
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

    // The only reader of this socket. Bytes the SDK rejected may wait up to FragmentWait for the rest of their packet;
    // the pending read is kept (never cancelled) while waiting, so no byte is lost.
    async Task ReadLoopAsync(NetworkStream stream, string reader, string remote, GateAlarmCoordinator? alarms)
    {
        var assembler = new PacketAssembler(sdk, reassemble);
        var data = new byte[TagBufferSize];
        Task<int>? read = null;
        while (true)
        {
            read ??= stream.ReadAsync(data, stopping.Token).AsTask();
            if (assembler.HasPending && await Task.WhenAny(read, Task.Delay(FragmentWait, stopping.Token)) != read)
            {
                if (assembler.Flush() is GateMessage stale) Handle(reader, stale, alarms);
                continue;
            }

            int n = await read;
            read = null;
            if (n == 0)
            {
                if (assembler.Flush() is GateMessage rest) Handle(reader, rest, alarms);
                log($"GATE CLOSED reader={reader} remote={remote} the gate disconnected");
                return;
            }
            foreach (GateMessage message in assembler.Feed(data.AsSpan(0, n), alarms?.AlarmOutstanding ?? false))
                Handle(reader, message, alarms);
        }
    }

    // A message the SDK rejects is printed and skipped; the connection stays open.
    void Handle(string reader, GateMessage message, GateAlarmCoordinator? alarms)
    {
        switch (message)
        {
            case TagMessage tags:
                HandleTags(reader, tags, alarms);
                break;
            case AlarmReplyMessage reply when alarms != null:
                alarms.OnReply(reply.Code);
                break;
            case AlarmReplyMessage reply:
                log($"BAD PACKET  reader={reader} an alarm reply ({HaxError.Text(reply.Code)}, code {reply.Code}) but no alarm command " +
                    $"was sent, {reply.Bytes.Length} bytes: {Hex(reply.Bytes, MaxHexBytes)} (connection stays open)");
                break;
            case UnreadableMessage bad:
                log($"BAD PACKET  reader={reader} {HaxError.Text(bad.Code)}, code {bad.Code}, {bad.Bytes.Length} bytes: " +
                    $"{Hex(bad.Bytes, MaxHexBytes)} (connection stays open)");
                break;
        }
    }

    // Every tag goes to the dashboard bridge whatever the physical decision; in selective mode also to the coordinator.
    void HandleTags(string reader, TagMessage packet, GateAlarmCoordinator? alarms)
    {
        if (packet.Problem != null)
            log($"BAD PACKET  reader={reader} tag JSON from the SDK: {packet.Problem}: {Shorten(packet.Json)} (connection stays open)");
        foreach (EpcTag tag in packet.Tags)
        {
            log($"TAG         reader={reader} ant={tag.AntId?.ToString() ?? "?"} epc={tag.Epc}");
            try { onTag(reader, tag.Epc); }
            catch (Exception ex) { log($"ERROR       reader={reader} epc={tag.Epc} not handed to the bridge: {ex.Message}"); }
        }
        if (alarms != null && (packet.Tags.Count > 0 || packet.UnreadableTags > 0))
            alarms.OfferTags(packet.Tags.Select(t => t.Epc).Distinct(StringComparer.Ordinal).ToList(), packet.UnreadableTags);
    }

    // The single writer after setup: called by the gate's coordinator, one command at a time.
    async Task<bool> SendAlarmAsync(NetworkStream stream, string reader, CancellationToken cancel)
    {
        var packet = new byte[PacketBufferSize];
        int rc = sdk.PackDeviceAlarm(alarmChannels, packet, out int len);
        if (rc != HaxError.Ok)
        {
            log($"ALARM FAIL  reader={reader} alarm command not sent: {HaxError.Text(rc)} while building it, code {rc}");
            return false;
        }
        if (len <= 0 || len > packet.Length)
        {
            log($"ALARM FAIL  reader={reader} alarm command not sent: the SDK built {len} bytes into a {packet.Length}-byte buffer");
            return false;
        }
        log($"ALARM CMD   reader={reader} channels {alarmChannels}, {len} bytes: {Hex(packet.AsSpan(0, len))}");
        await stream.WriteAsync(packet.AsMemory(0, len), cancel);
        return true;
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
