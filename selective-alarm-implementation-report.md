# Selective gate alarm: implementation report

Prepared 2026-11-07.

Selective gate alarms are implemented in both repos, and all automated checks pass. Nothing is committed, pushed or
deployed, and no service was restarted. No active config, gate or firewall setting was changed. The hardware
acceptance tests are still to do, and **the running backend doesn't have the new route yet**: it was started at 15:50
with plain `nest start` from an old `dist`, so Geo must rebuild and restart it.

One finding changes what was assumed. Smart App Control (still on) did **not** block anything this session, so
HAX's real 2.1.2.0 DLL could be tested using the bytes captured in Jenny's demo:

- It reads the captured alarm reply and tag packet correctly. It also packs `{1,1,1,1}` into exactly the 9 bytes the
  demo sent.
- It never mistakes one packet type for the other, and it rejects every truncated packet.
- It **ignores extra bytes after a packet**. So if a reply and a tag packet arrive in one read and are handed to the
  DLL as one buffer, it reads only the first and the second is silently lost.

Because truncations are rejected, the first packet always ends at the shortest prefix the DLL accepts. The bridge
splits reads that way, using only the DLL's own checks, with no invented framing. The bridge's actual splitting code
was run against the real DLL: every split point, merged pairs and a three-read case all came out correct. The bridge
repeats this check at every start and only enables splitting if it passes; otherwise it treats each read as one
packet, as HAX's sample does, and warns.

## Server (`rfid-anti-theft-server`)

- **`POST /api/v1/anti-theft/known-epcs/check`** (`anti-theft.controller.ts`, `anti-theft.service.ts`, new
  `dto/check-known-epcs.dto.ts`):
  - It is read-only: one query against the sold list, no writes, no CCTV.
  - "Sold" means the EPC is in `known_epcs`, the same rule `/alarms` uses. EPCs are normalized the same way,
    duplicates removed, first-occurrence order kept.
  - It accepts 1–100 strings of up to 128 characters, rejects unknown fields, and returns HTTP 200.
  - It has no login, like `/alarms`, with the same TODO comment.
- **No schema change, no migration, no database reset.**
- **Rate limit:** 3000 requests/min per IP. The bridge sends at most one check at a time per gate, so its rate is the
  gate's packet rate. Each check is a single indexed read, so 3000/min should cover about 25 gates sending
  2 packets/s. A 429 is treated as "unknown", which by default sounds the gate.
- **Docs:** new `docs/gate-sold-check-api.md`, plus a pointer from `docs/gate-alarm-api.md`.

Example request and response:

```json
{ "epcs": ["E28069950000AA039398B5EA"] }
```

```json
{ "statusCode": 200, "message": "Success", "data": { "results": [ { "epc": "E28069950000AA039398B5EA", "sold": true } ] } }
```

## Bridge (`rfid-anti-theft-gate-bridge`)

- **Sold check** (`SoldCheck.cs`): its own HTTP client, 800 ms timeout, no retry.
  - A timeout, 429, 5xx, unreadable reply or missing result means **Unknown**. It is logged as
    `CHECK FAIL … sold status unknown`, never reported as unsold.
  - Decision per batch: any unsold tag means one alarm command; all sold means none; unknown follows
    `onSoldCheckFailure`, which defaults to `"alarm"`.
- **Per-gate coordinator** (`GateAlarmCoordinator.cs`):
  - It is separate from dashboard reporting, which is unchanged: every tag is still posted to `/alarms` with the same
    30 s cooldown, retries and CCTV.
  - At most one alarm command waits for a reply per gate. A decision made while one waits is covered by it, and is
    resent once if that command fails.
  - Later qualifying reads sound the gate again, even inside the 30 s dashboard cooldown.
  - Waiting work is capped at 4 batches of 100 EPCs per gate. Anything beyond that is logged `QUEUE FULL` and decided
    as unknown, never dropped.
- **Stream handling** (`GateStream.cs`, `V2GateServer.cs`):
  - One reader per socket; the coordinator is the only writer.
  - Incomplete bytes wait up to 500 ms for the rest of their packet.
  - Gate replies are logged as `ALARM OK`, `ALARM FAIL` (refused, no reply, or not sent), `ALARM LATE` or
    `ALARM BUSY`. The connection stays open after failures.
- **Modes** (`physicalAlarm`): `"off"` is the default and keeps the previous v2 behaviour, which sends the
  alarm-channels command once at connect. `"selective"` sends nothing at connect. Legacy mode is unchanged.
- **SDK selection** (`gate-tcp-test.csproj`, `SdkManifest.cs`, `sdk-manifests/*.sha256`):
  - `dotnet build -p:HaxSdk=2.1.2.0` builds into its own folder, `bin\hax-2.1.2.0\...`. Plain `dotnet build` stays
    legacy, in the usual folder.
  - The build checks every DLL against SHA-256 hashes of HAX's original files, so it can tell the two sets apart even
    though `common.dll` has the same version number in both.
  - The exe checks the DLLs again before loading, refuses a mismatch between `sdkMode` and the set it was built with,
    and refuses a mixed set.
  - Vendor files are untouched.
- **Self-check** (`SdkSelfCheck.cs`): runs at every v2 start and via `gate-tcp-test.exe --check-sdk`.
- Also updated: README, example config, and `.gitignore` (now excludes `gate-bridge.json`, so deployment addresses
  can't be committed by accident).

## Test results

| Check | Result |
|---|---|
| Server `node ./node_modules/jest/bin/jest.js` | 9/9 passed (4 new) |
| Server `node ./node_modules/jest/bin/jest.js --config ./test/jest-e2e.json --runInBand --forceExit` | 42/42 passed, including 16 new and a 3000-request rate-limit test |
| Server typecheck and lint of changed files | clean |
| Bridge `dotnet build` and `dotnet build -p:HaxSdk=2.1.2.0` | 0 warnings, 0 errors |
| Bridge build with a mixed `common.dll`, or `-p:HaxSdk=bogus` | fails with a clear error, as intended |
| Bridge `dotnet test` (in `gate-tcp-test.Tests`) | 213/213 passed, run twice |
| Real DLL: `bin\hax-2.1.2.0\...\gate-tcp-test.exe --check-sdk` | all checks pass, exit 0 |
| Startup smoke tests in scratch copies (real DLLs) | v2 selective starts; mode/set mismatch refused; mixed DLL refused; legacy mode starts and stops |

The server e2e tests ran against the local dev database (`localhost:5432/muji_eas`). They use random EPCs and delete
them afterwards.

The bridge tests cover every requested case: all sold, unsold, mixed and normalized duplicates; timeout, 429, 5xx,
malformed and missing results; slow CCTV/reporting; repeated reads inside the dashboard cooldown and multiple gates;
alarm rejection, missing and late replies, interleaved tags and split/merged TCP input; bounded queues and overflow;
configuration validation, SDK selection, dependency mixing and legacy regression. They use a fake SDK, so they test
the bridge's logic, not HAX's protocol; only the `--check-sdk` result is evidence about the real DLL. Nothing was
blocked by security this session.

## Remaining risks

1. **Release gate:** whether the alarm command is one-shot or switches on a lasting alarm mode is unknown. If it is
   lasting, selective mode doesn't work as designed.
2. The meaning of each alarm channel is unverified; only `[1,1,1,1]` has been tested.
3. The server-control handshake with the 2.1.2.0 DLL hasn't been run against a gate, because no gate reply was
   captured. The new delivery also has no `config.ini`; the DLL loaded and passed the checks without it.
4. The boundary checks only covered the captured packet types: one single-tag packet and the alarm reply. Packets the
   DLL can't read are logged as `BAD PACKET` and do **not** sound the gate. That choice avoids beeping on unknown
   packet types such as heartbeats, and it needs approval. Tag data the DLL reads but whose EPC is missing does count
   as unknown and sounds the gate.
5. Because failure means "alarm", a backend outage or a 429 makes sold tags beep too. That is the approved behaviour,
   but it will be noticeable.
6. How the gate behaves when an alarm command arrives while it is sending tags is unverified on hardware.

## Geo's manual steps

1. **Backend:** in `rfid-anti-theft-server`, run `bun run build`, then restart the backend the way it normally runs
   (it currently runs `nest start` on port 8000). Then check the new route:

   ```
   curl.exe -X POST http://10.10.0.217:8000/api/v1/anti-theft/known-epcs/check -H "Content-Type: application/json" -d '{"epcs":["E28069950000AA039398B5EA"]}'
   ```

   It should return 200. Until this works, selective mode would beep on every tag.
2. **Widget:** in `hax7700-widget\settings.json`, set `"serverUrl": "http://10.10.0.217:8000"`. It is currently
   `http://10.242.70.113:8000`. Don't add `/api/v1`.
3. **Bridge config:** copy `gate-tcp-test\gate-bridge.example.json` to `gate-tcp-test\gate-bridge.json` and set:
   - `"serverUrl": "http://10.10.0.217:8000"`
   - `"gates": { "10.10.0.87": "GATE-1" }`
   - `"sdkMode": "v2"`, `"listenIp": "0.0.0.0"`, `"listenPort": 9000`
   - `"physicalAlarm": "selective"` (keep the other keys as in the example)
4. **Build and check:** in `gate-tcp-test`, run `dotnet build -p:HaxSdk=2.1.2.0`, then
   `bin\hax-2.1.2.0\Debug\net8.0\win-x86\gate-tcp-test.exe --check-sdk`. Expect "packet boundaries verified yes".
5. **Port and firewall:** close `HAX-demo.exe`. Confirm nothing is using port 9000 with
   `netstat -ano | findstr :9000`. Allow inbound TCP 9000 for that exe in the firewall.
6. **Gate:** start the bridge first. Then set the USR-C215 TCP-client destination to `10.10.0.217:9000`, leaving the
   jumper unchanged.

## Real-gate acceptance checklist

1. **Connect:** `GATE OPEN`, then `server control: ok`. No alarm command is sent and the gate doesn't beep.
2. **Sold tag:** `NO ALARM … all 1 tag(s) sold`, no beep; the dashboard shows "ignored, sold".
3. **Unsold tag:** `GATE ALARM unsold`, then `ALARM CMD`, then `ALARM OK`. Lights and beep; a dashboard event with a
   CCTV snapshot.
4. **Mixed (sold and unsold together):** one command per packet, and the gate beeps.
5. **Newly sold:** sell the unsold tag through the widget, then walk it through immediately. It should stay silent.
6. **Lookup failure:** stop the backend and pass a sold tag. Expect `CHECK FAIL`, then
   `GATE ALARM sold status unknown`, and a beep. Restart the backend: the same tag should be silent again without
   restarting the bridge.
7. **Reconnect:** power-cycle the gate. It reconnects with no beep, and steps 2–3 still behave the same.
8. **Repeated passes:** pass the unsold tag 3 times within 30 s. It should beep every time, with one dashboard event.
   Also note the beep cadence while the tag stays in range.
9. **Gate: one-shot or lasting alarm.** After an alarm, remove the unsold tags and let the alarm finish. Then pass only
   a sold tag, and also leave the gate idle. If it beeps while the log shows `NO ALARM` and no `ALARM CMD`, the command
   switches on a lasting alarm mode. Don't release selective mode in that case; contact HAX.
10. Keep `test.log` from next to the exe for HAX.

## Other notes

- The earlier plan file `selective-gate-alarm-plan.md` is still untracked at the bridge repo root.
- Smart App Control is still on but no longer blocks these DLLs; this may change between days.
