namespace DmarcAnalyzer.Api.Application.Ingestion;

public interface IReportPayloadExtractor
{
    Task<ReportPayloadExtractionResult> ExtractAsync(
        Stream payload,
        ReportPayloadMetadata metadata,
        CancellationToken ct);
}
