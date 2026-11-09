# Plan: selective physical gate alarm (unsold tags beep, sold tags don't)

Prepared 2026-11-07. Plan only: no code, SDK files or server files were changed. Nothing was run against the DLLs,
the gate or a database, because Smart App Control blocks new test DLLs and HAX `common.dll` on the PC where this
was prepared.

- Bridge: `Geoplan-Philippines/rfid-anti-theft-gate-bridge` at `f6602c7`. Its only working-tree change is the
  untracked SDK folder.
- Server: `Geoplan-Philippines/rfid-anti-theft-server` at `3528b3c` on `main`, clean working tree (read-only reference).

## 1. What the code shows

| Starting finding | Result | Evidence |
|---|---|---|
| V2 sends the alarm command when the gate connects, not after a sold-status decision | **Confirmed** | `gate-tcp-test/V2GateServer.cs:133-136`: server control is sent, then "alarm channels". `HandleTagPacket` (`:214-231`) only logs each tag and calls `onTag`. |
| The API result does not control the physical gate | **Confirmed** | `AlarmBridge.SendAsync` only logs the result (`GateBridge.cs:355-365`). It is one serial worker with a 30 s HTTP timeout (`:277`), and the cooldown is applied before queuing (`:301`). |
| `POST /api/v1/alarms` checks `known_epcs`, and unsold replies wait for CCTV | **Confirmed** | `alarms.service.ts:41,102` checks the sold list. For unsold tags it creates the alarm row (`:46`), then waits on `captureSnapshot` (`:60`), which runs `curl --max-time 10` (`cctv-capture.service.ts:43`). So an unsold reply can take about 10 s and writes data. |
| `GET /api/v1/anti-theft/known-epcs` returns the whole sold list | **Confirmed** | `anti-theft.controller.ts:15`, `anti-theft.service.ts:14`. The route needs no login and is not paginated. |
| **New:** v2 mode has never had a usable DLL in this checkout | **Found** | `sdk\card_7k.dll` is version 2.0.1.0 (built 2026-07-22). It has **none** of the five `Device_Pack*`/`Device_UnPack*` exports. The copies in `bin\Debug` and `bin\Release` are the same file. `NativeHaxSdk.TryCreate` (`NativeHaxSdk.cs:38`) would fail, and the README tells users to copy DLLs into `bin` by hand, which is how versions get mixed. |

Other server facts relevant to the plan:

- `known_epcs.epc` is `@unique` (`prisma/schema.prisma:30`), so lookups by EPC already use an index.
- Rate limits: 100 requests per minute per IP globally (`src/core/security/rate-limit.module.ts`); `/alarms` is
  raised to 600 (`alarms.controller.ts:29`).
- `ValidationPipe` rejects unknown fields (`whitelist` + `forbidNonWhitelisted`, `src/app.module.ts`).
- Every response is wrapped as `{statusCode, message, data}` (`src/common/interceptors/response.interceptors.ts`).

**Not verifiable here:** which server version is deployed, the database contents, real DLL behaviour, and gate
behaviour.

## 2. New SDK delivery

The files are at the top level, with no extra extraction nesting:

- `C:\Users\Ace\repos\work\rfid-anti-theft-gate-bridge\hax-7000-sdk-2.1.2.0-2026-11-07\main.cpp`
- `C:\Users\Ace\repos\work\rfid-anti-theft-gate-bridge\hax-7000-sdk-2.1.2.0-2026-11-07\hax_7k_sdk.h`
- `C:\Users\Ace\repos\work\rfid-anti-theft-gate-bridge\hax-7000-sdk-2.1.2.0-2026-11-07\readme.txt`
- `C:\Users\Ace\repos\work\rfid-anti-theft-gate-bridge\hax-7000-sdk-2.1.2.0-2026-11-07\2.1.2.0\` — 12 x86 DLLs:
  `card_7k`, `common`, `adapt_for_imports`, `concrt140`, `msvcp140` (plus `_1`, `_2`, `_atomic_wait`,
  `_codecvt_ids`), `vccorlib140`, `vcruntime140`, `vcruntime140_threads`

| | `sdk\` (current runtime set) | `2.1.2.0\` |
|---|---|---|
| `card_7k.dll` | 2.0.1.0. Has `Device_Init`/`StartScan` and similar; no packet API. | 2.1.2.0, built 2026-11-04. Has all 5 packet exports plus the legacy exports (`open_tcp_server`, `start_auto_read`, `enable_alarm`, `get_config`/`set_config`, …). The `Device_Init` family is removed. |
| `common.dll`, `adapt_for_imports.dll` | 2.0.6.2 / 1.1.1001.0 | **Same version numbers, different files.** A version check alone cannot detect mixing. |
| VC++ runtime | 14.51 | 14.52 |
| `config.ini`, `config\*.ini` | Present and copied to output | **Absent.** Unknown whether 2.1.2.0 needs them; ask HAX rather than reuse the old ones. |
| `ucrtbase.dll`, `api-ms-win-*` | Present (already excluded from the build copy) | Absent. Fine on Windows 10/11. |
| `mfc140.dll` (imported by `common.dll`) | Not shipped | Not shipped. The x86 VC++ Redistributable must be installed. |

SHA-256 of the files that matter for mixing:

| File | `sdk\` | `2.1.2.0\` |
|---|---|---|
| `card_7k.dll` | `05205fe38bd368609804a516174b9e35a99d7bf0df4f8c83ea77893f16dc6abf` | `97c50e8a87c12fa414f00d38ede9b8fa41cea9ac7c0190af3e48b52a5d263162` |
| `common.dll` | `feacf6903813002f5cbb6efbd961fe29da08a1b2b228e108140052ded06f9566` | `67c647cc5114dde764f21db9d025c889f9b058939e8b11d36a6ed26d91ba06eb` |
| `adapt_for_imports.dll` | `2851a8be74ea088e9f44cd1e2b9b1e997f9382234c7c030f7ca7c4cf4849f72f` | `f4be95ef52beb13b213add620fcf43a4648e189ea91697e3958845eb0a94c942` |

These, plus the hashes of the runtime DLLs in each set, go into the build manifest described below.

**Header:** the five function prototypes, `device_alarm_config` and the error-code enum are identical to the tracked
`gate-tcp-test\sdk-v2\hax_7k_sdk.h`; only comments changed. `HaxSdk.cs` and `NativeHaxSdk.cs` need no signature
changes.

**Call sequence changed:**

- The tracked `sdk-v2\main.cpp` sends the alarm once, at connect. `V2GateServer` copied that.
- The new `main.cpp` (`step_receive_epc`, lines 203-230) sends `{1,1,1,1}` after **every** successful EPC unpack
  where the JSON is longer than 2 characters. It reads the reply with one 256-byte recv and a 5 s timeout.
- The new readme marks the alarm step as "optional, only when device alarm is needed".

**Minor gaps, nothing required for the packet API is missing:**

- The new `main.cpp` includes `../../modules/hax_uhf/inc/hax_7k_sdk.h`, so it won't compile from the delivery folder
  as-is.
- The delivery has no demo exe, so which DLL set Jenny's `HAX-demo.exe` loaded is not recorded. The DLL build date
  (2026-11-04) matches the firmware string `7000202611040961`.

**How a later implementation selects the new DLL set without mixing versions:**

1. Track the delivery unchanged as a vendor drop and keep `sdk\` for legacy mode. Replace the tracked `sdk-v2\*` docs
   with the delivery's three files.
2. In `gate-tcp-test.csproj` (line 20 today), add a `HaxSdkSet` property: `legacy` (the default) uses `..\sdk`,
   `2.1.2.0` uses `..\hax-7000-sdk-2.1.2.0-2026-11-07\2.1.2.0`. Copy only the selected folder.
3. Give each set its own output folder. Today `PreserveNewest` only copies a newer file, so switching from 2.1.2.0
   back to legacy would leave the newer `card_7k.dll` in place.
4. Write a `hax-sdk-manifest.json` (set name plus SHA-256 of every DLL) into the output folder.
5. Add a startup check in `Program.LoadSdk`: hash every DLL next to the exe before loading and refuse to start on
   any mismatch.
   - v2 mode requires the 2.1.2.0 set and all 5 exports.
   - Legacy mode on the 2.1.2.0 set is refused until it is tested on a gate. The exports exist, but their behaviour
     is unverified.
6. Delete the README's "copy HAX's DLL over `bin`" instructions.

## 3. Sold-status check

| Option | Verdict |
|---|---|
| Reuse `POST /alarms` | No. Unsold replies take ~10 s for CCTV, create alarm rows, and the bridge's 30 s cooldown hides repeat passes. |
| Periodically cache the whole `known-epcs` list | No. A tag sold seconds earlier would beep at the exit, the download grows without limit, and the route exposes the full sold list without login. |
| **Bounded batch lookup per packet** | **Recommended.** |

**Proposed endpoint:** `POST /api/v1/anti-theft/known-epcs/check`

- Returns 200 (needs `@HttpCode(200)`, because Nest's POST default is 201).
- Rate limit `@Throttle` 600/min, like `/alarms`.
- No login, like `/alarms` today, with the same TODO for a per-device key.

Request:

```json
{ "epcs": ["E28069950000AA039398B5EA", "3BE100003744AA2700000769"] }
```

- Validation: an array of 1–100 items; each a non-empty string of at most 128 characters; no other fields.

Response:

```json
{
  "statusCode": 200,
  "message": "Success",
  "data": {
    "results": [
      { "epc": "E28069950000AA039398B5EA", "sold": true },
      { "epc": "3BE100003744AA2700000769", "sold": false }
    ]
  }
}
```

- EPCs are normalized like the existing `normalizeEpc` (whitespace removed, uppercased), de-duplicated, and returned
  in request order.
- `sold` means a row exists in `known_epcs`, which is the same rule `/alarms` uses.
- One `findMany({ where: { epc: { in } }, select: { epc: true } })` on the unique index. No writes, no CCTV, no alarm
  rows.
- 400, 429 and 5xx responses mean "unknown" to the bridge.
- Extract one shared EPC-normalizing helper so `/alarms` and `/check` can't disagree.

**Schema impact: none.** No migration is needed.

**Server files:**

- `src/modules/anti-theft/anti-theft.controller.ts`
- `src/modules/anti-theft/anti-theft.service.ts`
- new `src/modules/anti-theft/dto/check-known-epcs.dto.ts`
- new `test/known-epcs-check.e2e-spec.ts`

**Bridge side:** a new DLL-free `SoldCheck.cs` with its own `HttpClient`, timeout set by `soldCheckTimeoutMs`, and
no retry on the alarm path. Each EPC comes back as Sold, Unsold or Unknown. A missing entry, a non-2xx reply, a
timeout or unreadable JSON all mean Unknown.

## 4. One decision per packet

A pure function `Decide(statuses, policy)`:

- Any EPC confirmed **Unsold** → **one** alarm command. This covers mixed sold+unsold packets and unsold+unknown
  packets.
- All EPCs **Sold** → no command.
- No Unsold but at least one **Unknown** → follow the `onSoldCheckFailure` policy. Unknown is never treated as proof
  of either status.
- Empty JSON or no EPCs → nothing, as in `main.cpp`.

## 5. Physical alarm kept separate from reporting

Each accepted packet goes to two independent consumers in `V2GateServer`:

1. **Reporting (unchanged).** Every EPC goes through `AlarmBridge.OfferForReader`, then its 30 s cooldown, the
   queue, `POST /alarms`, the server's own dedupe (`ALARM_COOLDOWN_SECONDS`), CCTV capture and retries. Sold EPCs are
   still posted. Reporting never depends on the lookup result.
2. **Physical (new).** A per-connection `GateAlarmCoordinator` runs lookup → decide → send command. It never waits
   on `AlarmBridge` and never uses its `Cooldown`, and `AlarmBridge` never waits on it.

Result: someone passing twice within 30 s triggers the beep both times, while the dashboard still records one event.

## 6. Tag traffic and alarm replies on the same TCP stream

**What the evidence says:**

- The readme and header state there is no frame header or length field, and treat one recv as one packet. The
  unpack functions return code 4 on a header/length/CRC failure, so the DLL has internal framing we can't see; the
  plan does not try to parse it.
- The screenshot shows strict alternation: a 37-byte tag packet, the 9-byte command `DB 82 6F 2D 1F 23 6C 92 40`,
  then the 9-byte reply `DB 82 6F 2D 1F 23 62 56 C1`.
- But the demo is single-threaded and blocks on the reply recv. It never tested a tag packet arriving before the
  reply; that would have been fed to `UnPackDeviceAlarm`.
- Packet boundaries come from how the USR-C215 module groups serial data into TCP segments, not from TCP itself.

Captured bytes from the successful demo run (for the real-DLL check in section 9):

| Packet | Bytes |
|---|---|
| Alarm command `{1,1,1,1}` (9) | `DB 82 6F 2D 1F 23 6C 92 40` |
| Alarm reply, ret=0 (9) | `DB 82 6F 2D 1F 23 62 56 C1` |
| Tag packet, `E28069950000AA039398B5EA` on antenna 4 (37) | `DB 82 43 3F 1F 23 62 E3 33 69 BB 32 7C 68 1B FD E9 88 04 1E A5 60 E6 8D 75 DC 63 66 14 C8 32 A2 96 BE 47 D4 C4` |

**Design:**

- One reader per socket (the existing loop). After setup, one writer (the coordinator). At most one alarm command
  outstanding per connection.
- The reader classifies each recv using only SDK calls:
  - **Reply outstanding:** try `UnPackDeviceAlarm` first. Code 0 means the reply is OK. Codes 10/12/13/14 mean the
    gate answered and refused. Code 4 means it isn't a reply, so try `UnPackEpcId`.
  - **No reply outstanding:** try `UnPackEpcId` first. If that fails, try `UnPackDeviceAlarm` so a late reply is
    labelled as such. Otherwise log `BAD PACKET` with the hex and keep the connection open.
- Never split or join recv chunks. A chunk both unpackers reject (for example a reply and a tag packet joined
  together) is logged with its hex and counted, so field logs show whether this happens. If a reply was outstanding
  it ends as unconfirmed.
- The reply wait is `replyTimeoutMs` (5000 ms, as in the demo). On timeout, log `ALARM UNCONFIRMED`, clear the
  outstanding command, and keep the connection open.
- The reader never waits on HTTP. Each connection has at most one lookup in flight; EPCs from packets that arrive
  meanwhile are merged (de-duplicated, at most 100) into the next lookup.
- In selective mode, drop the connect-time "alarm channels" step.

**Gating prerequisite:** run this check with the real DLL using the captured bytes above.

- `UnPackDeviceAlarm` on the 37-byte tag packet must return 4, not a flag code.
- `UnPackEpcId` on the reply bytes must not return OK with tags.
- If the DLL can't tell the two apart, the classification order above is not safe, and HAX must provide a way to
  identify the command type before this ships.

## 7. Configuration and legacy mode

New `gate-bridge.json` keys, used in v2 only and validated like the existing v2 keys:

| Key | Values | Default |
|---|---|---|
| `physicalAlarm` | `"off"` / `"selective"` | `"off"`, so an upgrade never starts beeping unexpectedly |
| `soldCheckTimeoutMs` | milliseconds | 800 |
| `onSoldCheckFailure` | `"silent"` / `"alarm"` | `"silent"` |

- `alarmChannels` and `replyTimeoutMs` are reused unchanged.
- `"selective"` without a `serverUrl` stops startup with a config error.
- The connect-time alarm is removed rather than kept as an option, since v2 never had a working DLL and so has no
  deployed behaviour to preserve. **Needs confirmation.**
- **Legacy mode stays the same:** its code path, port 9090 arguments and keys are untouched. It ignores the new keys
  with a warning.
- The test addresses (laptop `10.10.0.84:9000`, gate `10.10.0.87`) go only in the laptop's local
  `gate-bridge.json`, never in defaults or the example. `.gitignore` doesn't currently exclude `gate-bridge.json`;
  add it.

**Affected bridge files:**

- `gate-tcp-test/gate-tcp-test.csproj`
- `gate-tcp-test/Program.cs` (`LoadSdk`, `RunV2`)
- `gate-tcp-test/V2GateServer.cs`
- new `gate-tcp-test/SoldCheck.cs`
- `gate-tcp-test/GateBridge.cs` (`BridgeConfig`)
- `gate-tcp-test/README.md`
- `gate-tcp-test/gate-bridge.example.json`
- `gate-tcp-test/sdk-v2/*` (replaced by the delivery's files)
- `.gitignore`
- the test project, including adding `SoldCheck.cs` to its compile list, and the fakes (`FakeHaxSdk`, `FakeGate`,
  `FakeAlarmServer`)

## 8. Decisions needed

These are recommendations, not approved behaviour.

| Decision | Recommended default |
|---|---|
| Lookup timeout or server outage | `silent`: no beep, loud log line, and dashboard reporting continues. `alarm` (beep when unsure) is a store/ops choice. |
| Lookup timeout value | 800 ms. Confirm on the gate that the beep still happens while the person is at the gate. |
| Repeated unsold reads | Beep on every qualifying packet, limited by one outstanding command per connection (close to the demo's cadence). Alternative: a minimum interval between beeps for the same EPC. Decide after hardware step R6. |
| Alarm channels | Keep `[1,1,1,1]`, the only combination tested, until each channel's meaning is known. |
| Reply failures | Keep the connection open and log. Revisit reconnecting after N failures once there is field data. |
| Is the command a one-shot beep or a lasting "enable"? | **Unknown, and it changes the design.** If it's a lasting enable, a `{0,0,0,0}` disarm may be needed. Ask HAX and test (step R7). |
| Login for the new route | Open, matching `/alarms` and its existing TODO. |
| Batch limit | 100 EPCs per request. |
| Does 2.1.2.0 need `config.ini`? May legacy mode run on 2.1.2.0? | Ask HAX. Keep the two DLL sets separate until then. |
| Removing the connect-time alarm from v2 | Remove it (see section 7). |

**Questions for HAX:**

- What the alarm command does (one-shot or lasting enable), and whether a disarm is needed.
- What each of the four channels controls.
- Whether `UnPackDeviceAlarm` / `UnPackEpcId` reject the other packet type.
- Whether the DLL is thread-safe.
- Whether `config.ini` / `config\*.ini` are needed with 2.1.2.0.
- Recommended USR-C215 packing settings.

## 9. Automated tests

These can't run on the PC where this plan was prepared because of Smart App Control; run them on the spare laptop
or in CI.

**Bridge** (xunit, DLL-free: fake SDK, fake gate over real loopback TCP, and a fake server extended with the
`/check` endpoint):

1. **All sold:** one lookup containing both EPCs, no command to the gate, and both EPCs still posted to `/alarms`.
2. **Unsold:** exactly one command with the configured channels; reply OK gets logged.
3. **Mixed sold + unsold:** exactly one command.
4. **Lookup failure** (500, 429, timeout, connection refused, bad JSON, missing EPC in the reply): `silent` sends no
   command, `alarm` sends one, and the next packet is still handled promptly.
5. **Reply failure:**
   - no reply → unconfirmed, connection stays open, the next alarm still works
   - flag-fail reply → logged with code 10
   - late reply → labelled late
   - tag packet before the reply → tags processed, reply still recognised
   - reply and tag in one write → logged as unclassified, connection stays open
6. **Repeated reads:** the same unsold EPC across 5 packets never has more than one command outstanding. A second
   pass inside the 30 s dashboard cooldown still beeps.
7. **Continued dashboard reporting:**
   - A 10 s slow `/alarms` does not delay the beep beyond the lookup timeout.
   - When the lookup service is down, `/alarms` posts continue.
   - With two gates, only the gate that read the tag gets the command.
8. **Config and handshake:**
   - New keys: defaults and validation errors.
   - `selective` without `serverUrl` stops startup.
   - In selective mode only server control is sent at connect.
   - Legacy mode is unchanged.
9. **SDK manifest:** hashes match the vendor folder, and the startup check refuses a mixed set.
10. **Decision function:** table-driven unit tests for `Decide` covering every sold/unsold/unknown combination and
    both failure policies.

**Server** (Jest e2e):

- sold, unsold and mixed EPCs; normalization and de-duplication; request order kept
- 400 for an empty list, more than 100 EPCs, a non-string or an extra field
- 200 status
- no alarm rows written and the CCTV mock never called
- throttle limit enforced

**Real DLL, no gate** (x86, on the laptop):

- The five exports are present.
- Packing `{1,1,1,1}` reproduces the captured 9-byte command.
- Unpacking the captured reply returns 0.
- Unpacking the captured tag packet returns `E28069950000AA039398B5EA` on antenna 4.
- The two unpackers reject each other's packets (the gating prerequisite in section 6).

## 10. Spare-laptop hardware sequence

**Automated, on the laptop:**

- **A1.** Laptop prerequisites: Smart App Control not blocking the DLLs, x86 VC++ Redistributable installed,
  firewall allows the app.
- **A2.** Run the bridge and server automated tests.
- **A3.** Run the real-DLL check. Stop here if the unpackers can't tell the packet types apart.

**Real gate:**

- **R1.** Close `HAX-demo.exe` and confirm port 9000 is free (`netstat -ano | findstr :9000`). The demo and the
  bridge cannot listen on the same port at the same time.
- **R2.** In the local config, set `listenPort` 9000, gate `10.10.0.87` named `GATE-TEST`, the server URL, and
  `physicalAlarm: "selective"`. Mark one test tag as sold.
- **R3.** Start the bridge and let the gate connect. Server control should succeed with **no beep** at connect.
- **R4.** Sold tag only: no beep, and the dashboard shows "ignored, sold".
- **R5.** Unsold tag: lights and beep, reply ret=0, and an alarm event with CCTV on the dashboard. Then both tags
  together: beep.
- **R6.** Hold the unsold tag in range for 30 s. Note the beep cadence (input for the repeated-reads decision); the
  dashboard should show one event.
- **R7.** After an alarm, carry a sold tag through. If it beeps, the command is a lasting enable, not a one-shot.
  **This changes the design.**
- **R8.** Sell the unsold tag and walk it through immediately: no beep, which shows there's no stale cache.
- **R9.** Stop the server and walk the unsold tag: behaviour follows the failure policy. Restart the server:
  recovery happens without restarting the bridge.
- **R10.** Test channels `[1,0,0,0]`, `[0,1,0,0]`, `[0,0,1,0]`, `[0,0,0,1]` and record what each does.
- **R11.** Power-cycle the gate: it reconnects with no beep.
- **R12.** Keep `test.log` for HAX.

Legacy mode on a real gate isn't part of this sequence; it stays covered by automated tests only, unless a gate on
legacy firmware is available.
