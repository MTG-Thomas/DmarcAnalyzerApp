namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// An in-flight WebAuthn ceremony (registration or authentication): the
/// challenge a stateless API host issued, keyed by an opaque handle the
/// browser returns with the credential. The challenge must live in the
/// database rather than server memory because the completing request may land
/// on a different host than the one that started the ceremony.
/// <para>
/// Single-use: completion sets <see cref="ConsumedAtUtc"/>, and the consume
/// is an atomic <c>UPDATE ... WHERE "ConsumedAtUtc" IS NULL</c> so only one
/// of two racing completions wins. Expired rows are swept by retention.
/// </para>
/// </summary>
public sealed class PasskeyCeremonyState
{
    public string Handle { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public byte[] Challenge { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public int Attempts { get; set; }
}
