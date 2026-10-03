using System.Text;
using System.Xml.Linq;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// At-rest encryption for the durable ring. No test here touches a live vault:
/// the Key Vault path is asserted at the wiring level (an encryptor is
/// registered, a bad identifier fails fast), and encrypted round-trips run
/// through a reversible in-memory <see cref="IXmlEncryptor"/>.
/// </summary>
public sealed class DataProtectionKeyVaultTests
{
    private const string ValidKeyId = "https://vault-name.vault.azure.net/keys/dp-key/abc123";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOrEmptyKeyId_StoresPlaintextRing(string? keyId)
    {
        var databaseName = $"dp-kv-off-{Guid.NewGuid():N}";
        var configuration = ConfigurationWith(keyId);

        using var first = BuildProvider(databaseName, configuration);
        var payload = first.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("kv-off-test")
            .Protect("session-secret");

        using (var scope = first.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            var keys = db.Set<DpKey>().AsNoTracking().ToList();
            Assert.Single(keys);
            Assert.Contains("<key", keys[0].Xml, StringComparison.Ordinal);
            Assert.Contains("<masterKey", keys[0].Xml, StringComparison.Ordinal);
        }

        using var second = BuildProvider(databaseName, configuration);
        var reopened = second.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("kv-off-test")
            .Unprotect(payload);

        Assert.Equal("session-secret", reopened);
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("vault-name.vault.azure.net/keys/dp-key")]
    [InlineData("http://vault-name.vault.azure.net/keys/dp-key/abc123")]
    public void InvalidKeyId_FailsFastNamingTheVariable(string keyId)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DmarcAnalyzerDbContext>(options =>
            options.UseInMemoryDatabase($"dp-kv-bad-{Guid.NewGuid():N}"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddDurableDataProtection(ConfigurationWith(keyId)));

        Assert.Contains("DataProtection__KeyVaultKeyId", exception.Message);
    }

    [Fact]
    public void ValidKeyId_RegistersKeyVaultEncryptorWithoutCallingIt()
    {
        // No Protect call here, so nothing reaches the network: resolving the
        // options only proves the wiring, which is all a unit test may assert
        // about the vault path.
        using var provider = BuildProvider($"dp-kv-wired-{Guid.NewGuid():N}", ConfigurationWith(ValidKeyId));

        var encryptor = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor;
        Assert.NotNull(encryptor);
        Assert.Contains("Azure", encryptor.GetType().FullName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CustomEncryptor_EncryptsAtRestAndRoundTripsAcrossInstances()
    {
        var databaseName = $"dp-kv-fake-{Guid.NewGuid():N}";

        using var first = BuildProvider(databaseName, new TestXmlEncryptor());
        var payload = first.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("kv-fake-test")
            .Protect("session-secret");

        using (var scope = first.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            var keys = db.Set<DpKey>().AsNoTracking().ToList();
            Assert.Single(keys);
            // The outer <key> wrapper (id, dates, algorithms) is never secret;
            // encryption seals the <masterKey> into an <encryptedSecret>.
            Assert.DoesNotContain("<masterKey", keys[0].Xml, StringComparison.Ordinal);
            Assert.Contains("encryptedSecret", keys[0].Xml, StringComparison.Ordinal);
            Assert.Contains("encrypted-test", keys[0].Xml, StringComparison.Ordinal);
        }

        using var second = BuildProvider(databaseName, new TestXmlEncryptor());
        var reopened = second.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("kv-fake-test")
            .Unprotect(payload);

        Assert.Equal("session-secret", reopened);
    }

    private static IConfiguration ConfigurationWith(string? keyId)
    {
        var pairs = keyId is null
            ? []
            : new KeyValuePair<string, string?>[1]
            {
                new("DataProtection:KeyVaultKeyId", keyId),
            };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(pairs)
            .Build();
    }

    private static ServiceProvider BuildProvider(string databaseName, IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DmarcAnalyzerDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddDurableDataProtection(configuration);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildProvider(string databaseName, IXmlEncryptor encryptor)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DmarcAnalyzerDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddDurableDataProtection(encryptor);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Reversible stand-in for the Key Vault encryptor: wraps the key XML in a
    /// marker element rather than sealing it, so tests can assert on the stored
    /// shape without a vault.
    /// </summary>
    private sealed class TestXmlEncryptor : IXmlEncryptor
    {
        public EncryptedXmlInfo Encrypt(XElement plaintextElement)
        {
            var bytes = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
            return new EncryptedXmlInfo(
                new XElement("encrypted-test", Convert.ToBase64String(bytes)),
                typeof(TestXmlDecryptor));
        }
    }

    private sealed class TestXmlDecryptor : IXmlDecryptor
    {
        public XElement Decrypt(XElement encryptedElement)
        {
            var bytes = Convert.FromBase64String(encryptedElement.Value);
            return XElement.Parse(Encoding.UTF8.GetString(bytes));
        }
    }
}
