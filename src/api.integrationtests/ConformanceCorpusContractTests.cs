using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

public sealed class ConformanceCorpusContractTests
{
    private static readonly Regex DomainToken = new(
        @"(?i)(?<![a-z0-9_-])(?:[a-z0-9](?:[a-z0-9_-]{0,61}[a-z0-9])?\.)+[a-z]{2,}(?![a-z0-9_-])");
    private static readonly Regex IpToken = new(
        @"(?<![0-9a-f:.])(?:[0-9a-f]{0,4}:){2,}[0-9a-f:.]*(?![0-9a-f:.])|(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])",
        RegexOptions.IgnoreCase);
    private static readonly string CorpusRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Conformance");

    [Fact]
    public void Manifest_PinsSyntheticProvenanceCasesSentinelsAndHashes()
    {
        using var manifest = OpenJson("manifest.json");
        var root = manifest.RootElement;

        Assert.Equal("dmarc-analyzer.conformance-corpus/v1", root.GetProperty("schema").GetString());
        Assert.False(root.GetProperty("provenance").GetProperty("contains_customer_data").GetBoolean());
        Assert.Equal(
            "258d606e1f8ce859fff5422d550d469c3b666111",
            root.GetProperty("provenance").GetProperty("recoverable_source").GetProperty("commit").GetString());

        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(33, cases.Length);
        Assert.Equal(33, cases.Select(item => item.GetProperty("id").GetString()).Distinct().Count());
        Assert.Equal(35, cases.Sum(item => item.GetProperty("payloads").GetArrayLength()));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(CorpusRoot, "*", SearchOption.AllDirectories),
            path => path.EndsWith(".eml", StringComparison.OrdinalIgnoreCase));

        var referenced = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
        foreach (var schema in root.GetProperty("schemas").EnumerateObject())
            referenced.Add(AssertHash(schema.Value));

        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var caseId = item.GetProperty("id").GetString();
            Assert.Equal(caseId, item.GetProperty("source_case_id").GetString());
            referenced.Add(AssertHash(item, "expected", "expected_sha256"));
            foreach (var payload in item.GetProperty("payloads").EnumerateArray())
            {
                referenced.Add(AssertHash(payload));
                ValidatePayloadIdentities(caseId!, payload);
            }

            using var expected = OpenJson(item.GetProperty("expected").GetString()!);
            Assert.Equal(caseId, expected.RootElement.GetProperty("case_id").GetString());
            Assert.Equal(
                item.GetProperty("expected_outcome").GetString(),
                expected.RootElement.GetProperty("outcome").GetString());

            if (!item.TryGetProperty("recovery_for", out var recoveryFor))
                continue;

            Assert.True(index > 0);
            Assert.Equal(
                cases[index - 1].GetProperty("id").GetString(),
                recoveryFor.GetString());
            Assert.Equal("inserted", item.GetProperty("expected_outcome").GetString());
        }

        Assert.Equal(9, cases.Count(item => item.TryGetProperty("recovery_for", out _)));
        var actual = Directory.EnumerateFiles(CorpusRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(CorpusRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(referenced.Order(), actual.Order());
    }

    private static JsonDocument OpenJson(string relativePath)
        => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CorpusRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))));

    private static string AssertHash(JsonElement reference)
        => AssertHash(reference, "path", "sha256");

    private static string AssertHash(JsonElement reference, string pathProperty, string hashProperty)
    {
        var path = reference.GetProperty(pathProperty).GetString()!;
        var expected = reference.GetProperty(hashProperty).GetString();
        var actual = Convert.ToHexStringLower(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(CorpusRoot, path.Replace('/', Path.DirectorySeparatorChar)))));
        Assert.Equal(expected, actual);
        return path;
    }

    private static void ValidatePayloadIdentities(string caseId, JsonElement payload)
    {
        var filename = payload.GetProperty("filename").GetString()!;
        ValidateIdentityText(WithoutPayloadExtension(filename));
        var data = File.ReadAllBytes(Path.Combine(
            CorpusRoot,
            payload.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar)));

        switch (payload.GetProperty("container").GetString())
        {
            case "plain":
                ValidateIdentityText(Encoding.UTF8.GetString(data));
                break;
            case "gzip" when caseId == "corrupt-gzip":
                Assert.Equal(
                    "411b637d433d358e86279310e7dd0c01975972948df589c3746bf57025212786",
                    Convert.ToHexStringLower(SHA256.HashData(data)));
                break;
            case "gzip":
                using (var source = new MemoryStream(data))
                using (var gzip = new GZipStream(source, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip, Encoding.UTF8))
                    ValidateIdentityText(reader.ReadToEnd());
                break;
            case "zip":
                using (var source = new MemoryStream(data))
                using (var archive = new ZipArchive(source, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries.Where(item => !string.IsNullOrEmpty(item.Name)))
                    {
                        ValidateIdentityText(WithoutPayloadExtension(entry.FullName));
                        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                        ValidateIdentityText(reader.ReadToEnd());
                    }
                }
                break;
            default:
                Assert.Fail($"Unexpected payload container for {caseId}.");
                break;
        }
    }

    private static string WithoutPayloadExtension(string value)
    {
        foreach (var suffix in new[] { ".gz", ".zip", ".xml", ".json", ".txt" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                value = value[..^suffix.Length];
        }
        return value;
    }

    private static void ValidateIdentityText(string value)
    {
        foreach (Match match in DomainToken.Matches(value))
            Assert.EndsWith(".example", match.Value, StringComparison.OrdinalIgnoreCase);

        foreach (Match match in IpToken.Matches(value))
        {
            if (!IPAddress.TryParse(match.Value, out var address))
                continue;
            var bytes = address.GetAddressBytes();
            var isDocumentationAddress = bytes.Length == 4
                ? bytes is [192, 0, 2, _] or [198, 51, 100, _] or [203, 0, 113, _]
                : bytes.Length == 16 && bytes[..4].SequenceEqual(new byte[] { 0x20, 0x01, 0x0d, 0xb8 });
            Assert.True(isDocumentationAddress, $"Non-documentation IP address in corpus: {address}");
        }
    }
}
