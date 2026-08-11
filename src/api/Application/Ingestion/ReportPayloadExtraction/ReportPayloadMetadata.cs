namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// Untrusted descriptive labels supplied alongside a raw payload. Content and magic
/// bytes always win when a label disagrees.
/// </summary>
public sealed record ReportPayloadMetadata(
    string? FileName = null,
    string? MediaType = null);
