namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// Resource limits applied before a report payload reaches any parser or persistence
/// service. These limits are shared by mailbox and machine-ingest callers.
/// </summary>
public sealed class ReportPayloadExtractionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Maximum compressed or bare bytes accepted from one caller.</summary>
    public int MaxRequestBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Maximum expanded bytes across every file in one container.</summary>
    public int MaxExpandedBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>Maximum number of directory and file records in a ZIP archive.</summary>
    public int MaxArchiveEntries { get; set; } = 100;

    /// <summary>Maximum expanded bytes in one report file.</summary>
    public int MaxEntryBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Maximum expanded-to-compressed byte ratio for GZIP and ZIP data.</summary>
    public double MaxCompressionRatio { get; set; } = 100;

    public bool IsValid()
        => MaxRequestBytes > 0
           && MaxExpandedBytes > 0
           && MaxArchiveEntries > 0
           && MaxEntryBytes > 0
           && MaxEntryBytes <= MaxExpandedBytes
           && double.IsFinite(MaxCompressionRatio)
           && MaxCompressionRatio >= 1;
}
