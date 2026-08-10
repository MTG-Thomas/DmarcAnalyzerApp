using System.Reflection;

namespace DmarcAnalyzer.Api.Application.Common;

/// <summary>
/// The release, and the commit when this is not a release build.
/// </summary>
/// <param name="Version">
/// The release number alone — <c>0.9.0</c>. Never empty; <see cref="AppVersion.UnknownVersion"/>
/// when the build did not stamp one.
/// </param>
/// <param name="Revision">
/// The full commit SHA, or null on a release build. Full rather than abbreviated
/// because a caller can always shorten, and a support question that starts from
/// seven characters sometimes has to end at forty.
/// </param>
public sealed record AppVersionInfo(string Version, string? Revision)
{
    /// <summary>Commit characters shown to a human. Git's own abbreviation length.</summary>
    private const int ShortRevisionLength = 7;

    /// <summary>The commit as a human would quote it, or null on a release build.</summary>
    public string? ShortRevision => Revision is null
        ? null
        : Revision.Length <= ShortRevisionLength ? Revision : Revision[..ShortRevisionLength];

    /// <summary>
    /// One string for logs and telemetry: <c>0.9.0</c>, or <c>0.9.0+a1b2c3d</c>
    /// for a build past a release.
    /// </summary>
    public string Display => Revision is null ? Version : $"{Version}+{ShortRevision}";
}

/// <summary>
/// The release this build came from, and the commit it was built at.
/// <para>
/// Read from <see cref="AssemblyInformationalVersionAttribute"/>, which the SDK
/// composes as <c>&lt;Version&gt;+&lt;SourceRevisionId&gt;</c>. Nothing here reads
/// an environment variable: an image whose version can be overridden at run time
/// can be made to lie about itself, and the whole point is answering "what am I
/// actually running" for someone on the <c>:latest</c> tag.
/// </para>
/// <para>
/// The commit is present exactly when the build was not a release. A local build
/// gets it from the git repository automatically; the container build gets it from
/// the <c>SOURCE_REVISION</c> build argument, which CI passes on every build
/// except a tag. So <c>0.9.0</c> means the 0.9.0 release, and
/// <c>0.9.0+a1b2c3d</c> means a build somewhere past it — an <c>edge</c> image or
/// a working tree. A release needs no commit of its own, because the tag pins it.
/// </para>
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// Shown when the attribute is missing entirely, which should not happen in
    /// anything built by this repository's tooling — the property is set in
    /// <c>Directory.Build.props</c>. Reported rather than thrown: an unknown
    /// version is a cosmetic problem, and refusing to start over it would turn
    /// one into an outage.
    /// </summary>
    public const string UnknownVersion = "unknown";

    /// <summary>This assembly's version, parsed once.</summary>
    public static AppVersionInfo Current { get; } = Parse(
        typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion);

    /// <summary>
    /// Splits an informational version into the release and the commit.
    /// <para>
    /// Separate from <see cref="Current"/> so the parsing is testable without
    /// asserting on the version this assembly happens to have been built with —
    /// a test written against that would need editing at every release.
    /// </para>
    /// </summary>
    public static AppVersionInfo Parse(string? informationalVersion)
    {
        var value = informationalVersion?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return new AppVersionInfo(UnknownVersion, null);
        }

        // Semver allows one '+' and everything after it is build metadata, so a
        // second one would be malformed input rather than a nested field. Split
        // on the first and keep the remainder whole.
        var plus = value.IndexOf('+');

        if (plus < 0)
        {
            return new AppVersionInfo(value, null);
        }

        var version = value[..plus];
        var revision = value[(plus + 1)..];

        return new AppVersionInfo(
            version.Length == 0 ? UnknownVersion : version,
            revision.Length == 0 ? null : revision);
    }
}
