using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ReportPayloadExtractorTests
{
    private static readonly byte[] Xml = "<feedback><report_metadata/></feedback>"u8.ToArray();
    private static readonly byte[] Json = "{\"report-id\":\"test\",\"policies\":[]}"u8.ToArray();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BareXmlAndJsonAreAccepted(bool xml)
    {
        var bytes = xml ? Xml : Json;
        var result = await ExtractAsync(bytes, new(
            xml ? "report.json" : "report.xml",
            xml ? "application/json" : "application/xml"));

        var payload = Assert.Single(result.Payloads);
        Assert.Equal(xml ? ReportPayloadKind.DmarcAggregateXml : ReportPayloadKind.SmtpTlsReportJson, payload.Kind);
        Assert.Empty(result.Rejections);
        Assert.Equal(bytes.Length, result.PayloadBytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), result.ContentSha256);
        Assert.Equal(bytes, await ReadPayloadAsync(payload));
    }

    [Fact]
    public async Task GzipMagicWinsOverMisleadingLabels()
    {
        var gzip = Gzip(Json);
        var result = await ExtractAsync(gzip, new("report.xml", "application/xml"));

        var payload = Assert.Single(result.Payloads);
        Assert.Equal(ReportPayloadKind.SmtpTlsReportJson, payload.Kind);
        Assert.Equal(Json, await ReadPayloadAsync(payload));
    }

    [Fact]
    public async Task XmlContentWinsOverMisleadingContainerLabel()
    {
        var result = await ExtractAsync(Xml, new("report.zip", "application/zip"));

        Assert.Equal(ReportPayloadKind.DmarcAggregateXml, Assert.Single(result.Payloads).Kind);
    }

    [Fact]
    public async Task ZipReturnsMultipleReportsAndAnExplicitJunkRejection()
    {
        var zip = Zip(
            ("00-readme.txt", "not a report"u8.ToArray()),
            ("01-report.xml", Xml),
            ("02-report.json", Json));

        var result = await ExtractAsync(zip, new("mislabelled.gz", "application/gzip"));

        Assert.Equal(2, result.Payloads.Count);
        Assert.Equal(
            [ReportPayloadKind.DmarcAggregateXml, ReportPayloadKind.SmtpTlsReportJson],
            result.Payloads.Select(payload => payload.Kind));
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(ReportPayloadRejectionCode.UnsupportedFormat, rejection.Code);
        Assert.Equal("00-readme.txt", rejection.SourceName);
    }

    [Fact]
    public async Task ZipReturnsValidSiblingWithEmptyAndNestedEntryRejections()
    {
        var zip = Zip(
            ("empty.xml", []),
            ("nested.gz", Gzip(Xml)),
            ("report.json", Json));

        var result = await ExtractAsync(zip, new("reports.zip"));

        Assert.Equal(ReportPayloadKind.SmtpTlsReportJson, Assert.Single(result.Payloads).Kind);
        Assert.Equal(
            [ReportPayloadRejectionCode.EmptyPayload, ReportPayloadRejectionCode.NestedContainer],
            result.Rejections.Select(rejection => rejection.Code));
    }

    [Fact]
    public async Task ExactRequestLimitIsAcceptedAndOneByteOverIsRejectedOnNonSeekableInput()
    {
        var options = Limits(maxRequest: Xml.Length);
        var extractor = Extractor(options);

        await using var exact = new NonSeekableReadStream(Xml);
        var accepted = await extractor.ExtractAsync(exact, new("report.xml"), CancellationToken.None);
        Assert.Single(accepted.Payloads);

        await using var over = new NonSeekableReadStream([.. Xml, (byte)' ']);
        var rejected = await extractor.ExtractAsync(over, new("report.xml"), CancellationToken.None);
        AssertRejection(rejected, ReportPayloadRejectionCode.RequestTooLarge);
        Assert.Equal(Xml.Length + 1, rejected.PayloadBytes);
    }

    [Fact]
    public async Task GzipExpansionStopsOneBytePastTheConfiguredLimit()
    {
        var gzip = GzipGenerated(1024 * 1024);
        var options = Limits(maxEntry: 1024, maxExpanded: 4096, ratio: 1_000_000);

        var result = await ExtractAsync(gzip, new("report.xml.gz"), options);

        AssertRejection(result, ReportPayloadRejectionCode.EntryTooLarge);
    }

    [Fact]
    public async Task GzipCompressionRatioIsCheckedBeforeTheExpandedSizeLimit()
    {
        var gzip = GzipGenerated(64 * 1024);
        var options = Limits(maxEntry: 128 * 1024, maxExpanded: 128 * 1024, ratio: 2);

        var result = await ExtractAsync(gzip, new("report.xml.gz"), options);

        AssertRejection(result, ReportPayloadRejectionCode.CompressionRatioExceeded);
    }

    [Fact]
    public async Task ExactGzipCompressionRatioIsAccepted()
    {
        var gzip = Gzip(Xml);
        var options = Limits(ratio: (double)Xml.Length / gzip.Length);

        var result = await ExtractAsync(gzip, new("report.xml.gz"), options);

        Assert.Single(result.Payloads);
    }

    [Fact]
    public async Task ZipEntryLimitCountsJunkAndDirectoryEntries()
    {
        var zip = Zip(
            ("folder/", []),
            ("folder/readme.txt", "junk"u8.ToArray()),
            ("report.xml", Xml));

        var result = await ExtractAsync(zip, new("reports.zip"), Limits(maxEntries: 2));

        AssertRejection(result, ReportPayloadRejectionCode.ArchiveEntryLimitExceeded);
    }

    [Fact]
    public async Task ZipAggregateExpandedLimitRejectsWithoutReturningPartialPayloads()
    {
        var zip = Zip(("a.xml", Xml), ("b.xml", Xml));
        var options = Limits(maxEntry: Xml.Length, maxExpanded: Xml.Length * 2 - 1, ratio: 1_000);

        var result = await ExtractAsync(zip, new("reports.zip"), options);

        AssertRejection(result, ReportPayloadRejectionCode.ExpandedSizeLimitExceeded);
        Assert.Empty(result.Payloads);
    }

    [Fact]
    public async Task ZipCompressionRatioLimitIsFatalForTheWholeContainer()
    {
        var zip = ZipCompressed(("large.xml", [.. "<feedback>"u8.ToArray(), .. new byte[64 * 1024], .. "</feedback>"u8.ToArray()]));

        var result = await ExtractAsync(zip, new("reports.zip"), Limits(ratio: 2));

        AssertRejection(result, ReportPayloadRejectionCode.CompressionRatioExceeded);
    }

    [Fact]
    public async Task ForgedHugeZipSizeIsRejectedBeforeEntryAllocation()
    {
        var zip = Zip(("report.xml", Xml));
        PatchCentralDirectoryUInt32(zip, 24, 0x7FFF_FFFF);
        var options = Limits(maxEntry: 1024, maxExpanded: 4096, ratio: 1_000_000);

        var result = await ExtractAsync(zip, new("report.zip"), options);

        AssertRejection(result, ReportPayloadRejectionCode.EntryTooLarge);
    }

    [Fact]
    public async Task EncryptedZipFlagIsRejectedBeforeOpeningAnEntry()
    {
        var zip = Zip(("report.xml", Xml));
        SetZipEncryptedFlag(zip);

        var result = await ExtractAsync(zip, new("report.zip"));

        AssertRejection(result, ReportPayloadRejectionCode.EncryptedContainer);
    }

    [Fact]
    public async Task NestedContainerIsRejectedWithoutRecursion()
    {
        var nested = Gzip(Xml);
        var zip = Zip(("nested.gz", nested), ("report.json", Json));

        var result = await ExtractAsync(zip, new("reports.zip"));

        Assert.Equal(ReportPayloadKind.SmtpTlsReportJson, Assert.Single(result.Payloads).Kind);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(ReportPayloadRejectionCode.NestedContainer, rejection.Code);
        Assert.Equal("nested.gz", rejection.SourceName);
    }

    [Fact]
    public async Task GzipContainingZipIsRejectedAsNested()
    {
        var result = await ExtractAsync(Gzip(Zip(("report.xml", Xml))), new("nested.gz"));

        AssertRejection(result, ReportPayloadRejectionCode.NestedContainer);
    }

    [Fact]
    public async Task EmptyAndCorruptContainersHaveStableRejections()
    {
        var emptyZip = Zip();
        var emptyResult = await ExtractAsync(emptyZip, new("empty.zip"));
        AssertRejection(emptyResult, ReportPayloadRejectionCode.EmptyContainer);

        var validZip = Zip(("report.xml", Xml));
        var corruptResult = await ExtractAsync(validZip[..^8], new("report.zip"));
        AssertRejection(corruptResult, ReportPayloadRejectionCode.CorruptContainer);

        var corruptGzip = new byte[] { 0x1F, 0x8B, 0x08, 0x00 };
        var corruptGzipResult = await ExtractAsync(corruptGzip, new("report.gz"));
        AssertRejection(corruptGzipResult, ReportPayloadRejectionCode.CorruptContainer);
    }

    [Theory]
    [InlineData("archive.tar", ReportPayloadRejectionCode.UnsupportedContainer)]
    [InlineData("notes.txt", ReportPayloadRejectionCode.UnsupportedFormat)]
    public async Task UnsupportedInputHasStableRejection(string fileName, ReportPayloadRejectionCode expected)
    {
        var result = await ExtractAsync("not a report"u8.ToArray(), new(fileName));

        AssertRejection(result, expected);
    }

    [Fact]
    public async Task EmptyPayloadIsRejectedEvenWhenLabelClaimsXml()
    {
        var result = await ExtractAsync([], new("report.xml", "application/xml"));

        AssertRejection(result, ReportPayloadRejectionCode.EmptyPayload);
    }

    private static BoundedReportPayloadExtractor Extractor(ReportPayloadExtractionOptions? options = null)
        => new(Options.Create(options ?? Limits()));

    private static async Task<ReportPayloadExtractionResult> ExtractAsync(
        byte[] bytes,
        ReportPayloadMetadata metadata,
        ReportPayloadExtractionOptions? options = null)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        return await Extractor(options).ExtractAsync(stream, metadata, CancellationToken.None);
    }

    private static ReportPayloadExtractionOptions Limits(
        int maxRequest = 1024 * 1024,
        int maxExpanded = 1024 * 1024,
        int maxEntries = 10,
        int maxEntry = 1024 * 1024,
        double ratio = 100)
        => new()
        {
            MaxRequestBytes = maxRequest,
            MaxExpandedBytes = maxExpanded,
            MaxArchiveEntries = maxEntries,
            MaxEntryBytes = maxEntry,
            MaxCompressionRatio = ratio,
        };

    private static async Task<byte[]> ReadPayloadAsync(ExtractedReportPayload payload)
    {
        await using var stream = payload.Stream;
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private static void AssertRejection(
        ReportPayloadExtractionResult result,
        ReportPayloadRejectionCode expected)
    {
        Assert.Empty(result.Payloads);
        Assert.Equal(expected, Assert.Single(result.Rejections).Code);
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        return output.ToArray();
    }

    private static byte[] GzipGenerated(int expandedBytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var block = new byte[4096];
            var remaining = expandedBytes;
            while (remaining > 0)
            {
                var count = Math.Min(block.Length, remaining);
                gzip.Write(block, 0, count);
                remaining -= count;
            }
        }

        return output.ToArray();
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
        => ZipWithCompression(CompressionLevel.NoCompression, entries);

    private static byte[] ZipCompressed(params (string Name, byte[] Content)[] entries)
        => ZipWithCompression(CompressionLevel.SmallestSize, entries);

    private static byte[] ZipWithCompression(
        CompressionLevel compression,
        params (string Name, byte[] Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, compression);
                if (name.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        return output.ToArray();
    }

    private static void SetZipEncryptedFlag(byte[] zip)
    {
        var local = FindSignature(zip, 0x04034B50);
        var central = FindSignature(zip, 0x02014B50);
        Assert.True(local >= 0 && central >= 0);

        BinaryPrimitives.WriteUInt16LittleEndian(
            zip.AsSpan(local + 6, 2),
            (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + 6, 2)) | 1));
        BinaryPrimitives.WriteUInt16LittleEndian(
            zip.AsSpan(central + 8, 2),
            (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(central + 8, 2)) | 1));
    }

    private static void PatchCentralDirectoryUInt32(byte[] zip, int relativeOffset, uint value)
    {
        var central = FindSignature(zip, 0x02014B50);
        Assert.True(central >= 0);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(central + relativeOffset, 4), value);
    }

    private static int FindSignature(byte[] bytes, uint signature)
    {
        for (var i = 0; i <= bytes.Length - 4; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == signature)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
