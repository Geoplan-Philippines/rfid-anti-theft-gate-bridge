# gate-tcp-test

Waits for the RFID gate (board RFID7000) to connect over TCP and prints everything `card_7k.dll` reports.

1. **Build** (needs the .NET 8 SDK, and internet the first time): open a terminal in this folder and run `dotnet build`
2. **Run**: `bin\Debug\net8.0\win-x86\gate-tcp-test.exe` listens on all addresses, port 9090.
   To choose, add IP then port: `gate-tcp-test.exe 0.0.0.0 9090`
3. **Firewall**: when Windows asks about gate-tcp-test, tick **both** Private and Public, then click **Allow access**
   (Windows marks most networks Public). Clicked Cancel by mistake? Windows Security > Firewall & network protection > Allow an app through firewall.
4. **Gate**: set it to connect to one of the IP addresses the app prints, on the same port.

**What to expect:** after up to ~10 s, `open_tcp_server(...) returned hax_success (0)`. Then, for each gate:
`TCP OPEN` (the gate reached the laptop), then `CONNECT` (the gate answered the DLL, ~3 s later), then `TAGS` and `EPC` lines while tags are near.
`TCP OPEN` without `CONNECT` means the gate does not speak the DLL's protocol. No `TCP OPEN` at all means a network, IP, port or firewall problem.

**Keys:** `A` = `enable_alarm` on the last connected gate (acts on the real gate), `B0`/`B1` = try to set the beep off/on,
`L` = list connections, `Q` = quit (takes up to ~10 s). Every line is also saved to `test.log` next to the exe.

**About B0/B1:** the SDK's public config struct (`hax_7k_config_st`) has no beep field, so these keys read the config,
write it back unchanged, and re-read it, logging the full config before and after. They do **not** change the gate's
beep (that setting is not reachable through this API). `set_config` writes to the device, so only press B0/B1 with the
real gate connected and knowingly.

## Sending tags to the anti-theft server

The app can send every tag the gate reads to the server, which checks it against the sold list and records an alarm
for anything unsold. Without this set up, the app only prints tags, as above.

1. **Settings**: in this folder, copy `gate-bridge.example.json` to `gate-bridge.json` and edit it:
   ```json
   {
     "serverUrl": "http://localhost:8000",
     "cooldownSeconds": 30,
     "gates": { "169.254.68.93": "GATE-1" }
   }
   ```
   - `serverUrl`: the server's address, without `/api/v1` (the app adds `/api/v1/alarms`). Use the server PC's IP
     instead of `localhost` when the server runs on another PC.
   - `cooldownSeconds` (optional, default 30): a tag that stays near the gate is re-read every 2.5-3 s; it is sent
     at most once per this many seconds.
   - `gates`: the name to show on the dashboard for each gate's IP. A gate not listed shows its IP.
2. **Build** with `dotnet build` again. That copies `gate-bridge.json` next to the exe, which is where it is read.
3. **Run** the app as before. Near the top it prints the address it sends to:
   `BRIDGE      sending tags to http://localhost:8000/api/v1/alarms, cooldown 30 s, gate names: 169.254.68.93=GATE-1`.
   If it prints `WARNING: alarms are NOT sent to the server` instead, the file is missing or wrong (the line above
   that warning says what is wrong), and the app only logs tags.

**What the lines mean** (one per tag sent; the end shows the server's answer and how long it took, up to ~10 s
because the server grabs a CCTV picture first):

| Line | Meaning |
|---|---|
| `ALARM SENT  epc=... reader=GATE-1 alarm recorded` | The tag is not sold. The server recorded an alarm, which shows on the dashboard's Alarm Events page. |
| `SOLD TAG    epc=... ignored, the tag is sold` | The tag is on the sold list. Nothing recorded. |
| `DUPLICATE   epc=... already alarmed in the last 30 s` | Same tag alarmed moments ago (for example by another gate). Nothing new recorded. |
| `RETRY       epc=... HTTP 503 ..., retry 1 of 3 in 1 s` | The server was busy or unreachable. The app tries again, up to 3 times. |
| `SEND FAILED epc=... ...` | The tag could not be sent, with the reason. The next time the gate reads that tag, the app tries again. |

`Q` waits up to 3 s for tags still being sent, then quits as before. `A`, `L`, `B0` and `B1` are unchanged.

## v2 mode (HAX's updated card_7k.dll)

**What it is:** HAX's updated `card_7k.dll` no longer opens a port itself. In v2 mode this app listens for the gates
and only asks the DLL to build the packets it sends and to read the packets the gates send back. Several gates can be
connected at once. Legacy mode (everything above) stays the default and works exactly as before.

**Build the v2 copy.** Each build carries exactly one HAX DLL set, in its own folder, so the two sets never mix:

| Build command | DLL set | Run from | For |
|---|---|---|---|
| `dotnet build` | `..\sdk` (card_7k.dll 2.0.1.0) | `bin\Debug\net8.0\win-x86\` | `"sdkMode": "legacy"` |
| `dotnet build -p:HaxSdk=2.1.2.0` | `..\hax-7000-sdk-2.1.2.0-2026-11-07\2.1.2.0` | `bin\hax-2.1.2.0\Debug\net8.0\win-x86\` | `"sdkMode": "v2"` |

Never copy HAX DLLs into a `bin` folder by hand. The build checks every DLL of the chosen set against
`sdk-manifests\<set>.sha256` (hashes of HAX's original files) and stops if one differs. The exe checks the DLLs next
to it again before loading `card_7k.dll`. It refuses to start if any is from the other set (`common.dll` has the same
version number in both sets, so only the hash tells them apart), or if `sdkMode` does not suit the set it was built with.

**Check the DLL without a gate:** `bin\hax-2.1.2.0\Debug\net8.0\win-x86\gate-tcp-test.exe --check-sdk` loads HAX's
2.1.2.0 DLL and checks it against packets captured from the gate during HAX's demo (firmware 7000202611040961). v2
runs the same check at every start. The `SDK CHECK` lines say whether the DLL reads those packets, and whether a packet
split over two TCP reads, or two packets in one read, can be cut apart using only the DLL's own checks. HAX documents
no frame header or length field, so this check is the only evidence for that. When it passes, v2 handles split and
joined reads; when it fails, each read is treated as one packet, as HAX's sample does, and the app warns at start.

**Switch to v2:**
1. In `gate-bridge.json` (see above; `gate-bridge.example.json` lists every setting) set `"sdkMode": "v2"`.
2. Run `dotnet build -p:HaxSdk=2.1.2.0`, then run `bin\hax-2.1.2.0\Debug\net8.0\win-x86\gate-tcp-test.exe`.
   The IP and port on the command line are for legacy mode only.
3. Set each gate to connect to this PC's IP address and `listenPort` (**9000** by default, not 9090). The app prints
   this PC's addresses when it starts. If Windows asks about the firewall, allow it as in step 3 at the top.

| Setting | Default | Meaning |
|---|---|---|
| `sdkMode` | `"legacy"` | `"legacy"`: the old DLL runs the TCP server, as before. `"v2"`: this app runs it. |
| `listenIp` | `"0.0.0.0"` | Address to listen on. `0.0.0.0` means every network card of this PC. |
| `listenPort` | `9000` | Port the gates connect to. |
| `alarmChannels` | `[1,1,1,1]` | Alarm channels 1 to 4 for the alarm command. `1` = on, `0` = off. Only `[1,1,1,1]` has been tried on the gate. |
| `replyTimeoutMs` | `5000` | How long to wait for the gate's answer to each setup step and each alarm command, in milliseconds. |
| `physicalAlarm` | `"off"` | `"off"`: the alarm channels command is sent once when a gate connects, as before, and never per tag. `"selective"`: see below. |
| `soldCheckTimeoutMs` | `800` | Selective only: how long to wait for the server's sold check, in milliseconds. There is no retry. |
| `onSoldCheckFailure` | `"alarm"` | Selective only: what the gate does when the sold check gives no answer. `"alarm"`: sound it. `"silent"`: do not. |

`serverUrl`, `cooldownSeconds` and `gates` work the same in both modes. A wrong value in one of the v2 settings above
stops the app at startup with `ERROR: gate-bridge.json: ...`, which says what to write instead.

**What to expect:** after `card_7k.dll loaded` and the `SDK CHECK` lines, the line
`Waiting for gates. Point each gate at ..., port 9000`.

| Line | Meaning |
|---|---|
| `GATE OPEN   reader=GATE-1 remote=192.168.1.50:50113` | A gate connected. `reader` is its name from `gates`, or its IP. |
| `TO GATE     reader=GATE-1 server control, 9 bytes: ...` | The packet the DLL built, sent to the gate, in hex. Setup step 1 takes control of the gate. |
| `FROM GATE   reader=GATE-1 server control, 9 bytes: ...` | The gate's raw answer, in hex. |
| `STEP        reader=GATE-1 server control: ok` | The DLL accepted the answer. With `physicalAlarm` `"off"`, the same three lines follow for `alarm channels` (setup step 2). |
| `STEP FAILED reader=GATE-1 alarm channels: unpack failed (header/length/CRC), code 4` | The gate refused, its answer was not accepted, or it did not answer in time. The app closes this gate's connection only; the gate can connect again. Give HAX the code and the hex lines above it if this keeps happening. |
| `TAG         reader=GATE-1 ant=1 epc=E2000017...` | A tag the gate read, and on which antenna. It is sent to the server exactly as in legacy mode (same cooldown, same `ALARM SENT` and other lines). |
| `BAD PACKET  reader=GATE-1 ...` | Bytes from the gate the DLL could not read, and why. They are skipped and the connection stays open. |
| `GATE CLOSED reader=GATE-1 remote=... the gate disconnected` | The gate went away, or the line says why the connection ended. It can connect again at any time. |

**Keys in v2:** `Q` stops listening, closes every gate connection, waits up to 3 s for tags still being sent, then
quits. `A`, `L`, `B0` and `B1` only print `not available in v2 mode`.

## Selective physical alarm (v2, `"physicalAlarm": "selective"`)

The gate itself sounds (lights and beep) only for tags that are not sold. Nothing is sent to the gate when it
connects. For each tag packet, the app asks the server which tags are sold
(`POST {serverUrl}/api/v1/anti-theft/known-epcs/check`, up to 100 tags per request, one request at a time per gate),
then:

| The server says | The gate |
|---|---|
| every tag in the packet is sold | stays silent |
| any tag is not sold | gets one alarm command for the packet |
| no answer in time, an error status (429, 5xx, ...), an unreadable answer, or no answer for a tag | gets one alarm command, unless `onSoldCheckFailure` is `"silent"`. The tag is logged as unknown, never as unsold. |

This is separate from the dashboard: every tag is still sent to `/api/v1/alarms` as before (same cooldown, same
retries, same CCTV snapshot), and that never delays the gate. A tag read again inside the dashboard's 30 s cooldown
still sounds the gate again. At most one alarm command waits for the gate's reply per gate; a decision to alarm while
one waits is covered by it (and sent once more if that command fails). When 4 checks (up to 400 tags) are already
waiting for one gate, further tags are not dropped: they count as unknown and follow `onSoldCheckFailure`.

`"selective"` without a valid `serverUrl` stops the app at startup.

| Line | Meaning |
|---|---|
| `SOLD CHECK  reader=GATE-1 2 tag(s): 1 sold, 1 unsold, 0 unknown (12 ms)` | The server's answer for one packet. |
| `CHECK FAIL  reader=GATE-1 HTTP 503 ...; sold status unknown for E2...` | The sold check gave no answer for these tags. |
| `NO ALARM    reader=GATE-1 all 2 tag(s) sold` | Every tag is sold: nothing is sent to the gate. |
| `GATE ALARM  reader=GATE-1 unsold: E2...` | The gate should sound, and why (`unsold: ...` or `sold status unknown ...`). |
| `ALARM CMD   reader=GATE-1 channels [1,1,1,1], 9 bytes: ...` | The alarm command sent to the gate, in hex. |
| `ALARM OK    reader=GATE-1 the gate accepted the alarm command (40 ms)` | The gate's reply to it. |
| `ALARM FAIL  reader=GATE-1 ...` | The gate refused the command, did not reply within `replyTimeoutMs`, or it could not be sent. The connection stays open; the next unsold read tries again. |
| `ALARM LATE  reader=GATE-1 ...` | A reply arrived after its command had timed out. |
| `ALARM BUSY  reader=GATE-1 ...` | An alarm was decided while a command was still waiting for its reply; that command covers it. |
| `QUEUE FULL  reader=GATE-1 ...` | Too many checks waiting for this gate: the rest count as unknown. |

**Not yet confirmed on the gate:** what each of the four alarm channels does, and whether the alarm command is a
one-shot alarm or switches on an alarm mode that lasts. Check both on the real gate before relying on selective mode.

**Tests** (no gate or server needed): from the `gate-tcp-test.Tests` folder next to this one, run `dotnet test`.
They take about a minute because some tests run a slow fake server for real. The v2 tests use a fake DLL and fake
gates on this PC; they test this app's logic, not HAX's protocol (`--check-sdk` checks the real DLL).
