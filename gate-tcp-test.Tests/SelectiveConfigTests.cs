// gate-bridge.json settings for the selective physical alarm.
namespace GateTcpTest.Tests;

public class SelectiveConfigTests
{
    [Fact]
    public void Defaults_keep_the_physical_alarm_off_with_an_800_ms_check_that_alarms_on_failure()
    {
        BridgeConfig c = BridgeConfig.Parse("""{ "serverUrl": "http://localhost:8000/" }""", _ => { });

        Assert.Equal(PhysicalAlarmMode.Off, c.PhysicalAlarm);
        Assert.Equal(TimeSpan.FromMilliseconds(800), c.SoldCheckTimeout);
        Assert.Equal(SoldCheckFailurePolicy.Alarm, c.OnSoldCheckFailure);
        Assert.Equal(new Uri("http://localhost:8000/api/v1/anti-theft/known-epcs/check"), c.SoldCheckUrl);
        Assert.Equal(new Uri("http://localhost:8000/api/v1/alarms"), c.AlarmUrl);
    }

    [Fact]
    public void Selective_mode_is_read_with_its_settings()
    {
        var warnings = new List<string>();
        BridgeConfig c = BridgeConfig.Parse("""
            { "serverUrl": "http://server:8000", "sdkMode": "v2", "physicalAlarm": "Selective",
              "soldCheckTimeoutMs": 500, "onSoldCheckFailure": "silent" }
            """, warnings.Add);

        Assert.Empty(warnings);
        Assert.Equal(PhysicalAlarmMode.Selective, c.PhysicalAlarm);
        Assert.Equal(TimeSpan.FromMilliseconds(500), c.SoldCheckTimeout);
        Assert.Equal(SoldCheckFailurePolicy.Silent, c.OnSoldCheckFailure);
        Assert.Equal(new Uri("http://server:8000/api/v1/anti-theft/known-epcs/check"), c.SoldCheckUrl);
    }

    [Theory]
    [InlineData("physicalAlarm", "\"on\"", "physicalAlarm \"on\" is not valid. Use \"off\" or \"selective\".")]
    [InlineData("physicalAlarm", "true", "physicalAlarm true is not valid. Use \"off\" or \"selective\".")]
    [InlineData("onSoldCheckFailure", "\"beep\"", "onSoldCheckFailure \"beep\" is not valid. Use \"alarm\" or \"silent\".")]
    [InlineData("soldCheckTimeoutMs", "0", "soldCheckTimeoutMs 0 is not valid. Use a number of milliseconds from 1 to 60000, for example 800.")]
    [InlineData("soldCheckTimeoutMs", "\"800\"", "soldCheckTimeoutMs \"800\" is not valid. Use a number of milliseconds from 1 to 60000, for example 800.")]
    public void A_wrong_value_stops_the_app_with_what_to_write_instead(string key, string value, string message)
    {
        var ex = Assert.Throws<ConfigException>(() =>
            BridgeConfig.Parse($$"""{ "serverUrl": "http://localhost:8000", "{{key}}": {{value}} }""", _ => { }));
        Assert.Equal(message, ex.Message);
    }

    [Theory]
    [InlineData("""{ "sdkMode": "v2", "physicalAlarm": "selective" }""")]
    [InlineData("""{ "sdkMode": "v2", "physicalAlarm": "selective", "serverUrl": "ftp://x" }""")]
    public void Selective_mode_without_a_server_stops_the_app(string json)
    {
        var ex = Assert.Throws<ConfigException>(() => BridgeConfig.Parse(json, _ => { }));
        Assert.StartsWith("physicalAlarm \"selective\" needs serverUrl", ex.Message);
    }

    [Fact]
    public void Selective_mode_in_legacy_mode_is_ignored_with_a_warning()
    {
        var warnings = new List<string>();
        BridgeConfig c = BridgeConfig.Parse("""{ "serverUrl": "http://localhost:8000", "physicalAlarm": "selective" }""", warnings.Add);
        Assert.Equal(SdkMode.Legacy, c.SdkMode);
        Assert.Equal(["physicalAlarm only applies when sdkMode is \"v2\"; legacy mode ignores it"], warnings);
    }

    [Fact]
    public void Untested_alarm_channels_in_selective_mode_get_a_warning()
    {
        var warnings = new List<string>();
        BridgeConfig.Parse("""
            { "serverUrl": "http://localhost:8000", "sdkMode": "v2", "physicalAlarm": "selective", "alarmChannels": [1,0,1,1] }
            """, warnings.Add);
        Assert.Equal(["alarmChannels [1,0,1,1] has not been tested on the gate; only [1,1,1,1] has"], warnings);
    }

    [Fact]
    public void The_example_file_keeps_the_physical_alarm_off()
    {
        var warnings = new List<string>();
        BridgeConfig c = BridgeConfig.Load(Path.Combine(AppContext.BaseDirectory, "gate-bridge.example.json"), warnings.Add);
        Assert.Empty(warnings);
        Assert.Equal(PhysicalAlarmMode.Off, c.PhysicalAlarm);
        Assert.Equal(SoldCheckFailurePolicy.Alarm, c.OnSoldCheckFailure);
        Assert.Equal(TimeSpan.FromMilliseconds(800), c.SoldCheckTimeout);
    }
}
