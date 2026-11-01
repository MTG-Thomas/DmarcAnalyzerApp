using DmarcAnalyzer.Api.Application.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class DatabaseSessionIdentityObserverTests
{
    [Fact]
    public void ReportsOnceAfterReuseAndKeepsSessionAndEffectiveUserDistinct()
    {
        var logger = new RecordingLogger();
        var observer = new DatabaseSessionIdentityObserver(logger);
        observer.PhysicalConnectionOpened(Key(1));
        observer.Observe(Key(1), observer.CaptureGeneration(Key(1)), "bifrostadmin", "bifrost", "bifrost");
        Assert.Empty(logger.Messages);
        observer.Observe(Key(1), observer.CaptureGeneration(Key(1)), "bifrostadmin", "bifrost", "bifrost");
        observer.Observe(Key(1), observer.CaptureGeneration(Key(1)), "bifrostadmin", "bifrost", "bifrost");
        Assert.Single(logger.Messages);
        Assert.Contains("session_user=bifrostadmin current_user=bifrost", logger.Messages[0]);
    }

    [Fact]
    public void FreshConnectionAndReusedBackendIdDoNotCountAsCachedGeneration()
    {
        var logger = new RecordingLogger();
        var observer = new DatabaseSessionIdentityObserver(logger);
        observer.Observe(Key(7), observer.CaptureGeneration(Key(7)), "runtime", "runtime", "dmarc");
        observer.PhysicalConnectionOpened(Key(7));
        observer.Observe(Key(7), observer.CaptureGeneration(Key(7)), "runtime", "runtime", "dmarc");
        observer.PhysicalConnectionOpened(Key(7));
        observer.Observe(Key(7), observer.CaptureGeneration(Key(7)), "runtime", "runtime", "dmarc");
        Assert.Empty(logger.Messages);
        observer.Observe(Key(7), observer.CaptureGeneration(Key(7)), "runtime", "runtime", "dmarc");
        Assert.Single(logger.Messages);
    }

    [Theory]
    [InlineData("sensitive\nvalue")]
    [InlineData("runtime\n")]
    [InlineData("")]
    public void UnsafeIdentityIsNeverLogged(string value)
    {
        var logger = new RecordingLogger();
        var observer = new DatabaseSessionIdentityObserver(logger);
        observer.PhysicalConnectionOpened(Key(1));
        observer.Observe(Key(1), observer.CaptureGeneration(Key(1)), value, "runtime", "dmarc");
        observer.Observe(Key(1), observer.CaptureGeneration(Key(1)), value, "runtime", "dmarc");
        Assert.Equal("Runtime database identity sample unavailable: unsafe_identity_result", Assert.Single(logger.Messages));
    }

    [Fact]
    public void StaleSampleCannotMarkAReplacementGenerationAsPreviouslyObserved()
    {
        var logger = new RecordingLogger();
        var observer = new DatabaseSessionIdentityObserver(logger);
        observer.PhysicalConnectionOpened(Key(1));
        var old = observer.CaptureGeneration(Key(1));
        observer.PhysicalConnectionOpened(Key(1));
        observer.Observe(Key(1), old, "runtime", "runtime", "dmarc");
        observer.Observe(Key(1), observer.CaptureGeneration(Key(1)), "runtime", "runtime", "dmarc");
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public void SameBackendIdOnDifferentHostsDoesNotCountAsReuse()
    {
        var logger = new RecordingLogger();
        var observer = new DatabaseSessionIdentityObserver(logger);
        var first = ("first.example", 5432, 1);
        var second = ("second.example", 5432, 1);
        observer.PhysicalConnectionOpened(first);
        observer.PhysicalConnectionOpened(second);
        observer.Observe(first, observer.CaptureGeneration(first), "runtime", "runtime", "dmarc");
        observer.Observe(second, observer.CaptureGeneration(second), "runtime", "runtime", "dmarc");
        Assert.Empty(logger.Messages);
    }

    private static (string Host, int Port, int BackendId) Key(int backendId) => ("db.example", 5432, backendId);

    private sealed class RecordingLogger : ILogger<DatabaseSessionIdentityObserver>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
