namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class ServiceApiCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public byte[] TokenHash { get; set; } = [];
    public string[] Permissions { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}
