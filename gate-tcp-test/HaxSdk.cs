// HAX's packet interface for v2 mode (sdk-v2\hax_7k_sdk.h). The DLL only builds and parses packets; this app owns
// every socket (V2GateServer.cs). Nothing in this file touches card_7k.dll: NativeHaxSdk.cs binds IHaxSdk to the
// real DLL, and the tests use a fake, so the 64-bit test project can compile this file directly.
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GateTcpTest;

// struct device_alarm_config, 4 bytes: one switch per alarm channel, 0 = off, 1 = on.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct DeviceAlarmConfig
{
    public byte Channel1, Channel2, Channel3, Channel4;

    public static DeviceAlarmConfig From(IReadOnlyList<byte> channels) => new()
    {
        Channel1 = channels[0], Channel2 = channels[1], Channel3 = channels[2], Channel4 = channels[3],
    };

    public override readonly string ToString() => $"[{Channel1},{Channel2},{Channel3},{Channel4}]";
}

// The five Device_* functions, one to one. Each returns a HaxError code, 0 = ok.
// Pack* only fill outBuf (outLen = bytes written); nothing is sent. UnPack* never change the bytes they are given.
interface IHaxSdk
{
    int PackServerControl(int value, Span<byte> outBuf, out int outLen);
    int UnPackServerControl(ReadOnlySpan<byte> reply);
    int PackDeviceAlarm(DeviceAlarmConfig config, Span<byte> outBuf, out int outLen);
    int UnPackDeviceAlarm(ReadOnlySpan<byte> reply);
    // On 0, jsonBuf holds UTF-8 JSON and jsonLen its length. On BufferTooSmall, jsonLen is the size needed.
    int UnPackEpcId(ReadOnlySpan<byte> packet, Span<byte> jsonBuf, out int jsonLen);
}

// enum device_error_code
static class HaxError
{
    public const int Ok = 0;
    public const int InvalidParam = 1;
    public const int BufferTooSmall = 2;
    public const int PackFailed = 3;
    public const int UnpackFailed = 4;
    public const int FlagFail = 10;
    public const int FlagAbnormal = 12;
    public const int FlagDuplicate = 13;
    public const int FlagUnknown = 14;
    public const int Network = 20;

    public static string Text(int code) => code switch
    {
        Ok => "ok",
        InvalidParam => "invalid param",
        BufferTooSmall => "buffer too small",
        PackFailed => "pack failed",
        UnpackFailed => "unpack failed (header/length/CRC)",
        FlagFail => "device flag=0 fail",
        FlagAbnormal => "device flag=2 abnormal",
        FlagDuplicate => "device flag=3 duplicate",
        FlagUnknown => "unknown flag",
        Network => "EPC verify could not reach the server",
        _ => $"unknown error code {code}",
    };
}

record EpcTag(int? AntId, string Epc);

static class EpcJson
{
    // Reads Device_UnPackEpcId's JSON, e.g. [{"ant_id":1,"epc_id":"E2000017..."}]. Problem is null when everything
    // was read. An item without an epc_id is skipped and named in Problem; the packet's other tags still count.
    // EPCs are trimmed and upper-cased, the same form legacy mode sends (Convert.ToHexString).
    public static (IReadOnlyList<EpcTag> Tags, string? Problem) Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return ([], $"not valid JSON ({ex.Message})"); }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return ([], $"expected a JSON array, got {root.ValueKind}");

            var tags = new List<EpcTag>();
            var skipped = new List<int>();
            int item = 0;
            foreach (JsonElement e in root.EnumerateArray())
            {
                item++;
                string? epc = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("epc_id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String ? id.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(epc))
                {
                    skipped.Add(item);
                    continue;
                }
                int? ant = e.TryGetProperty("ant_id", out JsonElement a) && a.ValueKind == JsonValueKind.Number
                    && a.TryGetInt32(out int n) ? n : null;
                tags.Add(new EpcTag(ant, epc.ToUpperInvariant()));
            }
            return (tags, skipped.Count switch
            {
                0 => null,
                1 => $"item {skipped[0]} of {item} has no epc_id, skipped",
                _ => $"items {string.Join(", ", skipped)} of {item} have no epc_id, skipped",
            });
        }
    }
}
