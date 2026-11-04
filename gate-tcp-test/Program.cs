// gate-tcp-test: hosts the card_7k.dll TCP server for the RFID7000 gate and logs every callback.
// With a gate-bridge.json next to the exe it also sends every tag to the anti-theft server (see GateBridge.cs).
// API source of truth: ..\hax_7k_adapt.h (32-bit only, #pragma pack(1), default cdecl calling convention).
// "sdkMode": "v2" in gate-bridge.json switches to HAX's packet interface instead: this app runs the TCP server
// itself and the DLL only builds and parses packets (RunV2, V2GateServer.cs, sdk-v2\hax_7k_sdk.h).
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace GateTcpTest;

enum HaxErrorCode { hax_success = 0, hax_failed, hax_param_error, hax_dev_error }

// Mirrors of the header structs. Pack = 1 matches #pragma pack(1); byte sizes in the comments.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct RfidInfo // rfid_info_st, 32
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public byte[] SerialNum;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public byte[] Version;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct EpcId // epc_id_st, 18
{
    public ushort Len;
    public ushort Pc;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public byte[] Id;
    public ushort Crc;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct EpcIdList // epc_id_list, 612
{
    public RfidInfo Sn;
    public int Size;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public EpcId[] Epc;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct Callbacks // IAsynchronousMessage, 12
{
    public IntPtr PortConnecting;
    public IntPtr PortClosing;
    public IntPtr OutputTags;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct Antenna // antenna_st, 6
{
    public byte Num;
    [MarshalAs(UnmanagedType.U1)] public bool IsEnable; // C++ bool is 1 byte, not the 4-byte Win32 BOOL
    public ushort ReadPower;
    public ushort WritePower;
}

// hax_7k_config_st, 64 bytes (0x40). This is the WHOLE public config surface; it has no beep field,
// and ends 197 bytes before the board's beep byte at blob offset 0x105. See TryBeep for the consequence.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct Hax7kConfig
{
    public byte WorkFreq;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public Antenna[] Antenna;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)] public byte[] Clock;
    public RfidInfo Sn;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void PortCallback(IntPtr connId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void TagsCallback(IntPtr connId, EpcIdList data); // the header passes epc_id_list by value

static class Native
{
    const string Dll = "card_7k.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern HaxErrorCode open_tcp_server([MarshalAs(UnmanagedType.LPStr)] string ip, uint port, Callbacks callbacks);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void close_tcp_server();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void start_auto_read(int timeMs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void stop_auto_read();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern HaxErrorCode enable_alarm([MarshalAs(UnmanagedType.LPStr)] string connId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern HaxErrorCode get_config([MarshalAs(UnmanagedType.LPStr)] string connId, ref Hax7kConfig config);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern HaxErrorCode set_config([MarshalAs(UnmanagedType.LPStr)] string connId, ref Hax7kConfig config);
}

static class Program
{
    // card_7k.dll keeps raw pointers to these delegates; static fields keep them alive for the whole run.
    static readonly PortCallback OnConnecting = PortConnecting;
    static readonly PortCallback OnClosing = PortClosing;
    static readonly TagsCallback OnTags = OutputTags;

    static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "test.log");
    static readonly object Sync = new();
    static readonly Dictionary<string, string> Seen = new(); // connID -> "connected" / "disconnected"
    static string? lastConnId;
    static bool logFileFailed;
    static AlarmBridge? bridge; // null = log-only, no gate-bridge.json or no serverUrl

    static int Main(string[] args)
    {
        string ip = args.Length > 0 ? args[0] : "0.0.0.0";
        uint port = 9090;
        if (!IPAddress.TryParse(ip, out _) || (args.Length > 1 && (!uint.TryParse(args[1], out port) || port is 0 or > 65535)))
        {
            Console.WriteLine("Usage: gate-tcp-test [listen-ip] [port]   (defaults: 0.0.0.0 9090)");
            return 2;
        }

        Log($"gate-tcp-test starting, {(Environment.Is64BitProcess ? 64 : 32)}-bit process, log file {LogPath}");
        BridgeConfig config;
        try { config = BridgeConfig.Load(Path.Combine(AppContext.BaseDirectory, BridgeConfig.FileName), problem => Log($"WARNING: {problem}")); }
        catch (ConfigException ex)
        {
            Log($"ERROR: {BridgeConfig.FileName}: {ex.Message}");
            PauseIfInteractive();
            return 2;
        }
        StartBridge(config);
        if (config.SdkMode == SdkMode.V2) return RunV2(config, argsGiven: args.Length > 0);

        if (!LoadSdk(out _))
        {
            PauseIfInteractive();
            return 1;
        }

        var callbacks = new Callbacks
        {
            PortConnecting = Marshal.GetFunctionPointerForDelegate(OnConnecting),
            PortClosing = Marshal.GetFunctionPointerForDelegate(OnClosing),
            OutputTags = Marshal.GetFunctionPointerForDelegate(OnTags),
        };
        HaxErrorCode rc = Native.open_tcp_server(ip, port, callbacks);
        Log($"open_tcp_server(\"{ip}\", {port}) returned {rc} ({(int)rc})");
        if (rc != HaxErrorCode.hax_success)
        {
            Log("The server did not open. Is the port already in use, or is the IP not an address of this PC?");
            PauseIfInteractive();
            return 1;
        }

        Native.start_auto_read(1000);
        Log("start_auto_read(1000) called");
        new Thread(() => WatchTcp(port)) { IsBackground = true }.Start();
        Log($"Waiting for the gate. Point it at {(ip == "0.0.0.0" ? "one of: " + string.Join(", ", LocalIPv4()) : ip)}, port {port}");
        Console.WriteLine("Keys: A = enable_alarm on the most recent connID, B0/B1 = try beep off/on, L = list connIDs, Q = quit");

        while (true)
        {
            char key = char.ToUpperInvariant(ReadKey());
            switch (key)
            {
                case 'A':
                    string? target;
                    lock (Sync) target = lastConnId;
                    if (target == null)
                    {
                        Log("A: no connID seen yet");
                        break;
                    }
                    HaxErrorCode alarm = Native.enable_alarm(target);
                    Log($"enable_alarm(\"{target}\") returned {alarm} ({(int)alarm})");
                    break;
                case 'B':
                    char v = ReadKey(); // B must be followed immediately by 0 or 1
                    if (v is '0' or '1') TryBeep(v - '0');
                    else Log($"B: expected B0 or B1, got 'B{v}'");
                    break;
                case 'L':
                    lock (Sync)
                    {
                        if (Seen.Count == 0) Log("L: no connIDs seen yet");
                        foreach (var (id, state) in Seen)
                            Log($"L: {id}  {state}{(id == lastConnId ? "  (most recent)" : "")}");
                    }
                    break;
                case 'Q':
                    bridge?.Stop(TimeSpan.FromSeconds(3));
                    Native.stop_auto_read();
                    Log("stop_auto_read called, closing the server (the DLL can take up to about 10 s)...");
                    Native.close_tcp_server();
                    Log("close_tcp_server called, exiting");
                    return 0;
            }
        }
    }

    // v2 mode. The old DLL's TCP server, auto read and callbacks are never touched: this app listens for the gates
    // itself and only asks card_7k.dll to build and parse packets.
    static int RunV2(BridgeConfig config, bool argsGiven)
    {
        Log($"MODE        v2: this app listens on {config.ListenIp}:{config.ListenPort}, alarm channels " +
            $"{DeviceAlarmConfig.From(config.AlarmChannels)}, reply timeout {config.ReplyTimeout.TotalMilliseconds:0} ms");
        if (argsGiven)
            Log($"WARNING: the [listen-ip] [port] arguments only apply to legacy mode. v2 uses listenIp and listenPort from {BridgeConfig.FileName}.");
        if (!LoadSdk(out IntPtr dll))
        {
            PauseIfInteractive();
            return 1;
        }
        NativeHaxSdk? sdk = NativeHaxSdk.TryCreate(dll);
        if (sdk == null)
        {
            Log(NativeHaxSdk.MissingFunctionsMessage);
            return 1;
        }

        var server = new V2GateServer(sdk, config, (reader, epc) => bridge?.OfferForReader(reader, epc), Log);
        try { server.Start(); }
        catch (SocketException ex)
        {
            Log($"ERROR: cannot listen on {config.ListenIp}:{config.ListenPort}: {ex.Message}");
            Log("Is another program (or another copy of this app) using that port, or is listenIp not an address of this PC?");
            PauseIfInteractive();
            return 1;
        }
        string where = config.ListenIp.Equals(IPAddress.Any) ? "one of: " + string.Join(", ", LocalIPv4()) : config.ListenIp.ToString();
        Log($"Waiting for gates. Point each gate at {where}, port {config.ListenPort}");
        Console.WriteLine("Keys: Q = quit (A, L, B0 and B1 only work in legacy mode)");

        while (true)
        {
            char key = char.ToUpperInvariant(ReadKey());
            switch (key)
            {
                case 'A':
                case 'L':
                    Log($"{key}: not available in v2 mode");
                    break;
                case 'B':
                    Log("B0/B1: not available in v2 mode");
                    break;
                case 'Q':
                    server.Stop(TimeSpan.FromSeconds(2));
                    bridge?.Stop(TimeSpan.FromSeconds(3));
                    Log("exiting");
                    return 0;
            }
        }
    }

    // Callbacks run on DLL threads. An exception escaping into native code would kill the process, so catch everything.
    static void PortConnecting(IntPtr connId)
    {
        try
        {
            string id = Marshal.PtrToStringAnsi(connId) ?? "(null)";
            lock (Sync)
            {
                Seen[id] = "connected";
                lastConnId = id;
            }
            Log($"CONNECT     connID={id}");
        }
        catch (Exception ex) { Log($"ERROR in port_connecting: {ex}"); }
    }

    static void PortClosing(IntPtr connId)
    {
        try
        {
            string id = Marshal.PtrToStringAnsi(connId) ?? "(null)";
            lock (Sync) Seen[id] = "disconnected";
            Log($"DISCONNECT  connID={id}");
        }
        catch (Exception ex) { Log($"ERROR in port_closing: {ex}"); }
    }

    static void OutputTags(IntPtr connId, EpcIdList data)
    {
        try
        {
            string id = Marshal.PtrToStringAnsi(connId) ?? "(null)";
            lock (Sync)
            {
                Seen.TryAdd(id, "connected");
                lastConnId ??= id;
            }
            int count = Math.Clamp(data.Size, 0, data.Epc.Length); // never trust size beyond the 32 slots
            Log($"TAGS        connID={id} serial={Convert.ToHexString(data.Sn.SerialNum)} version={Convert.ToHexString(data.Sn.Version)} " +
                $"size={data.Size}{(count != data.Size ? $" (out of range, showing {count})" : "")}");
            for (int i = 0; i < count; i++)
            {
                EpcId e = data.Epc[i];
                string epc = Convert.ToHexString(e.Id);
                Log($"  EPC {i + 1}/{count}: {epc}  (len={e.Len} pc={e.Pc:X4} crc={e.Crc:X4})");
                bridge?.Offer(id, epc); // never blocks: cooldown check + queue write, the HTTP POST happens on the worker
            }
        }
        catch (Exception ex) { Log($"ERROR in output_tags: {ex}"); }
    }

    // config is gate-bridge.json from next to the exe. Any problem leaves bridge null, so the app stays a log-only test.
    static void StartBridge(BridgeConfig config)
    {
        if (config.AlarmUrl == null)
        {
            Log($"WARNING: alarms are NOT sent to the server, this run only logs tags. To send them, copy " +
                $"gate-bridge.example.json to {BridgeConfig.FileName} in the gate-tcp-test folder, set serverUrl, and run 'dotnet build' again.");
            return;
        }
        string names = string.Join(", ", config.Gates.Select(g => $"{g.Key}={g.Value}"));
        Log($"BRIDGE      sending tags to {config.AlarmUrl}, cooldown {config.Cooldown.TotalSeconds:0} s, " +
            $"gate names: {(names.Length > 0 ? names : "none")} (other gates use their IP)");
        bridge = new AlarmBridge(config, Log);
    }

    // B0 / B1: attempt to change the gate's beep setting through the documented config API.
    //
    // This CANNOT actually change beep, and that is the honest finding, not a bug:
    //   - hax_7k_config_st is 64 bytes and has no beep field. The beep byte lives at board-config blob
    //     offset 0x105 (261), i.e. 197 bytes past the end of this struct, so set_config can never carry it.
    //   - The `beep` key the DLL knows is a JSON key of the separate, undocumented, license-checked Device_*
    //     API (Device_Init/Device_SetConfig/...), which has no header. Using it would mean guessing its ABI.
    //   - Changing beep otherwise needs the raw 0xC6/0xD6 board frames, which we will not guess.
    // So B0/B1 do the one safe, documented thing: read the config, write back the identical bytes, and
    // re-read, logging each call and the full blob before/after. No beep change, no raw frames.
    static void TryBeep(int value)
    {
        string? id;
        lock (Sync) id = lastConnId;
        if (id == null)
        {
            Log($"B{value}: no connID yet. A gate must be connected and past its handshake first (nothing can connect on 127.0.0.1).");
            return;
        }

        Hax7kConfig before = NewConfig();
        HaxErrorCode r1 = Native.get_config(id, ref before);
        Log($"B{value}: get_config(\"{id}\") returned {r1} ({(int)r1})");
        if (r1 != HaxErrorCode.hax_success)
        {
            Log("  get_config did not succeed (no device?), so nothing is written. Beep unchanged.");
            return;
        }
        byte[] b0 = ConfigToBytes(before);
        Log($"  config BEFORE (64 bytes): {Convert.ToHexString(b0)}");
        Log($"  work_freq={before.WorkFreq} clock={Convert.ToHexString(before.Clock)} sn.serial={Convert.ToHexString(before.Sn.SerialNum)}");
        Log($"  NOTE: hax_7k_config_st has no beep field (beep byte is at blob offset 0x105, outside this 64-byte struct), so B{value} does NOT change the beep.");

        // Faithful write-back of the bytes just read - not a fabricated frame. It leaves every public value as
        // read from get_config. Whether the DLL preserves the out-of-struct beep byte during a set_config is
        // unknown without the gate, hence the warning.
        Log("  WARNING: set_config writes config to the device; calling it with the same values read above.");
        HaxErrorCode r2 = Native.set_config(id, ref before);
        Log($"B{value}: set_config(\"{id}\") returned {r2} ({(int)r2})");

        Hax7kConfig after = NewConfig();
        HaxErrorCode r3 = Native.get_config(id, ref after);
        Log($"B{value}: get_config(after) returned {r3} ({(int)r3})");
        byte[] b1 = ConfigToBytes(after);
        Log($"  config AFTER  (64 bytes): {Convert.ToHexString(b1)}");
        Log($"  public 64-byte config changed by the round-trip: {!b0.AsSpan().SequenceEqual(b1)} (the beep byte lives outside this struct and is not visible here either way)");
    }

    // A fully-allocated, zeroed config so the ByValArray fields marshal correctly on the way in to get_config.
    static Hax7kConfig NewConfig() => new()
    {
        Antenna = new Antenna[4],
        Clock = new byte[7],
        Sn = new RfidInfo { SerialNum = new byte[12], Version = new byte[20] },
    };

    static byte[] ConfigToBytes(Hax7kConfig c)
    {
        int n = Marshal.SizeOf<Hax7kConfig>();
        byte[] bytes = new byte[n];
        IntPtr p = Marshal.AllocHGlobal(n);
        try { Marshal.StructureToPtr(c, p, false); Marshal.Copy(p, bytes, 0, n); }
        finally { Marshal.FreeHGlobal(p); }
        return bytes;
    }

    // The DLL reports CONNECT only after the device answers its handshake, so also watch Windows' own TCP table.
    // "TCP OPEN" with no CONNECT means the gate reached this PC but did not answer the DLL.
    static void WatchTcp(uint port)
    {
        try
        {
            var open = new HashSet<string>();
            while (true)
            {
                var now = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                    .Where(c => c.LocalEndPoint.Port == port && c.State == TcpState.Established)
                    .Select(c => c.RemoteEndPoint.ToString())
                    .ToHashSet();
                foreach (string remote in now.Except(open))
                    Log($"TCP OPEN    remote={remote}  (a CONNECT line follows only if the gate answers the DLL)");
                foreach (string remote in open.Except(now))
                    Log($"TCP CLOSED  remote={remote}");
                open = now;
                Thread.Sleep(500);
            }
        }
        catch (Exception ex) { Log($"TCP watch stopped: {ex.Message}"); }
    }

    // Load card_7k.dll up front so a bad install gives a readable message instead of a crash on the first call.
    static bool LoadSdk(out IntPtr handle)
    {
        handle = IntPtr.Zero;
        string dll = Path.Combine(AppContext.BaseDirectory, "card_7k.dll");
        if (!File.Exists(dll))
        {
            Log($"ERROR: {dll} not found. Run 'dotnet build' so the files from ..\\sdk are copied next to the exe.");
            return false;
        }
        try
        {
            Log("Loading card_7k.dll (takes a few seconds, up to about 10 s on the first run)...");
            handle = NativeLibrary.Load(dll);
            Log("card_7k.dll loaded");
            return true;
        }
        catch (BadImageFormatException ex)
        {
            Log($"ERROR: wrong bitness. card_7k.dll is 32-bit, this process is {(Environment.Is64BitProcess ? 64 : 32)}-bit. Run the win-x86 build. ({ex.Message})");
        }
        catch (Exception ex)
        {
            Log($"ERROR: card_7k.dll could not be loaded: {ex.Message}");
            Log("Check that common.dll and the msvcp140/vcruntime140 DLLs are next to the exe, and that the x86 Visual C++ Redistributable (mfc140.dll) is installed.");
        }
        return false;
    }

    static char ReadKey()
    {
        if (!Console.IsInputRedirected) return Console.ReadKey(intercept: true).KeyChar;
        int c = Console.Read(); // piped input, used for scripted tests; end of input means quit
        return c < 0 ? 'Q' : (char)c;
    }

    static void PauseIfInteractive()
    {
        if (Console.IsInputRedirected) return;
        Console.WriteLine("Press any key to close.");
        Console.ReadKey(intercept: true);
    }

    static IEnumerable<string> LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => $"{a.Address} ({n.Name})"));

    static void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        lock (Sync)
        {
            Console.WriteLine(line);
            if (logFileFailed) return;
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch (Exception ex)
            {
                logFileFailed = true;
                Console.WriteLine($"WARNING: cannot write {LogPath}: {ex.Message}");
            }
        }
    }
}
