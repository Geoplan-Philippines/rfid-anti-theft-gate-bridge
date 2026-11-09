// Checks the loaded card_7k.dll against packets captured from the real gate, without a gate. Run at every v2 start
// and by "gate-tcp-test.exe --check-sdk". It answers two questions:
//   1. Decode: does this DLL read the gate's packets at all (the captured tag packet and alarm reply)?
//   2. Boundaries: can a run of bytes be cut into packets using only this DLL's own checks? That needs:
//      - neither packet type is taken for the other;
//      - every truncated packet is rejected, so the shortest prefix the DLL accepts is exactly one packet;
//      - bytes after a packet either make the DLL reject the whole run, or are ignored, with the leading packet read
//        exactly as it reads alone (HAX's 2.1.2.0 DLL does the latter: it apparently reads its own length).
//      PacketAssembler (GateStream.cs) only cuts and joins TCP reads when all of that holds; HAX documents no
//      framing, so this is the only evidence for it.
// A fake SDK passes or fails these checks however it was written: only a run against HAX's real DLL counts.
// Nothing in this file touches card_7k.dll, so the test project compiles it directly.
namespace GateTcpTest;

record SdkFixtures(DeviceAlarmConfig Channels, byte[] AlarmCommand, byte[] AlarmReply, byte[] TagPacket, int TagAntenna, string TagEpc);

static class CapturedPackets
{
    // Jenny's HAX-demo.exe session with the RFID7000 gate, firmware 7000202611040961, over its USR-C215 Wi-Fi module
    // (console screenshot, 2026-11-07). Each byte string was printed identically on every repeat.
    //   [send] device_alarm 9 bytes   -> Device_PackDeviceAlarm({1,1,1,1})
    //   [recv] 9 bytes                -> Device_UnPackDeviceAlarm ret=0 (ok); lights and beep confirmed by the operator
    //   [recv] 37 bytes               -> epc json [{"ant_id":4,"epc_id":"E28069950000AA039398B5EA"}]
    public static readonly SdkFixtures Gate7000 = new(
        new DeviceAlarmConfig { Channel1 = 1, Channel2 = 1, Channel3 = 1, Channel4 = 1 },
        Convert.FromHexString("DB826F2D1F236C9240"),
        Convert.FromHexString("DB826F2D1F236256C1"),
        Convert.FromHexString("DB82433F1F2362E33369BB327C681BFDE988041EA560E68D75DC636614C832A296BE47D4C4"),
        4,
        "E28069950000AA039398B5EA");
}

enum SdkCheckGroup { Decode, Boundaries, Info }

record SdkCheckItem(SdkCheckGroup Group, string Name, bool Passed, string Detail);

sealed record SdkSelfCheckReport(IReadOnlyList<SdkCheckItem> Items)
{
    public bool Decodes => Items.Where(i => i.Group == SdkCheckGroup.Decode).All(i => i.Passed);
    public bool BoundariesVerified => Decodes && Items.Where(i => i.Group == SdkCheckGroup.Boundaries).All(i => i.Passed);
}

static class SdkSelfCheck
{
    public static SdkSelfCheckReport Run(IHaxSdk sdk, SdkFixtures f)
    {
        var items = new List<SdkCheckItem>();
        void Add(SdkCheckGroup group, string name, bool passed, string detail) => items.Add(new(group, name, passed, detail));

        int reply = sdk.UnPackDeviceAlarm(f.AlarmReply);
        Add(SdkCheckGroup.Decode, "captured alarm reply unpacks", reply == HaxError.Ok, Code(reply));

        int tagRc = UnpackJson(sdk, f.TagPacket, out string tagJson);
        (IReadOnlyList<EpcTag> tags, string? problem, _) = tagRc == HaxError.Ok ? EpcJson.ParseCounting(tagJson) : ([], null, 0);
        bool tagOk = tagRc == HaxError.Ok && problem == null && tags.Count == 1
                     && tags[0].Epc == f.TagEpc && tags[0].AntId == f.TagAntenna;
        Add(SdkCheckGroup.Decode, "captured tag packet unpacks", tagOk, tagRc == HaxError.Ok ? tagJson : Code(tagRc));

        var packet = new byte[V2GateServer.PacketBufferSize];
        int packRc = sdk.PackDeviceAlarm(f.Channels, packet, out int len);
        bool same = packRc == HaxError.Ok && len > 0 && len <= packet.Length && packet.AsSpan(0, len).SequenceEqual(f.AlarmCommand);
        Add(SdkCheckGroup.Info, $"alarm command {f.Channels} matches the captured bytes", same,
            packRc != HaxError.Ok ? Code(packRc) : V2GateServer.Hex(packet.AsSpan(0, Math.Clamp(len, 0, packet.Length))));

        int crossReply = sdk.UnPackDeviceAlarm(f.TagPacket);
        Add(SdkCheckGroup.Boundaries, "tag packet is not taken for an alarm reply", !HaxError.IsDeviceAnswer(crossReply), Code(crossReply));
        int crossTag = UnpackJson(sdk, f.AlarmReply, out _);
        Add(SdkCheckGroup.Boundaries, "alarm reply is not taken for a tag packet", crossTag != HaxError.Ok, Code(crossTag));

        string? truncated = FirstAccepted(sdk, Prefixes(f.TagPacket).Concat(Prefixes(f.AlarmReply)));
        Add(SdkCheckGroup.Boundaries, "every truncated packet is rejected", truncated == null, truncated ?? "all rejected");

        (string Name, byte[] Input, bool LeadIsReply)[] joined =
        [
            ("tag packet + alarm reply", [.. f.TagPacket, .. f.AlarmReply], false),
            ("alarm reply + tag packet", [.. f.AlarmReply, .. f.TagPacket], true),
            ("tag packet + tag packet", [.. f.TagPacket, .. f.TagPacket], false),
            ("alarm reply + alarm reply", [.. f.AlarmReply, .. f.AlarmReply], true),
            ("tag packet + 00", [.. f.TagPacket, 0x00], false),
            ("alarm reply + 00", [.. f.AlarmReply, 0x00], true),
        ];
        var outcomes = new List<string>();
        bool consistent = true, ignored = false;
        foreach ((string name, byte[] input, bool leadIsReply) in joined)
        {
            int asReply = sdk.UnPackDeviceAlarm(input);
            int asTag = UnpackJson(sdk, input, out string json);
            bool replyAccepted = HaxError.IsDeviceAnswer(asReply), tagAccepted = asTag == HaxError.Ok;
            string outcome;
            if (!replyAccepted && !tagAccepted) outcome = "rejected";
            else if (leadIsReply ? replyAccepted && asReply == reply && !tagAccepted : tagAccepted && json == tagJson && !replyAccepted)
            {
                outcome = "read as the leading packet alone";
                ignored = true;
            }
            else
            {
                outcome = $"read differently (as a reply: {Code(asReply)}; as tags: {(tagAccepted ? json : Code(asTag))})";
                consistent = false;
            }
            outcomes.Add($"{name}: {outcome}");
        }
        Add(SdkCheckGroup.Boundaries, "bytes after a packet are rejected, or ignored with the packet read as alone", consistent,
            string.Join("; ", outcomes));
        Add(SdkCheckGroup.Info, "bytes after a packet are ignored by the DLL", !ignored,
            ignored ? "yes: a read holding two packets unpacks as the first only, so the shortest accepted prefix is the cut"
                    : "no: the DLL rejects them");

        string? stream = StreamProblem(sdk, f);
        Add(SdkCheckGroup.Boundaries, "split and joined reads of these packets come out whole and in order", stream == null,
            stream ?? $"every cut of each packet, joined pairs and rejected leading bytes: all read correctly");

        return new SdkSelfCheckReport(items);
    }

    // Runs PacketAssembler, the code the gate connection uses, over the captured packets cut and joined in every
    // way that matters. Null when every arrangement reads back as the packets that were sent.
    static string? StreamProblem(IHaxSdk sdk, SdkFixtures f)
    {
        string tag = $"tags {f.TagEpc}", reply = "reply 0";
        var cases = new List<(string Name, byte[][] Reads, string Expected)>();
        for (int cut = 1; cut < f.TagPacket.Length; cut++)
            cases.Add(($"tag packet cut at {cut}", [f.TagPacket[..cut], f.TagPacket[cut..]], tag));
        for (int cut = 1; cut < f.AlarmReply.Length; cut++)
            cases.Add(($"alarm reply cut at {cut}", [f.AlarmReply[..cut], f.AlarmReply[cut..]], reply));
        byte[] three = [.. f.TagPacket, .. f.TagPacket, .. f.AlarmReply];
        int a = f.TagPacket.Length / 2, b = f.TagPacket.Length + f.TagPacket.Length / 2 + 1;
        cases.Add(("alarm reply + tag packet in one read", [[.. f.AlarmReply, .. f.TagPacket]], $"{reply} | {tag}"));
        cases.Add(("tag packet + alarm reply in one read", [[.. f.TagPacket, .. f.AlarmReply]], $"{tag} | {reply}"));
        cases.Add(("two tag packets and a reply cut across three reads", [three[..a], three[a..b], three[b..]], $"{tag} | {tag} | {reply}"));
        cases.Add(("rejected bytes, then a tag packet, in one read", [[0x00, 0x01, 0x02, .. f.TagPacket]], $"unreadable 3 | {tag}"));
        cases.Add(("rejected bytes, then a tag packet, in two reads", [[0x00, 0x01, 0x02], f.TagPacket], $"unreadable 3 | {tag}"));

        foreach ((string name, byte[][] reads, string expected) in cases)
        {
            var assembler = new PacketAssembler(sdk, reassemble: true);
            var seen = new List<GateMessage>();
            foreach (byte[] read in reads) seen.AddRange(assembler.Feed(read, alarmOutstanding: true));
            if (assembler.Flush() is GateMessage rest) seen.Add(rest);
            string got = string.Join(" | ", seen.Select(Describe));
            if (got != expected) return $"{name}: expected \"{expected}\", got \"{got}\"";
        }
        return null;
    }

    static string Describe(GateMessage m) => m switch
    {
        TagMessage t => $"tags {string.Join(",", t.Tags.Select(x => x.Epc))}",
        AlarmReplyMessage r => $"reply {r.Code}",
        _ => $"unreadable {m.Bytes.Length}",
    };

    static IEnumerable<byte[]> Prefixes(byte[] packet)
    {
        for (int n = 1; n < packet.Length; n++) yield return packet[..n];
    }

    // The first input either unpack function accepts, in hex, or null.
    static string? FirstAccepted(IHaxSdk sdk, IEnumerable<byte[]> inputs)
    {
        foreach (byte[] input in inputs)
        {
            int asReply = sdk.UnPackDeviceAlarm(input);
            if (HaxError.IsDeviceAnswer(asReply)) return $"accepted as an alarm reply ({Code(asReply)}): {V2GateServer.Hex(input, 64)}";
            if (UnpackJson(sdk, input, out _) == HaxError.Ok) return $"accepted as a tag packet: {V2GateServer.Hex(input, 64)}";
        }
        return null;
    }

    static int UnpackJson(IHaxSdk sdk, byte[] input, out string json) =>
        V2GateServer.UnpackEpcJson(sdk, input, new byte[V2GateServer.JsonBufferSize], out json);

    static string Code(int rc) => $"{HaxError.Text(rc)}, code {rc}";
}
