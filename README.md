# RFID anti-theft gate bridge

Connects HAX RFID7000 anti-theft gates to the MUJI EAS server
([`rfid-anti-theft-server`](https://github.com/Geoplan-Philippines/rfid-anti-theft-server)).

- Every tag a gate reads is reported to the server's dashboard (`POST /api/v1/alarms`, with a CCTV snapshot for unsold
  tags).
- In **selective mode** the gate itself sounds only for unsold tags: the bridge asks the server which tags are sold
  (`POST /api/v1/anti-theft/known-epcs/check`) and sends the gate an alarm command only when one is not.

It is a Windows console app (`gate-tcp-test`) built on HAX's vendor DLL `card_7k.dll`, which is **32-bit only**.

> **Operators:** the step-by-step run guide, every setting and every log line are in
> [`gate-tcp-test/README.md`](gate-tcp-test/README.md). This page is for developers setting up the code.

## Quick start

```powershell
git clone https://github.com/Geoplan-Philippines/rfid-anti-theft-gate-bridge.git
cd rfid-anti-theft-gate-bridge

# 1. Tests: no gate, no server, no HAX DLL needed
cd gate-tcp-test.Tests
dotnet test

# 2. Build the app with HAX's 2.1.2.0 DLLs (v2 mode)
cd ..\gate-tcp-test
dotnet build -p:HaxSdk=2.1.2.0

# 3. Check the DLL against packets captured from a real gate (no gate needed)
.\bin\hax-2.1.2.0\Debug\net8.0\win-x86\gate-tcp-test.exe --check-sdk
```

Step 3 should end with `reads the gate's packets yes, packet boundaries verified yes`.

## Prerequisites

| What | Why |
|---|---|
| Windows 10/11 | The vendor DLLs are Windows-only. |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Builds the app and the tests. The app is self-contained, so the PC that runs it needs no separate .NET install. |
| Microsoft Visual C++ Redistributable, **x86** | HAX's `common.dll` needs `mfc140.dll`, which is not shipped with the DLLs. |
| Internet on the first build | NuGet restore. |
| HAX's DLL sets in the repo (see [Layout](#layout)) | `sdk\` for legacy mode, `hax-7000-sdk-2.1.2.0-2026-11-07\2.1.2.0\` for v2 mode. |
| A running `rfid-anti-theft-server` | Only to send tags anywhere. Without `serverUrl` the app only logs tags. Selective mode needs it. |

Windows Smart App Control can block freshly built DLLs or HAX's `common.dll` (it has done so on one dev PC). If
`dotnet test` finds no tests, or the app stops at `Loading card_7k.dll`, check *Event Viewer > Applications and
Services Logs > Microsoft > Windows > CodeIntegrity > Operational*. Don't disable security controls to work around it;
use another PC or ask whoever owns the machine.

## Layout

```
rfid-anti-theft-gate-bridge/
├── gate-tcp-test/                    the app (net8.0, win-x86, self-contained)
│   ├── Program.cs                    entry point, legacy mode, SDK loading, --check-sdk
│   ├── GateBridge.cs                 gate-bridge.json, AlarmBridge (dashboard reporting, cooldown, retries)
│   ├── V2GateServer.cs               v2: TCP listener, one task per gate, setup steps, socket reader
│   ├── GateStream.cs                 v2: PacketAssembler, turns TCP reads into packets (see below)
│   ├── GateAlarmCoordinator.cs       v2 selective: sold check -> decision -> alarm command -> gate reply
│   ├── SoldCheck.cs                  v2 selective: HTTP sold check, decision rule, bounded queue
│   ├── HaxSdk.cs                     IHaxSdk (the five Device_* functions), error codes, EPC JSON
│   ├── NativeHaxSdk.cs               IHaxSdk bound to the real card_7k.dll (the only file that calls it)
│   ├── SdkManifest.cs                which DLL set this build carries; hash check before loading
│   ├── SdkSelfCheck.cs               real-DLL check against captured gate packets
│   ├── sdk-manifests/*.sha256        SHA-256 of HAX's original files, per DLL set
│   ├── sdk-v2/                       HAX's earlier packet-SDK docs (superseded by the 2.1.2.0 delivery)
│   ├── gate-bridge.example.json      every setting with its default
│   └── README.md                     operator guide
├── gate-tcp-test.Tests/              xunit tests (64-bit, DLL-free: fake SDK, fake gates, fake server)
├── sdk/                              HAX legacy DLL set (card_7k.dll 2.0.1.0) + config.ini
├── hax-7000-sdk-2.1.2.0-2026-11-07/  HAX 2.1.2.0 delivery: hax_7k_sdk.h, main.cpp, readme.txt, 2.1.2.0\*.dll
├── hax_7k_adapt.h, 7k_api_demo.*     HAX's legacy API header and samples (reference only)
└── README.md                         this file
```

Never edit or hand-copy the vendor files. The build checks them against `sdk-manifests\` and stops if they differ.

## How it works

```
                      ┌──────────────────── gate-tcp-test.exe (x86) ────────────────────┐
 RFID7000 gate        │                                                                 │
 (USR-C215 Wi-Fi) ──TCP──> V2GateServer ── PacketAssembler ──┬── tag ──> AlarmBridge ──HTTP──> POST /api/v1/alarms
   TCP client         │     (listenPort)    (card_7k.dll       │          (30 s cooldown,        (dashboard + CCTV)
                      │                      unpack only)      │           retries)
                      │                                        └── tag ──> GateAlarmCoordinator ──HTTP──> POST /api/v1/anti-theft/
                      │                                                    (selective mode only)          known-epcs/check
                      │      alarm command <── card_7k.dll pack <──────────── unsold or unknown
                      └─────────────────────────────────────────────────────────────────┘
```

### Two SDK modes

| `sdkMode` | DLL set | Who runs the TCP server | Notes |
|---|---|---|---|
| `legacy` (default) | `sdk\`, card_7k.dll 2.0.1.0 | the DLL (`open_tcp_server`, callbacks) | Default port 9090 from the command line. Unchanged. |
| `v2` | 2.1.2.0 delivery | this app (`V2GateServer`) | The DLL only builds and parses packets (`Device_Pack*` / `Device_UnPack*`). Port from `listenPort`, 9000 by default. |

Each build carries exactly one DLL set, chosen with the `HaxSdk` MSBuild property:

| Command | DLL set | Output folder |
|---|---|---|
| `dotnet build` | legacy | `bin\Debug\net8.0\win-x86\` |
| `dotnet build -p:HaxSdk=2.1.2.0` | 2.1.2.0 | `bin\hax-2.1.2.0\Debug\net8.0\win-x86\` |

Before loading `card_7k.dll`, the exe hashes the DLLs next to it against its embedded manifest. It refuses to start if
any file is from the other set, or if `sdkMode` doesn't match the set it was built with. `common.dll` has the same
version number in both sets, so only the hash tells them apart.

### Selective physical alarm (v2, `"physicalAlarm": "selective"`)

For each tag packet, per gate:

1. Every tag goes to `AlarmBridge` for the dashboard, exactly as before. That path never delays the gate.
2. The coordinator asks the server which tags are sold: up to 100 EPCs per request, one request in flight per gate,
   timeout `soldCheckTimeoutMs` (800 ms), no retry.
3. Decision for the batch (`PhysicalAlarmRules.Decide`):
   - any tag unsold: one alarm command;
   - all sold: nothing;
   - no answer (timeout, 429, 5xx, bad or missing result): the tag is **unknown**, never "unsold"; `onSoldCheckFailure`
     decides (`"alarm"` by default).
4. At most one alarm command waits for the gate's reply per gate. A decision made meanwhile is covered by it, and is
   sent once more if that command fails.
5. At most 4 sold checks (400 EPCs) wait per gate. Beyond that, tags are decided as unknown (`QUEUE FULL`), never
   dropped.

With `"physicalAlarm": "off"` (the default), v2 behaves as before: the alarm-channels command is sent once when a gate
connects, and never per tag.

### TCP reads are not packets

HAX documents no frame header or length field ("one recv is treated as one packet"). The bridge invents none.
`PacketAssembler` cuts a run of bytes at the **shortest prefix the DLL's own unpack functions accept**, and keeps
rejected bytes up to 500 ms for the rest of their packet. That is only correct if the DLL:

- rejects every truncated packet,
- never takes a tag packet for an alarm reply or the reverse,
- rejects bytes after a packet, or ignores them and reads the packet as if alone.

`SdkSelfCheck` verifies exactly that against packets captured from a real gate (firmware 7000202611040961) at every
v2 start and with `--check-sdk`. HAX's 2.1.2.0 DLL passes; it ignores bytes after a packet, which is why the
"shortest accepted prefix" rule is needed. If the check fails, the app treats each read as one packet, as HAX's
sample does, and warns at startup.

## Running locally

### Against a local server

Set up the server first (see its README): `bun install`, `.env`, `bunx prisma migrate deploy`,
`bunx prisma db seed`, `bun run start:dev`. Then:

```powershell
cd gate-tcp-test
copy gate-bridge.example.json gate-bridge.json   # gitignored: never commit real addresses
```

Edit `gate-bridge.json`:

```json
{
  "serverUrl": "http://localhost:8000",
  "gates": { "<gate IP>": "GATE-1" },
  "sdkMode": "v2",
  "listenIp": "0.0.0.0",
  "listenPort": 9000,
  "physicalAlarm": "selective"
}
```

- `serverUrl` is the server's origin **without** `/api/v1`; the app adds the paths.
- Missing keys take the defaults in `gate-bridge.example.json`. A wrong value stops the app at startup with a message
  saying what to write instead.

```powershell
dotnet build -p:HaxSdk=2.1.2.0     # also copies gate-bridge.json next to the exe
.\bin\hax-2.1.2.0\Debug\net8.0\win-x86\gate-tcp-test.exe
```

Check the server's new route from PowerShell:

```powershell
curl.exe -X POST http://localhost:8000/api/v1/anti-theft/known-epcs/check -H "Content-Type: application/json" -d '{"epcs":["E28069950000AA039398B5EA"]}'
```

### With a real gate

1. Only one program can listen on the port: close HAX's demo (`netstat -ano | findstr :9000` shows what holds it).
2. Allow inbound TCP on `listenPort` for the exe in Windows Firewall (Private and Public).
3. Start the bridge, then point the gate's TCP client (USR-C215 module) at this PC's IP and `listenPort`.
4. Expect `GATE OPEN`, `server control: ok`, then `TAG` lines. The operator guide explains every line.

`Q` quits. The log is also written to `test.log` next to the exe.

## Tests

```powershell
cd gate-tcp-test.Tests
dotnet test
```

- Takes about 40 s; some tests run slow fake servers for real.
- The test project is 64-bit and cannot load the 32-bit app, so it compiles the app's **DLL-free** source files
  directly (see `gate-tcp-test.Tests.csproj`). Keep `card_7k.dll` calls inside `NativeHaxSdk.cs` and `Program.cs`, and
  add every new DLL-free file to the test project's `<Compile Include>` list.
- `FakeHaxSdk` uses readable stand-in packets, not HAX's real bytes. It can model a strict DLL, a DLL that ignores
  trailing bytes (`lenient: true`, like the real 2.1.2.0 DLL) and a broken one (`acceptTruncated: true`).
- Fake gates connect over real loopback TCP; `FakeAlarmServer` (Kestrel) stands in for `/api/v1/alarms` and
  `/api/v1/anti-theft/known-epcs/check`.

Fake-SDK tests prove the bridge's logic, not HAX's protocol. The real-DLL evidence is `gate-tcp-test.exe --check-sdk`:

| Test file | Covers |
|---|---|
| `SelectiveAlarmTests.cs` | Selective mode end to end: sold/unsold/mixed, failures, slow dashboard, repeats, multiple gates, gate replies, split/joined reads, queue overflow, mode off |
| `SoldCheckTests.cs` | Decision rule, bounded queue, HTTP client against every failure |
| `PacketAssemblerTests.cs` | Split, joined and interleaved TCP reads, in both modes |
| `SdkChecksTests.cs` | Self-check logic, captured fixtures, vendor files vs manifests, mixed-set detection |
| `SelectiveConfigTests.cs`, `V2RulesTests.cs`, `BridgeRulesTests.cs` | `gate-bridge.json` parsing and rules |
| `V2GateServerTests.cs`, `AlarmBridgeTests.cs` | v2 setup steps and dashboard reporting |

## Common tasks

**Add a new HAX DLL delivery.** Put HAX's folder at the repo root unchanged. Add a `PropertyGroup` for it in
`gate-tcp-test.csproj` (`HaxSdkDir`, its own `BaseOutputPath` and `IntermediateOutputPath`), generate its manifest, add
the set name to `SdkManifest.Sets`, then run `--check-sdk` against it:

```bash
cd <delivery>/<dll folder> && for f in $(ls *.dll | sort); do printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "$f"; done > ../../gate-tcp-test/sdk-manifests/<set>.sha256
```

**Add a setting.** Add it to `BridgeConfig` (`GateBridge.cs`): property, `FileModel` field, parsing with a clear
`ConfigException` message. Then update `gate-bridge.example.json`, the operator README table and the config tests.
Unknown keys are rejected, so a misspelt setting never passes silently.

**Change what the gate does.** The decision lives in `PhysicalAlarmRules.Decide` (`SoldCheck.cs`); the per-gate
flow, timeouts and resends live in `GateAlarmCoordinator.cs`. Only `V2GateServer.SendAlarmAsync` writes alarm commands
to the socket.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Build: `... does not match sdk-manifests\<set>.sha256` | A vendor DLL was changed or swapped. Restore HAX's original files. |
| Build: `HaxSdk '...' is not a known DLL set` | Use `legacy` (default) or `2.1.2.0`. |
| Start: `v2 mode needs the 2.1.2.0 DLL set, but this exe was built with the legacy set` | Build with `-p:HaxSdk=2.1.2.0` and run the exe from `bin\hax-2.1.2.0\...`. |
| Start: `... is the legacy set's copy ... the SDK sets are mixed` | A DLL was copied in by hand. Rebuild; never copy DLLs into `bin`. |
| Start: `ERROR: gate-bridge.json: ...` | Fix the named setting; the message says what to write. |
| Start: `physicalAlarm "selective" needs serverUrl` | Set `serverUrl` to the server's origin, for example `http://localhost:8000`. |
| `Loading card_7k.dll` fails | Install the x86 VC++ Redistributable; check Smart App Control (see Prerequisites). |
| `cannot listen on ...:9000` | Another program (often HAX's demo) holds the port, or `listenIp` isn't this PC's address. |
| No `GATE OPEN` | Network, gate destination IP/port, or firewall. |
| Every tag beeps, `CHECK FAIL ... HTTP 404` | The server doesn't have `/api/v1/anti-theft/known-epcs/check` yet: deploy, rebuild and restart it. |
| `dotnet test` finds no tests | Smart App Control blocked the freshly built test DLL; see Prerequisites. |

## Known limitations

- Whether the gate's alarm command is a one-shot alarm or switches on a lasting alarm mode is **not yet confirmed on
  hardware**. Confirm it before relying on selective mode.
- What each of the four alarm channels does is unconfirmed; only `[1,1,1,1]` has been tried on a gate.
- Bytes the DLL cannot read are logged as `BAD PACKET` and do not sound the gate.
- Legacy mode with the 2.1.2.0 DLLs is untested, so the app refuses it.
- Gate endpoints (`/alarms`, `/known-epcs/check`) have no authentication yet (TODO in the server).
