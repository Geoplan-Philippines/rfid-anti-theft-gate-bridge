// Turns the bytes read from one gate socket into gate messages: tag packets and alarm replies. Both share the stream.
//
// HAX documents no frame header or length field ("one recv is treated as one packet", readme.txt), and this app
// invents none. A TCP read is still not a packet boundary, so there are two modes:
//   - Documented (reassemble = false): each read is classified as it is, as HAX's sample does. A packet split over two
//     reads, or two packets in one read, is not handled: it is reported as BAD PACKET, or (with a DLL that ignores
//     bytes after a packet) only the first packet of the read is seen.
//   - SDK-validated (reassemble = true): a run of bytes is cut at the shortest prefix the SDK's own unpack functions
//     accept (they check header, length and CRC, hax_7k_sdk.h); bytes it rejects wait for the next read. That is only
//     correct when the DLL rejects every truncated packet, never takes one packet type for the other, and either
//     rejects or ignores bytes after a packet. SdkSelfCheck verifies exactly that on bytes captured from the gate;
//     Program turns this mode on only when that check passed on the loaded DLL.
// Nothing in this file touches card_7k.dll, so the test project compiles it directly.
namespace GateTcpTest;

abstract record GateMessage(byte[] Bytes);

// Device_UnPackEpcId accepted the bytes. Problem is set when its JSON could not be fully read; UnreadableTags counts
// the tags in it that have no usable EPC (1 when the JSON was not readable at all).
sealed record TagMessage(byte[] Bytes, string Json, IReadOnlyList<EpcTag> Tags, string? Problem, int UnreadableTags) : GateMessage(Bytes);

// Device_UnPackDeviceAlarm recognised the bytes as the gate's reply to an alarm command: 0 = accepted,
// 10/12/13/14 = the gate answered but refused.
sealed record AlarmReplyMessage(byte[] Bytes, int Code) : GateMessage(Bytes);

// Neither unpack function accepted the bytes. Code is Device_UnPackEpcId's answer.
sealed record UnreadableMessage(byte[] Bytes, int Code) : GateMessage(Bytes);

sealed class PacketAssembler(IHaxSdk sdk, bool reassemble)
{
    public const int MaxPendingBytes = V2GateServer.TagBufferSize; // never hold more than one maximum-size read
    const int MaxScanBytes = 4096;                                   // longest prefix tried, furthest start tried

    readonly byte[] jsonBuf = new byte[V2GateServer.JsonBufferSize]; // reused: a prefix scan unpacks many times
    byte[] pending = [];

    public bool Reassembles => reassemble;
    public bool HasPending => pending.Length > 0;

    // alarmOutstanding: an alarm command is waiting for its reply, so a reply is the likelier packet and is tried first.
    public IReadOnlyList<GateMessage> Feed(ReadOnlySpan<byte> chunk, bool alarmOutstanding)
    {
        var output = new List<GateMessage>();
        byte[] bytes = chunk.ToArray();
        if (!reassemble)
        {
            output.Add(Classify(bytes, alarmOutstanding) ?? Unreadable(bytes));
            return output;
        }

        if (pending.Length == 0) pending = bytes;
        else
        {
            byte[] joined = [.. pending, .. bytes];
            if (FirstPacket(joined, alarmOutstanding).Message == null && FirstPacket(bytes, alarmOutstanding).Message != null)
            {
                output.Add(Unreadable(pending)); // what waited was not the start of a packet: this read starts a new one
                pending = bytes;
            }
            else pending = joined;               // the rest of a packet that arrived in pieces, or still incomplete
        }
        Drain(output, alarmOutstanding);
        return output;
    }

    // Called when nothing more arrived in time: whatever still waits cannot be completed.
    public GateMessage? Flush()
    {
        if (pending.Length == 0) return null;
        GateMessage rest = Unreadable(pending);
        pending = [];
        return rest;
    }

    void Drain(List<GateMessage> output, bool alarmOutstanding)
    {
        while (pending.Length > 0)
        {
            (int cut, GateMessage? first) = FirstPacket(pending, alarmOutstanding);
            if (first != null)
            {
                output.Add(first);
                pending = pending[cut..];
                continue;
            }
            // Bytes the SDK rejects, then a packet: report the bytes, go on from where the packet starts.
            int start = FirstPacketStart(alarmOutstanding);
            if (start > 0)
            {
                output.Add(Unreadable(pending[..start]));
                pending = pending[start..];
                continue;
            }
            break; // probably the first part of a packet: wait for the rest (or for Flush)
        }
        if (pending.Length > MaxPendingBytes)
        {
            output.Add(Unreadable(pending));
            pending = [];
        }
    }

    // The shortest prefix of bytes the SDK accepts, and what it is. Every truncated packet is rejected (verified by
    // SdkSelfCheck), so that prefix is exactly one packet, whether or not more bytes follow it.
    (int Cut, GateMessage? Message) FirstPacket(byte[] bytes, bool alarmOutstanding)
    {
        if (Classify(bytes, alarmOutstanding) is GateMessage whole)
        {
            // Fast path: accepted, and accepted no more once the last byte is gone, so nothing follows the packet.
            if (bytes.Length == 1 || Classify(bytes[..^1], alarmOutstanding) == null) return (bytes.Length, whole);
        }
        int max = Math.Min(bytes.Length, MaxScanBytes);
        for (int cut = 1; cut <= max; cut++)
            if (Classify(bytes[..cut], alarmOutstanding) is GateMessage m) return (cut, m);
        return (0, null);
    }

    // Where the first packet in pending starts, after bytes the SDK rejects; 0 when none is found.
    int FirstPacketStart(bool alarmOutstanding)
    {
        int last = Math.Min(pending.Length - 1, MaxScanBytes);
        for (int start = 1; start <= last; start++)
            if (Classify(pending[start..], alarmOutstanding) != null) return start;
        return 0;
    }

    // Asks only the SDK. Null when neither unpack function accepts the bytes.
    GateMessage? Classify(byte[] bytes, bool alarmOutstanding)
    {
        if (alarmOutstanding && ReplyCode(bytes) is int early) return new AlarmReplyMessage(bytes, early);
        if (Tags(bytes) is TagMessage tags) return tags;
        if (!alarmOutstanding && ReplyCode(bytes) is int late) return new AlarmReplyMessage(bytes, late);
        return null;
    }

    int? ReplyCode(byte[] bytes)
    {
        int rc = sdk.UnPackDeviceAlarm(bytes);
        return HaxError.IsDeviceAnswer(rc) ? rc : null;
    }

    TagMessage? Tags(byte[] bytes)
    {
        int rc = V2GateServer.UnpackEpcJson(sdk, bytes, jsonBuf, out string json);
        if (rc != HaxError.Ok) return null;
        (IReadOnlyList<EpcTag> tags, string? problem, int unreadable) = EpcJson.ParseCounting(json);
        return new TagMessage(bytes, json, tags, problem, unreadable);
    }

    UnreadableMessage Unreadable(byte[] bytes)
    {
        int rc = V2GateServer.UnpackEpcJson(sdk, bytes, jsonBuf, out _);
        return new UnreadableMessage(bytes, rc == HaxError.Ok ? HaxError.UnpackFailed : rc);
    }
}
