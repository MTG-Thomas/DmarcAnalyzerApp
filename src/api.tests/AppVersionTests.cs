using System.Reflection;
using DmarcAnalyzer.Api.Application.Common;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// What the app answers when a self-hoster on <c>:latest</c> asks which version
/// they are running. The distinction the parse carries is release versus
/// not-a-release: a commit is stamped on only when the build was not a tag, so
/// <c>0.9.0</c> and <c>0.9.0+a1b2c3d</c> are two different builds and have to read
/// as two different builds.
/// </summary>
public sealed class AppVersionTests
{
    [Fact]
    public void ReleaseBuildHasNoRevision()
    {
        var version = AppVersion.Parse("0.9.0");

        Assert.Equal("0.9.0", version.Version);
        Assert.Null(version.Revision);
        Assert.Null(version.ShortRevision);
        // No "+" for a release: this is the number a user compares against the
        // releases page, so anything appended is noise at best.
        Assert.Equal("0.9.0", version.Display);
    }

    [Fact]
    public void EdgeBuildKeepsTheFullCommitAndShowsSeven()
    {
        var version = AppVersion.Parse("0.9.0+b1c72c28cbb5c3704a9cddbd86088373df4692a9");

        Assert.Equal("0.9.0", version.Version);
        // Full SHA retained: a support thread that opens with seven characters
        // sometimes has to end at forty, and the UI can always shorten.
        Assert.Equal("b1c72c28cbb5c3704a9cddbd86088373df4692a9", version.Revision);
        Assert.Equal("b1c72c2", version.ShortRevision);
        Assert.Equal("0.9.0+b1c72c2", version.Display);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingAttributeReportsUnknownRatherThanThrowing(string? informational)
    {
        // Cosmetic failure only. Throwing here would turn "the sidebar shows no
        // version" into "the container does not start".
        var version = AppVersion.Parse(informational);

        Assert.Equal(AppVersion.UnknownVersion, version.Version);
        Assert.Null(version.Revision);
    }

    [Theory]
    // Build metadata present but empty — a SOURCE_REVISION build arg passed as ""
    // reaches the compiler as a bare trailing "+".
    [InlineData("0.9.0+", "0.9.0", null)]
    // A prerelease label belongs to the version, not the metadata.
    [InlineData("1.0.0-rc.1+abcdef1234", "1.0.0-rc.1", "abcdef1234")]
    // Semver allows one "+"; the remainder stays whole rather than being treated
    // as a nested field.
    [InlineData("0.9.0+a1b2c3d+extra", "0.9.0", "a1b2c3d+extra")]
    // Shorter than the abbreviation length, so there is nothing to trim.
    [InlineData("0.9.0+abc", "0.9.0", "abc")]
    public void ParsesTheAwkwardShapes(string informational, string version, string? revision)
    {
        var parsed = AppVersion.Parse(informational);

        Assert.Equal(version, parsed.Version);
        Assert.Equal(revision, parsed.Revision);
    }

    [Fact]
    public void CurrentComesFromTheBuildAndNotFromTheEnvironment()
    {
        // Deliberately not asserting a version number: it would need editing at
        // every release, and the useful property is that <Version> reached the
        // assembly at all. A build that lost it reports "unknown" and this fails.
        Assert.NotEqual(AppVersion.UnknownVersion, AppVersion.Current.Version);

        var attribute = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.NotNull(attribute);
        Assert.StartsWith(AppVersion.Current.Version, attribute!.InformationalVersion);
    }
}
