namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class ApiSourceCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReportSourceId { get; set; }
    public string Prefix { get; set; } = string.Empty;
    public byte[] TokenHash { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAtUtc { get; set; }

    public ReportSource? ReportSource { get; set; }
}
