// Which HAX DLL set this build carries, and whether the DLLs next to the exe are exactly that set.
// Both sets name their files the same, and common.dll / adapt_for_imports.dll even carry the same version numbers with
// different content, so only file hashes can tell them apart. The expected hashes are sdk-manifests\<set>.sha256,
// taken from HAX's untouched deliveries, checked by the build and embedded in the exe.
// Nothing in this file touches card_7k.dll, so the test project compiles it directly.
using System.Reflection;
using System.Security.Cryptography;

namespace GateTcpTest;

static class SdkManifest
{
    public const string LegacySet = "legacy";   // ..\sdk: card_7k.dll 2.0.1.0, the DLL runs the TCP server
    public const string V2Set = "2.1.2.0";      // ..\hax-7000-sdk-2.1.2.0-2026-11-07\2.1.2.0: Device_Pack*/UnPack*
    public static readonly IReadOnlyList<string> Sets = [LegacySet, V2Set];

    // The set chosen at build time (dotnet build -p:HaxSdk=...), from the exe's AssemblyMetadata.
    public static string? BuiltSet =>
        typeof(SdkManifest).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "HaxSdk")?.Value;

    // The embedded copy of sdk-manifests\<set>.sha256, or null.
    public static string? EmbeddedText(string set)
    {
        using Stream? s = typeof(SdkManifest).Assembly.GetManifestResourceStream($"hax-sdk-{set}.sha256");
        if (s == null) return null;
        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    // "sha256  file.dll" per line (sha256sum's format). File names are matched case-insensitively.
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int space = line.IndexOf(' ');
            if (space != 64) throw new FormatException($"not a \"sha256  file\" line: {line}");
            files[line[(space + 1)..].Trim().TrimStart('*')] = line[..64].ToLowerInvariant();
        }
        return files;
    }

    // Problems with directory against the expected set, empty when every listed DLL is there with the listed hash.
    // others: the other sets' manifests, so a file from another set is named as such ("the SDK sets are mixed").
    public static IReadOnlyList<string> Verify(string directory, string set, IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> others)
    {
        var problems = new List<string>();
        foreach ((string file, string hash) in expected.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(directory, file);
            if (!File.Exists(path))
            {
                problems.Add($"{file} is missing");
                continue;
            }
            string actual = Hash(path);
            if (actual == hash) continue;
            string? from = others.FirstOrDefault(o => o.Value.TryGetValue(file, out string? h) && h == actual).Key;
            problems.Add(from != null
                ? $"{file} is the {from} set's copy, not the {set} set's: the SDK sets are mixed"
                : $"{file} is not the {set} set's copy (sha256 {actual})");
        }
        return problems;
    }

    public static string Hash(string path)
    {
        using FileStream s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }
}
