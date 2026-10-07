using System.Net;
using System.Net.Sockets;
using DmarcAnalyzer.Api.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
public sealed class WorkerLockStartupIntegrationTests(PostgreSqlDatabaseFixture database)
{
    private static WorkerSingleInstanceLock CreateLock(string connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString }).Build();
        return new WorkerSingleInstanceLock(configuration, Options.Create(new WorkerOptions()),
            NullLogger<WorkerSingleInstanceLock>.Instance);
    }

    [Fact]
    public async Task LateDatabaseForwardAllowsRetryWithoutGivingTwoWorkersOwnership()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        var target = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        var forwarded = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Host = "127.0.0.1", Port = port, Timeout = 1,
        };
        await using var owner = CreateLock(forwarded.ConnectionString);
        await using var contender = CreateLock(database.ConnectionString);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var acquiring = owner.AcquireAsync(cancellation.Token);
        await Task.Delay(200, cancellation.Token);
        Assert.False(acquiring.IsCompleted); // Refusal must not stop the host.
        listener.Listen(1);
        var proxy = ForwardAsync(listener, target.Host!, target.Port, cancellation.Token);
        try
        {
            await acquiring.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(await contender.TryAcquireAsync(cancellation.Token));
            await owner.DisposeAsync();
            Assert.True(await contender.TryAcquireAsync(cancellation.Token));
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await proxy; }
            catch (OperationCanceledException) { }
            catch (IOException) when (cancellation.IsCancellationRequested) { }
            catch (SocketException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private static async Task ForwardAsync(Socket listener, string host, int port, CancellationToken token)
    {
        using var incoming = await listener.AcceptAsync(token);
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(host, port, token);
        await using var source = new NetworkStream(incoming, ownsSocket: false);
        await using var destination = upstream.GetStream();
        await Task.WhenAll(source.CopyToAsync(destination, token), destination.CopyToAsync(source, token));
    }
}
