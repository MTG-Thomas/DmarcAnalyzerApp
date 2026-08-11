using System.Text;
using DmarcAnalyzer.Api.Application.Ingestion;
using MimeKit;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class MailboxAttachmentStreamTests
{
    [Fact]
    public async Task MimePartStreamIsTransferDecodedWithoutIntermediateMaterialization()
    {
        var decoded = "<feedback><report_metadata/></feedback>"u8.ToArray();
        var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(decoded));
        var part = new MimePart("application", "xml")
        {
            Content = new MimeContent(new MemoryStream(encoded, writable: false), ContentEncoding.Base64),
        };

        await using var stream = MailboxSyncService.OpenDecodedAttachmentStream(part);
        Assert.NotNull(stream);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);

        Assert.Equal(decoded, output.ToArray());
    }

    [Fact]
    public void AttachedMessageIsExplicitlyUnsupportedRatherThanSerializedUnbounded()
    {
        var part = new MessagePart { Message = new MimeMessage() };

        Assert.Null(MailboxSyncService.OpenDecodedAttachmentStream(part));
    }

    [Theory]
    [InlineData(ReportPayloadContainer.Gzip, ReportPayloadRejectionCode.UnsupportedFormat, 1, 1)]
    [InlineData(ReportPayloadContainer.Gzip, ReportPayloadRejectionCode.EmptyContainer, 1, 1)]
    [InlineData(ReportPayloadContainer.Gzip, ReportPayloadRejectionCode.NestedContainer, 1, 1)]
    [InlineData(ReportPayloadContainer.Bare, ReportPayloadRejectionCode.UnsupportedFormat, 0, 0)]
    [InlineData(ReportPayloadContainer.Zip, ReportPayloadRejectionCode.EmptyContainer, 0, 0)]
    public void LegacyGzipFailureCountersRemainStable(
        ReportPayloadContainer container,
        ReportPayloadRejectionCode rejection,
        int expectedProcessed,
        int expectedFailures)
    {
        var outcome = new ReportPayloadIngestResult(
            0, 0, 0, 0, 0, 0,
            [new(rejection)],
            "digest",
            1,
            container);

        var counters = MailboxSyncService.MapPayloadOutcomeCounters(outcome);

        Assert.Equal(expectedProcessed, counters.AttachmentsProcessed);
        Assert.Equal(expectedFailures, counters.ParseFailures);
    }
}
