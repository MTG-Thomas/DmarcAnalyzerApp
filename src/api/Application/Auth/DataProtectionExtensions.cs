using System.Xml.Linq;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>
/// Durable ASP.NET Core Data Protection: key rings live in the <c>dp_key</c> table instead of the local key directory, so every replica and every cold start shares one ring. To enable it, replace the <c>builder.Services.AddDataProtection()</c> line in <c>Program.cs</c> with <c>builder.Services.AddDurableDataProtection()</c> once the <c>dp_key</c> table exists (needs-migration-merge: the schema worker adds the <c>DbSet</c>, calls <c>ApplyDpKeyMapping</c> from <c>OnModelCreating</c>, and ships the migration); nothing else changes, and the returned <see cref="IDataProtectionBuilder"/> stays open for the explicit follow-up of encrypting keys at rest via <c>ProtectKeysWithAzureKeyVault</c> (or <c>ProtectKeysWith*</c>), which is the hook point — call one of those on the returned builder once Key Vault wiring exists, and do not build it here.
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
