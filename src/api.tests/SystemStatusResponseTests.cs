using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Hosting;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The payload of <c>/api/v1/system/status</c>. It reported <c>mode: "api"</c>
/// unconditionally for as long as it existed, and nothing caught it, because the shape
/// only existed inside an anonymous object in a running host. It is a type with a
/// factory now, so these are assertions rather than a code read.
/// </summary>
public sealed class SystemStatusResponseTests
{
    private static readonly DateTime At = new(2026, 8, 10, 10, 11, 28, DateTimeKind.Utc);

    [Theory]
    [InlineData(AppMode.Api, "api")]
    [InlineData(AppMode.Worker, "worker")]
    // The regression: an APP_MODE=all container is the shape the chart and Render both
    // deploy, and it used to report the one mode it was not running in.
    [InlineData(AppMode.All, "all")]
    [InlineData(AppMode.Migrate, "migrate")]
    // Hyphenated, so ToString() would have been wrong here too.
    [InlineData(AppMode.MtaSts, "mta-sts")]
    public void ReportsTheModeItIsActuallyRunning(AppMode mode, string expected)
    {
        var response = SystemStatusResponse.For(mode, new AppVersionInfo("0.9.0", null), At);

        Assert.Equal(expected, response.Mode);
    }

    [Fact]
    public void ReleaseBuildReportsNoRevision()
    {
        var response = SystemStatusResponse.For(
            AppMode.All, new AppVersionInfo("0.9.0", null), At);

        Assert.Equal(SystemStatusResponse.ServiceName, response.Service);
        Assert.Equal("0.9.0", response.Version);
        Assert.Null(response.Revision);
        Assert.Equal(At, response.TimestampUtc);
    }

    [Fact]
    public void PreReleaseBuildCarriesTheFullCommit()
    {
        // Full, not abbreviated: the console shortens it for display, and a caller that
        // wants to resolve the exact commit needs all of it.
        const string sha = "b1c72c28cbb5c3704a9cddbd86088373df4692a9";

        var response = SystemStatusResponse.For(
            AppMode.All, new AppVersionInfo("0.9.0", sha), At);

        Assert.Equal(sha, response.Revision);
    }

    [Fact]
    public void PassesThroughAVersionItCouldNotDetermine()
    {
        // The endpoint does not invent a number when the build did not stamp one. The
        // console is what decides how to render that, and it must not be handed
        // something version-shaped that is not a version.
        var response = SystemStatusResponse.For(
            AppMode.Api, AppVersion.Parse(null), At);

        Assert.Equal(AppVersion.UnknownVersion, response.Version);
        Assert.Null(response.Revision);
    }
}
