using System.Xml.Linq;
using Azure.Identity;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>
/// Durable ASP.NET Core Data Protection: the key ring lives in the <c>dp_key</c> table instead of the local
/// key directory, so every replica and every cold start shares one ring.
/// <para>
/// At-rest encryption is config-driven: the <see cref="IConfiguration"/> overload reads
/// <c>DataProtection:KeyVaultKeyId</c> and envelope-encrypts every row through that Azure Key Vault key when
/// it is set. Empty (the default) keeps the plaintext ring — see <c>docs/ops/configuration.md</c>.
/// </para>
/// </summary>
public static class DataProtectionExtensions
{
    public static IDataProtectionBuilder AddDurableDataProtection(this IServiceCollection services)
    {
        var builder = services.AddDataProtection();
        services.AddSingleton<IXmlRepository, EfXmlRepository>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IDbContextOptionsConfiguration<DmarcAnalyzerDbContext>,
            DpKeyDbContextOptionsConfiguration>());
        services.AddOptions<KeyManagementOptions>()
            .Configure<IXmlRepository>((options, repository) => options.XmlRepository = repository);
        return builder;
    }

    /// <summary>Where the Vault key identifier lives in configuration.</summary>
    public const string KeyIdConfigPath = "DataProtection:KeyVaultKeyId";

    /// <summary>
    /// Durable ring plus config-driven at-rest encryption. A set <see cref="KeyIdConfigPath"/> must be an
    /// absolute <c>https://</c> Vault key identifier and fails startup otherwise; empty leaves the ring in
    /// plaintext, which is the default.
    /// </summary>
    public static IDataProtectionBuilder AddDurableDataProtection(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = services.AddDurableDataProtection();
        var keyIdValue = configuration[KeyIdConfigPath];

        if (string.IsNullOrWhiteSpace(keyIdValue))
        {
            return builder;
        }

        if (!Uri.TryCreate(keyIdValue, UriKind.Absolute, out var keyId)
            || !string.Equals(keyId.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "DataProtection__KeyVaultKeyId must be an absolute https:// Key Vault key identifier " +
                "(e.g. https://vault-name.vault.azure.net/keys/dp-key/<version>); " +
                "leave it empty to keep the dp_key ring in plaintext.");
        }

        // Envelope encryption: every dp_key row is sealed under this Vault key. Authentication is ambient
        // (DefaultAzureCredential) — managed or workload identity in Azure, environment or developer
        // credentials elsewhere — so there is no secret to pass alongside the identifier.
        return builder.ProtectKeysWithAzureKeyVault(keyId, new DefaultAzureCredential());
    }

    /// <summary>
    /// Durable ring persisted through <paramref name="encryptor"/> instead of a Vault key. This is the seam
    /// the tests seal the ring with (no live vault in tests) and the escape hatch for a non-Azure encryptor;
    /// production Key Vault wiring goes through the <see cref="IConfiguration"/> overload, not here.
    /// </summary>
    public static IDataProtectionBuilder AddDurableDataProtection(
        this IServiceCollection services, IXmlEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);

        var builder = services.AddDurableDataProtection();
        services.Configure<KeyManagementOptions>(options => options.XmlEncryptor = encryptor);
        return builder;
    }
}

/// <summary>
/// Adds the <see cref="DpKey"/> mapping to the shared context's model without
/// touching the context itself. Once the schema worker wires the
/// <c>DbSet</c> and calls <c>ApplyDpKeyMapping</c> from
/// <c>OnModelCreating</c>, this runs the same mapping a second time, which is
/// idempotent; it stays so a context built without that wiring still resolves
/// the repository.
/// </summary>
internal sealed class DpKeyDbContextOptionsConfiguration : IDbContextOptionsConfiguration<DmarcAnalyzerDbContext>
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.ReplaceService<IModelCustomizer, DpKeyModelCustomizer>();
}

/// <summary>
/// Applies <see cref="DpKeyModelBuilderExtensions.ApplyDpKeyMapping"/> on top
/// of whatever <c>OnModelCreating</c> built.
/// </summary>
internal sealed class DpKeyModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        modelBuilder.ApplyDpKeyMapping();
    }
}

/// <summary>
/// An <see cref="IXmlRepository"/> over <c>dp_key</c> through the shared
/// <see cref="DmarcAnalyzerDbContext"/>. Singleton-safe: each call opens its
/// own scope rather than holding a context. Concurrent first-use inserts from
/// two replicas both land and both keys stay valid, which is how the key
/// manager expects a shared repository to behave.
/// </summary>
internal sealed class EfXmlRepository(IServiceScopeFactory scopeFactory) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
        return db.Set<DpKey>().AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => x.Xml)
            .AsEnumerable()
            .Select(XElement.Parse)
            .ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
        db.Set<DpKey>().Add(new DpKey { Xml = element.ToString(SaveOptions.DisableFormatting) });
        db.SaveChanges();
    }
}
