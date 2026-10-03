using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class DataProtectionDurabilityTests
{
    [Fact]
    public void ProtectedPayloadUnprotectsOnAFreshProviderInstance()
    {
        var databaseName = $"dp-durability-{Guid.NewGuid():N}";
        using var first = BuildProvider(databaseName);
        var payload = first.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("durability-test")
            .Protect("session-secret");

        using (var scope = first.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            var keys = db.Set<DpKey>().AsNoTracking().ToList();
            Assert.Single(keys);
            Assert.Contains("<key", keys[0].Xml, StringComparison.Ordinal);
        }

        using var second = BuildProvider(databaseName);
        var reopened = second.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("durability-test")
            .Unprotect(payload);

        Assert.Equal("session-secret", reopened);
    }

    [Fact]
    public void SecondInstanceReusesKeysInsteadOfMinting()
    {
        var databaseName = $"dp-durability-{Guid.NewGuid():N}";
        using var first = BuildProvider(databaseName);
        first.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("one");

        using var second = BuildProvider(databaseName);
        second.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("two");

        using var scope = second.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
        Assert.Single(db.Set<DpKey>().AsNoTracking().ToList());
    }

    private static ServiceProvider BuildProvider(string databaseName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DmarcAnalyzerDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddDurableDataProtection();
        return services.BuildServiceProvider();
    }
}
