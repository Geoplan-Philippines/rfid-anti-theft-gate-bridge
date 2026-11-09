// SdkSelfCheck's logic (with fake SDKs) and the SDK set check against HAX's real files on disk.
// No DLL is loaded here: the self-check against HAX's real card_7k.dll is "gate-tcp-test.exe --check-sdk".
using System.Diagnostics;

namespace GateTcpTest.Tests;

public class SdkChecksTests
{
    [Fact]
    public void Captured_gate_packets_are_the_bytes_from_the_demo_screenshot()
    {
        SdkFixtures f = CapturedPackets.Gate7000;
        Assert.Equal("DB 82 6F 2D 1F 23 6C 92 40", V2GateServer.Hex(f.AlarmCommand));
        Assert.Equal("DB 82 6F 2D 1F 23 62 56 C1", V2GateServer.Hex(f.AlarmReply));
        Assert.Equal("DB 82 43 3F 1F 23 62 E3 33 69 BB 32 7C 68 1B FD E9 88 04 1E A5 60 E6 8D 75 DC 63 66 14 C8 32 A2 96 BE 47 D4 C4",
            V2GateServer.Hex(f.TagPacket));
        Assert.Equal(37, f.TagPacket.Length);
        Assert.Equal((4, "E28069950000AA039398B5EA"), (f.TagAntenna, f.TagEpc));
        Assert.Equal("[1,1,1,1]", f.Channels.ToString());
    }

    [Fact]
    public void An_sdk_that_reads_its_packets_and_rejects_partial_joined_and_crossed_input_passes()
    {
        SdkSelfCheckReport r = SdkSelfCheck.Run(new FakeHaxSdk(), FakeHaxSdk.Fixtures());
        Assert.True(r.Decodes);
        Assert.True(r.BoundariesVerified, string.Join("\n", r.Items));
        Assert.All(r.Items, i => Assert.True(i.Passed, i.ToString()));
    }

    [Fact]
    public void An_sdk_that_ignores_bytes_after_a_packet_but_reads_it_as_alone_is_verified_with_a_note()
    {
        // What HAX's 2.1.2.0 DLL did with the captured packets (gate-tcp-test.exe --check-sdk, 2026-11-07).
        SdkSelfCheckReport r = SdkSelfCheck.Run(new FakeHaxSdk(lenient: true), FakeHaxSdk.Fixtures());
        Assert.True(r.BoundariesVerified, string.Join(Environment.NewLine, r.Items));
        SdkCheckItem note = Assert.Single(r.Items, i => i.Name == "bytes after a packet are ignored by the DLL");
        Assert.Equal(SdkCheckGroup.Info, note.Group);
        Assert.False(note.Passed);
    }

    [Fact]
    public void An_sdk_that_reads_a_cut_off_packet_fails_the_boundary_check_so_reassembly_stays_off()
    {
        SdkSelfCheckReport r = SdkSelfCheck.Run(new FakeHaxSdk(acceptTruncated: true), FakeHaxSdk.Fixtures());
        Assert.True(r.Decodes);
        Assert.False(r.BoundariesVerified);
        Assert.False(Assert.Single(r.Items, i => i.Name == "every truncated packet is rejected").Passed);
    }

    [Fact]
    public void An_sdk_that_does_not_read_the_captured_packets_fails_decode()
    {
        SdkFixtures wrong = FakeHaxSdk.Fixtures() with { TagEpc = "000000000000000000000000" };
        SdkSelfCheckReport r = SdkSelfCheck.Run(new FakeHaxSdk(), wrong);
        Assert.False(r.Decodes);
        Assert.False(r.BoundariesVerified);

        // The real captured bytes mean nothing to the fake: decode fails, as it would with the wrong DLL.
        Assert.False(SdkSelfCheck.Run(new FakeHaxSdk(), CapturedPackets.Gate7000).Decodes);
    }

    static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "gate-tcp-test", "sdk-manifests"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root not found above " + AppContext.BaseDirectory);
    }

    static IReadOnlyDictionary<string, string> Manifest(string set) =>
        SdkManifest.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "gate-tcp-test", "sdk-manifests", $"{set}.sha256")));

    static string SetDir(string set) => set == SdkManifest.LegacySet
        ? Path.Combine(RepoRoot(), "sdk")
        : Path.Combine(RepoRoot(), "hax-7000-sdk-2.1.2.0-2026-11-07", "2.1.2.0");

    static Dictionary<string, IReadOnlyDictionary<string, string>> Others(string set) =>
        SdkManifest.Sets.Where(s => s != set).ToDictionary(s => s, Manifest);

    [Theory]
    [InlineData(SdkManifest.LegacySet)]
    [InlineData(SdkManifest.V2Set)]
    public void Each_vendor_folder_matches_its_committed_manifest(string set)
    {
        IReadOnlyDictionary<string, string> expected = Manifest(set);
        Assert.Equal(12, expected.Count);
        Assert.Empty(SdkManifest.Verify(SetDir(set), set, expected, Others(set)));
    }

    [Fact]
    public void Same_version_number_different_file_so_only_the_hash_tells_the_sets_apart()
    {
        string legacy = Path.Combine(SetDir(SdkManifest.LegacySet), "common.dll");
        string v2 = Path.Combine(SetDir(SdkManifest.V2Set), "common.dll");
        Assert.Equal(FileVersionInfo.GetVersionInfo(legacy).FileVersion, FileVersionInfo.GetVersionInfo(v2).FileVersion);
        Assert.NotEqual(SdkManifest.Hash(legacy), SdkManifest.Hash(v2));
    }

    [Fact]
    public void A_mixed_missing_or_unknown_file_is_reported()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gate-sdk-mix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string f in Directory.GetFiles(SetDir(SdkManifest.V2Set), "*.dll"))
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            File.Copy(Path.Combine(SetDir(SdkManifest.LegacySet), "common.dll"), Path.Combine(dir, "common.dll"), overwrite: true);
            File.Delete(Path.Combine(dir, "concrt140.dll"));
            File.WriteAllBytes(Path.Combine(dir, "msvcp140.dll"), [1, 2, 3]);

            IReadOnlyList<string> problems = SdkManifest.Verify(dir, SdkManifest.V2Set, Manifest(SdkManifest.V2Set), Others(SdkManifest.V2Set));

            Assert.Contains("common.dll is the legacy set's copy, not the 2.1.2.0 set's: the SDK sets are mixed", problems);
            Assert.Contains("concrt140.dll is missing", problems);
            Assert.Contains(problems, p => p.StartsWith("msvcp140.dll is not the 2.1.2.0 set's copy", StringComparison.Ordinal));
            Assert.Equal(3, problems.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_manifest_line_that_is_not_hash_space_space_file_is_refused() =>
        Assert.Throws<FormatException>(() => SdkManifest.Parse("abc card_7k.dll"));
}
