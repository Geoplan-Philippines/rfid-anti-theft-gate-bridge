// PacketAssembler with the fake SDK: how split, joined and interleaved TCP reads are handled in both modes.
// The fake SDK's packets are self-delimiting by construction. Whether HAX's real DLL behaves the same is what
// SdkSelfCheck checks on the real DLL; these tests do not prove it.
namespace GateTcpTest.Tests;

public class PacketAssemblerTests
{
    const string A = "E28069950000AA039398B5EA";
    const string B = "3BE100003744AA2700000769";
    static readonly byte[] Reply = FakeHaxSdk.Reply(FakeHaxSdk.DeviceAlarm);

    static PacketAssembler Strict(bool reassemble = true) => new(new FakeHaxSdk(), reassemble);

    static string[] Epcs(GateMessage m) => ((TagMessage)m).Tags.Select(t => t.Epc).ToArray();

    [Fact]
    public void A_whole_tag_packet_and_a_whole_reply_are_read_in_both_modes()
    {
        foreach (bool reassemble in new[] { false, true })
        {
            PacketAssembler p = Strict(reassemble);
            Assert.Equal([A, B], Epcs(Assert.Single(p.Feed(FakeHaxSdk.TagPacket(A, B), false))));
            Assert.Equal(HaxError.Ok, Assert.IsType<AlarmReplyMessage>(Assert.Single(p.Feed(Reply, true))).Code);
            Assert.False(p.HasPending);
        }
    }

    [Fact]
    public void Documented_mode_reports_a_split_or_joined_read_as_unreadable_and_does_not_guess()
    {
        PacketAssembler p = Strict(reassemble: false);
        byte[] tag = FakeHaxSdk.TagPacket(A);

        Assert.IsType<UnreadableMessage>(Assert.Single(p.Feed(tag[..5], false)));
        Assert.IsType<UnreadableMessage>(Assert.Single(p.Feed(tag[5..], false)));
        Assert.IsType<UnreadableMessage>(Assert.Single(p.Feed([.. Reply, .. tag], true)));
        Assert.False(p.HasPending);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(14)]
    public void A_packet_split_over_reads_is_joined_once_the_sdk_accepts_it(int cut)
    {
        PacketAssembler p = Strict();
        byte[] tag = FakeHaxSdk.TagPacket(A, B);

        Assert.Empty(p.Feed(tag[..cut], false));
        Assert.True(p.HasPending);
        Assert.Empty(p.Feed(tag[cut..(cut + 3)], false));
        Assert.Equal([A, B], Epcs(Assert.Single(p.Feed(tag[(cut + 3)..], false))));
        Assert.False(p.HasPending);
    }

    [Fact]
    public void A_reply_and_a_tag_packet_in_one_read_come_out_in_order_either_way_round()
    {
        PacketAssembler p = Strict();
        byte[] tag = FakeHaxSdk.TagPacket(A);

        IReadOnlyList<GateMessage> replyFirst = p.Feed([.. Reply, .. tag], true);
        Assert.IsType<AlarmReplyMessage>(replyFirst[0]);
        Assert.Equal([A], Epcs(replyFirst[1]));

        IReadOnlyList<GateMessage> tagFirst = p.Feed([.. tag, .. Reply], true);
        Assert.Equal([A], Epcs(tagFirst[0]));
        Assert.IsType<AlarmReplyMessage>(tagFirst[1]);
        Assert.False(p.HasPending);
    }

    // HAX's 2.1.2.0 DLL ignores bytes after a packet (gate-tcp-test.exe --check-sdk on the captured packets): the lenient
    // fake behaves the same. The shortest accepted prefix still cuts every packet out.
    [Fact]
    public void With_an_sdk_that_ignores_bytes_after_a_packet_every_packet_of_a_joined_read_is_still_read()
    {
        var p = new PacketAssembler(new FakeHaxSdk(lenient: true), reassemble: true);

        IReadOnlyList<GateMessage> m = p.Feed([.. FakeHaxSdk.TagPacket(A), .. Reply, .. FakeHaxSdk.TagPacket(B)], true);

        Assert.Equal(3, m.Count);
        Assert.Equal([A], Epcs(m[0]));
        Assert.Equal(HaxError.Ok, Assert.IsType<AlarmReplyMessage>(m[1]).Code);
        Assert.Equal([B], Epcs(m[2]));
        Assert.False(p.HasPending);
    }

    [Fact]
    public void Documented_mode_with_such_an_sdk_sees_only_the_first_packet_of_a_joined_read()
    {
        // The limitation reassembly removes: without it, the reply after the tag packet is never seen.
        var p = new PacketAssembler(new FakeHaxSdk(lenient: true), reassemble: false);
        Assert.Equal([A], Epcs(Assert.Single(p.Feed([.. FakeHaxSdk.TagPacket(A), .. Reply], true))));
    }

    [Fact]
    public void Two_tag_packets_and_the_start_of_a_third_in_one_read()
    {
        PacketAssembler p = Strict();
        byte[] third = FakeHaxSdk.TagPacket(B);

        IReadOnlyList<GateMessage> first = p.Feed([.. FakeHaxSdk.TagPacket(A), .. FakeHaxSdk.TagPacket(B), .. third[..4]], false);
        Assert.Equal(2, first.Count);
        Assert.True(p.HasPending);
        Assert.Equal([B], Epcs(Assert.Single(p.Feed(third[4..], false))));
    }

    [Fact]
    public void Rejected_bytes_then_a_packet_report_the_bytes_and_keep_the_packet()
    {
        PacketAssembler p = Strict();
        byte[] tag = FakeHaxSdk.TagPacket(A);

        Assert.Empty(p.Feed([0x00, 0x01, 0x02], false));                 // might be the start of something: waits
        IReadOnlyList<GateMessage> next = p.Feed(tag, false);              // not its continuation: reported, packet kept
        Assert.Equal([0x00, 0x01, 0x02], Assert.IsType<UnreadableMessage>(next[0]).Bytes);
        Assert.Equal([A], Epcs(next[1]));

        IReadOnlyList<GateMessage> joined = p.Feed([0x07, 0x07, .. tag], false);
        Assert.Equal([0x07, 0x07], Assert.IsType<UnreadableMessage>(joined[0]).Bytes);
        Assert.Equal([A], Epcs(joined[1]));
    }

    [Fact]
    public void An_incomplete_packet_is_reported_when_it_is_flushed()
    {
        PacketAssembler p = Strict();
        byte[] tag = FakeHaxSdk.TagPacket(A);

        Assert.Empty(p.Feed(tag[..6], false));
        Assert.Equal(tag[..6], Assert.IsType<UnreadableMessage>(p.Flush()).Bytes);
        Assert.Null(p.Flush());
    }

    [Fact]
    public void Pending_bytes_never_grow_past_one_maximum_read()
    {
        PacketAssembler p = Strict();
        // A tag packet header that promises more tags than will ever come keeps waiting, until the limit.
        byte[] start = [FakeHaxSdk.Tags, 0xFF, 0xFF];
        Assert.Empty(p.Feed(start, false));
        IReadOnlyList<GateMessage> out1 = p.Feed(new byte[PacketAssembler.MaxPendingBytes], false);
        Assert.IsType<UnreadableMessage>(Assert.Single(out1));
        Assert.False(p.HasPending);
    }

    [Fact]
    public void A_reply_with_no_command_waiting_is_still_recognized_as_a_reply()
    {
        PacketAssembler p = Strict();
        Assert.Equal(HaxError.FlagFail, Assert.IsType<AlarmReplyMessage>(Assert.Single(p.Feed(FakeHaxSdk.Reply(FakeHaxSdk.DeviceAlarm, 0), false))).Code);
    }

    [Fact]
    public void A_tag_item_without_an_epc_is_counted_as_unreadable()
    {
        PacketAssembler p = Strict();
        TagMessage m = Assert.IsType<TagMessage>(Assert.Single(p.Feed(
            FakeHaxSdk.RawJsonPacket($$"""[{"ant_id":4,"epc_id":"{{A}}"},{"ant_id":4}]"""), false)));
        Assert.Equal([A], m.Tags.Select(t => t.Epc));
        Assert.Equal(1, m.UnreadableTags);

        TagMessage broken = Assert.IsType<TagMessage>(Assert.Single(p.Feed(FakeHaxSdk.RawJsonPacket("[{"), false)));
        Assert.Empty(broken.Tags);
        Assert.Equal(1, broken.UnreadableTags);
    }
}
