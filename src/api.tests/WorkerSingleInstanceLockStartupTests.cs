using System.Net;
using System.Net.Sockets;
using DmarcAnalyzer.Api.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class WorkerSingleInstanceLockStartupTests
{
    [Fact]
    public async Task RefusedStartupConnectionWaitsForCancellationRatherThanStoppingHost()
    {
        // Reserve a real local port without listening: deterministic refusal.
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Host=127.0.0.1;Port={port};Database=unused;Username=unused;Timeout=1",
            }).Build();
        await using var owner = new WorkerSingleInstanceLock(configuration,
            Options.Create(new WorkerOptions()), NullLogger<WorkerSingleInstanceLock>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => owner.AcquireAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }
}
