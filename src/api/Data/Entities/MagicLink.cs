namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// A single-client read-only share link. The full token is reveal-once; only
/// the prefix and SHA-256 hash persist, verified in fixed time like the
/// machine credentials (ADR 0010). Revocation is a state, not a delete, so the
/// audit trail can answer when a link died.
/// </summary>
public sealed class MagicLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public byte[] TokenHash { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }

    public Client? Client { get; set; }
}
