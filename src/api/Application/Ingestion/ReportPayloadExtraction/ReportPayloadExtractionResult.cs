namespace DmarcAnalyzer.Api.Application.Ingestion;

public enum ReportPayloadRejectionCode
{
    EmptyPayload,
    RequestTooLarge,
    UnsupportedFormat,
    UnsupportedContainer,
    EmptyContainer,
    CorruptContainer,
    EncryptedContainer,
    NestedContainer,
    ArchiveEntryLimitExceeded,
    EntryTooLarge,
    ExpandedSizeLimitExceeded,
    CompressionRatioExceeded,
    InvalidDmarcReport,
    InvalidTlsReport,
    ContentSha256Mismatch,
}

public enum ReportPayloadContainer
{
    Unknown,
    Bare,
    Gzip,
    Zip,
}

/// <summary>
/// A stable machine-readable rejection. <see cref="SourceName"/> identifies a ZIP
/// entry when the rest of the container could still be inspected.
/// </summary>
public sealed record ReportPayloadRejection(
    ReportPayloadRejectionCode Code,
    string? SourceName = null);

/// <summary>
/// Bounded extraction output. A ZIP may contain accepted reports and rejected junk;
/// fatal container failures return no payloads. <see cref="ContentSha256"/> is the
/// lowercase SHA-256 of the original request bytes, ready for the later raw-ingest
/// layer to compare before parsing or persistence.
/// </summary>
public sealed record ReportPayloadExtractionResult(
    IReadOnlyList<ExtractedReportPayload> Payloads,
    IReadOnlyList<ReportPayloadRejection> Rejections,
    string? ContentSha256,
    long PayloadBytes,
    ReportPayloadContainer Container = ReportPayloadContainer.Unknown)
{
    public bool HasPayloads => Payloads.Count > 0;
}
