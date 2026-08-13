using System.Text.Json;
using System.Xml;
using DmarcAnalyzer.Api.Application.Reports;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public interface IReportPayloadIngestor
{
    Task<ReportPayloadIngestResult> IngestAsync(
        ReportSourceContext source,
        Stream payload,
        ReportPayloadMetadata metadata,
        CancellationToken ct);
}

/// <summary>
/// The complete bounded result for one raw payload. Extraction rejections and
/// parser/persistence rejections share the same stable list so callers do not
/// need to reproduce format routing to explain an outcome.
/// </summary>
public sealed record ReportPayloadIngestResult(
    int DmarcInserted,
    int DmarcDuplicates,
    int DmarcRejected,
    int TlsInserted,
    int TlsDuplicates,
    int TlsRejected,
    IReadOnlyList<ReportPayloadRejection> Rejections,
    string? ContentSha256,
    long PayloadBytes,
    ReportPayloadContainer Container)
{
    public int ReportsProcessed =>
        DmarcInserted + DmarcDuplicates + DmarcRejected
        + TlsInserted + TlsDuplicates + TlsRejected;
}

/// <summary>
/// One deep ingestion path for mailbox and machine callers: bounded extraction,
/// format routing, parsing, and dispatch to the parsed persistence services.
/// It knows neither MailKit nor HTTP authentication.
/// </summary>
public sealed class ReportPayloadIngestor(
    IReportPayloadExtractor extractor,
    IDmarcReportParser dmarcParser,
    IDmarcReportIngestor dmarcIngestor,
    ITlsRptReportParser tlsParser,
    ITlsReportIngestor tlsIngestor) : IReportPayloadIngestor
{
    public async Task<ReportPayloadIngestResult> IngestAsync(
        ReportSourceContext source,
        Stream payload,
        ReportPayloadMetadata metadata,
        CancellationToken ct)
    {
        var extraction = await extractor.ExtractAsync(payload, metadata, ct);
        var rejections = extraction.Rejections.ToList();
        var dmarcInserted = 0;
        var dmarcDuplicates = 0;
        var dmarcRejected = 0;
        var tlsInserted = 0;
        var tlsDuplicates = 0;
        var tlsRejected = 0;

        try
        {
            if (metadata.ExpectedContentSha256 is not null
                && !string.Equals(
                    metadata.ExpectedContentSha256,
                    extraction.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                rejections.Add(new(ReportPayloadRejectionCode.ContentSha256Mismatch));
                return new(
                    0, 0, 0, 0, 0, 0,
                    rejections,
                    extraction.ContentSha256,
                    extraction.PayloadBytes,
                    extraction.Container);
            }

            foreach (var report in extraction.Payloads)
            {
                ct.ThrowIfCancellationRequested();
                report.Stream.Position = 0;

                if (report.Kind == ReportPayloadKind.SmtpTlsReportJson)
                {
                    TlsRptParseResult parsed;
                    try
                    {
                        EnsureWellFormed(report.Stream, IsWellFormedJson);
                        parsed = tlsParser.Parse(report.Stream);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        tlsRejected++;
                        rejections.Add(new(ReportPayloadRejectionCode.InvalidTlsReport, report.SourceName));
                        continue;
                    }

                    var outcome = await tlsIngestor.IngestAsync(source, parsed, ct);
                    if (outcome == TlsReportIngestOutcome.Inserted)
                    {
                        tlsInserted++;
                    }
                    else if (outcome == TlsReportIngestOutcome.Duplicate)
                    {
                        tlsDuplicates++;
                    }
                    else
                    {
                        tlsRejected++;
                        rejections.Add(new(ReportPayloadRejectionCode.InvalidTlsReport, report.SourceName));
                    }

                    continue;
                }

                DmarcReportParseResult dmarc;
                try
                {
                    EnsureWellFormed(report.Stream, IsWellFormedXml);
                    dmarc = dmarcParser.Parse(report.Stream);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    dmarcRejected++;
                    rejections.Add(new(ReportPayloadRejectionCode.InvalidDmarcReport, report.SourceName));
                    continue;
                }

                var dmarcOutcome = await dmarcIngestor.IngestParsedAsync(source, dmarc, ct);
                if (dmarcOutcome == DmarcIngestOutcome.Inserted)
                {
                    dmarcInserted++;
                }
                else if (dmarcOutcome == DmarcIngestOutcome.Duplicate)
                {
                    dmarcDuplicates++;
                }
                else
                {
                    dmarcRejected++;
                    rejections.Add(new(ReportPayloadRejectionCode.InvalidDmarcReport, report.SourceName));
                }
            }

            return new(
                dmarcInserted,
                dmarcDuplicates,
                dmarcRejected,
                tlsInserted,
                tlsDuplicates,
                tlsRejected,
                rejections,
                extraction.ContentSha256,
                extraction.PayloadBytes,
                extraction.Container);
        }
        finally
        {
            foreach (var report in extraction.Payloads)
            {
                await report.Stream.DisposeAsync();
            }
        }

    }

    private static void EnsureWellFormed(Stream stream, Func<Stream, bool> validator)
    {
        stream.Position = 0;
        var wellFormed = validator(stream);
        stream.Position = 0;

        if (!wellFormed)
        {
            throw new InvalidDataException("Report payload is not a complete document.");
        }
    }

    private static bool IsWellFormedJson(Stream stream)
    {
        try
        {
            using var _ = JsonDocument.Parse(stream);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsWellFormedXml(Stream stream)
    {
        try
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            RemoveWhitespaceBeforeXmlDeclaration(copy);
            using var reader = XmlReader.Create(copy, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                CloseInput = false,
            });

            while (reader.Read())
            {
            }

            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static void RemoveWhitespaceBeforeXmlDeclaration(MemoryStream stream)
    {
        var bytes = stream.GetBuffer().AsSpan(0, checked((int)stream.Length));
        var prefixLength = bytes.StartsWith("\uFEFF"u8) ? 3 : 0;
        var declarationOffset = prefixLength;

        while (declarationOffset < bytes.Length
               && bytes[declarationOffset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            declarationOffset++;
        }

        if (declarationOffset > prefixLength && bytes[declarationOffset..].StartsWith("<?xml"u8))
        {
            bytes[declarationOffset..].CopyTo(bytes[prefixLength..]);
            stream.SetLength(stream.Length - (declarationOffset - prefixLength));
        }

        stream.Position = 0;
    }
}
