using DmarcAnalyzer.Api.Application.Hosting;
using DmarcAnalyzer.Api.Workers;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The <c>worker-once</c> mode: run every pass once and exit. Parser, mode
/// predicates and the run-once options' defaults.
/// </summary>
public sealed class WorkerOnceModeTests
{
    [Theory]
    [InlineData("worker-once")]
    [InlineData("WORKER-ONCE")]
    [InlineData("  worker-once  ")]
    public void ParsesWorkerOnce(string value)
        => Assert.Equal(AppMode.WorkerOnce, AppRuntimeMode.Parse(value));

    [Fact]
    public void ToNameIsTheAcceptedSpelling()
    {
        Assert.Equal("worker-once", AppMode.WorkerOnce.ToName());
        Assert.Equal(AppMode.WorkerOnce, AppRuntimeMode.Parse(AppMode.WorkerOnce.ToName()));
    }

    [Fact]
    public void UnknownValueErrorListsWorkerOnce()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AppRuntimeMode.Parse("woker"));

        Assert.Contains("worker-once", ex.Message);
    }

    [Fact]
    public void WorkerOnceRunsWorkerPassesButNoHttp()
    {
        Assert.True(AppMode.WorkerOnce.RunsWorker());
        Assert.False(AppMode.WorkerOnce.RunsHttp());
    }

    [Fact]
    public void WorkerOnceIsOneShot()
    {
        Assert.True(AppMode.WorkerOnce.IsOneShot());
        Assert.False(AppMode.Worker.IsOneShot());
    }

    [Fact]
    public void OptionsCarryTheShippedDefaults()
    {
        var options = new WorkerOnceOptions();

        Assert.Equal(50, options.OverallTimeoutMinutes);
        Assert.Equal(30, options.LockWaitSeconds);
        Assert.Equal("WorkerOnce", WorkerOnceOptions.SectionName);
    }
}
