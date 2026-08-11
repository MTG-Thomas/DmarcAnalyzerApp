namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// Metadata supplied alongside a raw payload. File and media labels are untrusted;
/// content and magic bytes always win when they disagree. An optional expected digest
/// is checked after the bounded read and before parsing or persistence.
/// </summary>
public sealed record ReportPayloadMetadata(
    string? FileName = null,
    string? MediaType = null,
    string? ExpectedContentSha256 = null);
