using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// One ASP.NET Core Data Protection key, persisted so every API replica (and
/// every cold start) decrypts what any other replica protected: session
/// cookies, passkey ceremony handles, and anything else the protectors seal.
/// Key management reads the whole table on startup and appends on rotation,
/// so this stays a handful of rows with no tenancy and no foreign keys.
/// </summary>
[Table("dp_key")]
public sealed class DpKey
{
    public int Id { get; set; }
    public string Xml { get; set; } = string.Empty;
}

/// <summary>
/// Fluent mapping for <see cref="DpKey"/>, mirroring the data annotations on
/// the entity. Call this from <c>DmarcAnalyzerDbContext.OnModelCreating</c>
/// alongside the other entity mappings once the owning migration exists.
/// </summary>
public static class DpKeyModelBuilderExtensions
{
    public static void ApplyDpKeyMapping(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DpKey>(entity =>
        {
            entity.ToTable("dp_key");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Xml).IsRequired();
        });
    }
}
