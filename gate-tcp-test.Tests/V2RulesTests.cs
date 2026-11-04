// v2 mode's pure rules: the SDK's tag JSON, the buffer-too-small retry, error code texts, reader names from the
// gate socket's remote IP, and the v2 settings in gate-bridge.json.
using System.Net;

namespace GateTcpTest.Tests;

public class EpcJsonTests
{
    [Fact]
    public void One_tag()
    {
        (IReadOnlyList<EpcTag> tags, string? problem) = EpcJson.Parse("""[{"ant_id":1,"epc_id":"E2000017221101441890ABCD"}]""");
        Assert.Equal(new[] { new EpcTag(1, "E2000017221101441890ABCD") }, tags);
        Assert.Null(problem);
    }

    [Fact]
    public void Several_tags_keep_their_order_and_antennas()
    {
        (IReadOnlyList<EpcTag> tags, string? problem) = EpcJson.Parse(
            """[{"ant_id":1,"epc_id":"E2000017AA"},{"ant_id":3,"epc_id":"E2000017BB"},{"ant_id":4,"epc_id":"E2000017CC"}]""");
        Assert.Equal(new[] { new EpcTag(1, "E2000017AA"), new EpcTag(3, "E2000017BB"), new EpcTag(4, "E2000017CC") }, tags);
        Assert.Null(problem);
    }

    [Fact]
    public void Empty_array_is_no_tags_and_no_problem()
    {
        (IReadOnlyList<EpcTag> tags, string? problem) = EpcJson.Parse("[]");
        Assert.Empty(tags);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("", "not valid JSON")]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""[{"ant_id":1,"epc_id":"E2""", "not valid JSON")] // cut off
    [InlineData("""{"ant_id":1,"epc_id":"E2000017AA"}""", "expected a JSON array, got Object")]
    [InlineData("null", "expected a JSON array, got Null")]
    public void Bad_json_is_reported_and_gives_no_tags(string json, string problem)
    {
        (IReadOnlyList<EpcTag> tags, string? found) = EpcJson.Parse(json);
        Assert.Empty(tags);
        Assert.StartsWith(problem, found);
    }

    [Fact]
    public void Item_without_an_epc_is_skipped_and_the_other_tags_still_count()
    {
        (IReadOnlyList<EpcTag> tags, string? problem) = EpcJson.Parse(
            """[{"ant_id":1,"epc_id":"E2000017AA"},{"ant_id":2},{"ant_id":3,"epc_id":""},5,{"epc_id":"E2000017DD"}]""");
        Assert.Equal(new[] { new EpcTag(1, "E2000017AA"), new EpcTag(null, "E2000017DD") }, tags);
        Assert.Equal("items 2, 3, 4 of 5 have no epc_id, skipped", problem);
    }

    [Fact]
    public void Epc_is_trimmed_and_upper_cased_like_legacy_mode_sends_it() =>
        Assert.Equal("E2000017AABB", EpcJson.Parse("""[{"ant_id":1,"epc_id":" e2000017aabb "}]""").Tags.Single().Epc);
}

public class EpcUnpackTests
{
    const int First = V2GateServer.JsonBufferSize;

    [Fact]
    public void Json_that_fits_takes_one_call()
    {
        var sdk = new FakeHaxSdk();
        int rc = V2GateServer.UnpackEpcJson(sdk, FakeHaxSdk.TagPacket((2, "E2000017AABB")), new byte[First], out string json);
        Assert.Equal(HaxError.Ok, rc);
        Assert.Equal("""[{"ant_id":2,"epc_id":"E2000017AABB"}]""", json);
        Assert.Equal(new[] { First }, sdk.JsonBufferSizes);
    }

    [Fact]
    public void Buffer_too_small_is_retried_once_with_the_size_the_sdk_asked_for()
    {
        // 300 tags: a 4201-byte packet (fits one recv) whose JSON is over 14000 bytes (does not fit 10240).
        var sdk = new FakeHaxSdk();
        (byte, string)[] many = Enumerable.Range(0, 300).Select(i => ((byte)(i % 4 + 1), $"E28011700000020A1B2C{i:X4}")).ToArray();
        byte[] packet = FakeHaxSdk.TagPacket(many);

        int rc = V2GateServer.UnpackEpcJson(sdk, packet, new byte[First], out string json);

        Assert.Equal(HaxError.Ok, rc);
        int needed = System.Text.Encoding.UTF8.GetByteCount(json) + 1; // the JSON and its NUL
        Assert.True(needed > First, $"the JSON should not fit {First} bytes, it is {needed}");
        Assert.Equal(new[] { First, needed }, sdk.JsonBufferSizes);
        Assert.Equal(300, EpcJson.Parse(json).Tags.Count);
        Assert.Equal("E28011700000020A1B2C012B", EpcJson.Parse(json).Tags[299].Epc);
    }

    [Fact]
    public void Too_small_a_second_time_is_returned_not_retried_forever()
    {
        var calls = new List<int>();
        var sdk = new StubSdk((Span<byte> jsonBuf, out int jsonLen) =>
        {
            calls.Add(jsonBuf.Length);
            jsonLen = jsonBuf.Length * 2;
            return HaxError.BufferTooSmall;
        });
        Assert.Equal(HaxError.BufferTooSmall, V2GateServer.UnpackEpcJson(sdk, new byte[] { 1 }, new byte[First], out string json));
        Assert.Equal(new[] { First, First * 2 }, calls);
        Assert.Equal("", json);
    }

    [Theory]
    [InlineData(HaxError.UnpackFailed)]
    [InlineData(HaxError.Network)]
    [InlineData(99)]
    public void Other_codes_are_returned_as_they_are(int code)
    {
        var sdk = new FakeHaxSdk();
        Assert.Equal(code, V2GateServer.UnpackEpcJson(sdk, FakeHaxSdk.CodePacket((byte)code), new byte[First], out string json));
        Assert.Single(sdk.JsonBufferSizes); // no retry
        Assert.Equal("", json);
    }

    [Fact]
    public void Text_stops_at_a_nul_and_never_runs_past_the_buffer_whatever_length_the_sdk_says()
    {
        var sdk = new StubSdk((Span<byte> jsonBuf, out int jsonLen) =>
        {
            "[]\0junk"u8.CopyTo(jsonBuf);
            jsonLen = jsonBuf.Length + 50; // a DLL bug: longer than the buffer
            return HaxError.Ok;
        });
        Assert.Equal(HaxError.Ok, V2GateServer.UnpackEpcJson(sdk, new byte[] { 1 }, new byte[16], out string json));
        Assert.Equal("[]", json);
    }

    delegate int UnpackEpc(Span<byte> jsonBuf, out int jsonLen);

    sealed class StubSdk(UnpackEpc unpackEpc) : IHaxSdk
    {
        public int UnPackEpcId(ReadOnlySpan<byte> packet, Span<byte> jsonBuf, out int jsonLen) => unpackEpc(jsonBuf, out jsonLen);
        public int PackServerControl(int value, Span<byte> outBuf, out int outLen) => throw new NotSupportedException();
        public int UnPackServerControl(ReadOnlySpan<byte> reply) => throw new NotSupportedException();
        public int PackDeviceAlarm(DeviceAlarmConfig config, Span<byte> outBuf, out int outLen) => throw new NotSupportedException();
        public int UnPackDeviceAlarm(ReadOnlySpan<byte> reply) => throw new NotSupportedException();
    }
}

public class HaxErrorTests
{
    [Theory]
    [InlineData(0, "ok")]
    [InlineData(1, "invalid param")]
    [InlineData(2, "buffer too small")]
    [InlineData(3, "pack failed")]
    [InlineData(4, "unpack failed (header/length/CRC)")]
    [InlineData(10, "device flag=0 fail")]
    [InlineData(12, "device flag=2 abnormal")]
    [InlineData(13, "device flag=3 duplicate")]
    [InlineData(14, "unknown flag")]
    [InlineData(20, "EPC verify could not reach the server")]
    [InlineData(11, "unknown error code 11")]
    [InlineData(-1, "unknown error code -1")]
    public void Every_code_has_its_text(int code, string text) => Assert.Equal(text, HaxError.Text(code));
}

public class V2ReaderNameTests
{
    static readonly Dictionary<string, string> Gates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["169.254.68.93"] = "GATE-1",
        ["fe80::1"] = "GATE-6",
    };

    [Theory]
    [InlineData("169.254.68.93", 50113, "GATE-1")]
    [InlineData("169.254.68.93", 1272, "GATE-1")]          // same gate after a reconnect, new port
    [InlineData("::ffff:169.254.68.93", 50113, "GATE-1")]  // IPv4 gate seen through a dual-mode socket
    [InlineData("169.254.68.94", 50113, "169.254.68.94")]  // not in the map: its IP, without the port
    [InlineData("fe80::1", 50113, "GATE-6")]
    [InlineData("fe80::2", 50113, "fe80::2")]
    public void Name_comes_from_the_remote_ip_through_the_gates_map(string ip, int port, string expected) =>
        Assert.Equal(expected, V2GateServer.ReaderNameFor(new IPEndPoint(IPAddress.Parse(ip), port), Gates));
}

public class V2ConfigTests
{
    [Fact]
    public void Missing_v2_settings_get_their_defaults()
    {
        var warnings = new List<string>();
        BridgeConfig c = BridgeConfig.Parse("""{ "serverUrl": "http://localhost:8000" }""", warnings.Add);
        Assert.Equal(SdkMode.Legacy, c.SdkMode);
        Assert.Equal(IPAddress.Any, c.ListenIp);
        Assert.Equal(9000, c.ListenPort);
        Assert.Equal(new byte[] { 1, 1, 1, 1 }, c.AlarmChannels);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), c.ReplyTimeout);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Missing_file_means_legacy_mode()
    {
        BridgeConfig c = BridgeConfig.Load(Path.Combine(AppContext.BaseDirectory, "no-such-gate-bridge.json"), _ => { });
        Assert.Equal(SdkMode.Legacy, c.SdkMode);
    }

    [Fact]
    public void Null_means_the_default()
    {
        BridgeConfig c = BridgeConfig.Parse("""
            { "sdkMode": null, "listenIp": null, "listenPort": null, "alarmChannels": null, "replyTimeoutMs": null }
            """, _ => { });
        Assert.Equal(SdkMode.Legacy, c.SdkMode);
        Assert.Equal(9000, c.ListenPort);
        Assert.Equal(new byte[] { 1, 1, 1, 1 }, c.AlarmChannels);
    }

    [Fact]
    public void Full_v2_file_is_read()
    {
        var warnings = new List<string>();
        BridgeConfig c = BridgeConfig.Parse("""
            { "serverUrl": "http://192.168.1.20:8000", "cooldownSeconds": 45, "gates": { "192.168.1.50": "GATE-1" },
              "sdkMode": "v2", "listenIp": "192.168.1.10", "listenPort": 9001, "alarmChannels": [1, 0, 0, 1], "replyTimeoutMs": 2000 }
            """, warnings.Add);
        Assert.Equal(SdkMode.V2, c.SdkMode);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), c.ListenIp);
        Assert.Equal(9001, c.ListenPort);
        Assert.Equal(new byte[] { 1, 0, 0, 1 }, c.AlarmChannels);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), c.ReplyTimeout);
        Assert.Equal("http://192.168.1.20:8000/api/v1/alarms", c.AlarmUrl?.ToString()); // the existing settings work the same
        Assert.Equal(TimeSpan.FromSeconds(45), c.Cooldown);
        Assert.Equal("GATE-1", c.Gates["192.168.1.50"]);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("\"v2\"", "V2")]
    [InlineData("\" V2 \"", "V2")]
    [InlineData("\"legacy\"", "Legacy")]
    [InlineData("\"Legacy\"", "Legacy")]
    public void Sdk_mode_ignores_case_and_spaces(string value, string mode) =>
        Assert.Equal(mode, BridgeConfig.Parse($$"""{ "sdkMode": {{value}} }""", _ => { }).SdkMode.ToString());

    [Theory]
    [InlineData("[1,1,1]")]
    [InlineData("[1,1,1,1,1]")]
    [InlineData("[]")]
    [InlineData("[2,1,1,1]")]
    [InlineData("[-1,1,1,1]")]
    [InlineData("[1,1,1,0.5]")]
    [InlineData("[1,1,1,1.0]")]
    [InlineData("[\"1\",1,1,1]")]
    [InlineData("[true,true,true,true]")]
    [InlineData("[[1],1,1,1]")]
    [InlineData("\"1111\"")]
    [InlineData("1")]
    [InlineData("{}")]
    public void Bad_alarm_channels_are_rejected_with_a_clear_message(string value)
    {
        var ex = Assert.Throws<ConfigException>(() => BridgeConfig.Parse($$"""{ "alarmChannels": {{value}} }""", _ => { }));
        Assert.Equal($"alarmChannels {value} is not valid. Use a list of 4 numbers, one per alarm channel, each 0 (off) " +
                     "or 1 (on), for example [1,1,1,1].", ex.Message);
    }

    [Theory]
    [InlineData("[0,0,0,0]")]
    [InlineData("[1,0,1,0]")]
    [InlineData("[ 0, 1, 1, 1 ]")]
    public void Any_mix_of_0_and_1_is_accepted(string value) =>
        Assert.Equal(4, BridgeConfig.Parse($$"""{ "alarmChannels": {{value}} }""", _ => { }).AlarmChannels.Count);

    [Theory]
    [InlineData("sdkMode", "\"v3\"")]
    [InlineData("sdkMode", "2")]
    [InlineData("sdkMode", "\"\"")]
    [InlineData("listenIp", "\"abc\"")]
    [InlineData("listenIp", "\"9000\"")]          // IPAddress.TryParse alone would take this as 0.0.35.40
    [InlineData("listenIp", "\"192.168.1\"")]
    [InlineData("listenIp", "0")]
    [InlineData("listenPort", "0")]
    [InlineData("listenPort", "65536")]
    [InlineData("listenPort", "\"9000\"")]
    [InlineData("listenPort", "9000.5")]
    [InlineData("replyTimeoutMs", "0")]
    [InlineData("replyTimeoutMs", "-5000")]
    [InlineData("replyTimeoutMs", "\"5000\"")]
    public void Other_bad_v2_settings_are_rejected_and_name_the_setting(string key, string value)
    {
        var ex = Assert.Throws<ConfigException>(() => BridgeConfig.Parse($$"""{ "{{key}}": {{value}} }""", _ => { }));
        Assert.StartsWith($"{key} {value} is not", ex.Message);
    }

    [Fact]
    public void Load_stops_on_a_bad_v2_setting_instead_of_falling_back_to_log_only_legacy()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gate-bridge-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "serverUrl": "http://localhost:8000", "sdkMode": "v2", "alarmChannels": [1, 1] }""");
        try { Assert.Throws<ConfigException>(() => BridgeConfig.Load(path, _ => { })); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_example_file_is_accepted_as_it_is_and_keeps_legacy_mode()
    {
        var warnings = new List<string>();
        BridgeConfig c = BridgeConfig.Load(Path.Combine(AppContext.BaseDirectory, "gate-bridge.example.json"), warnings.Add);
        Assert.Empty(warnings);
        Assert.Equal(SdkMode.Legacy, c.SdkMode);
        Assert.Equal(9000, c.ListenPort);
        Assert.Equal(new byte[] { 1, 1, 1, 1 }, c.AlarmChannels);
    }
}
