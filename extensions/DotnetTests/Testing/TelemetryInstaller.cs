
namespace AgentsDashboard.Extensions.DotnetTests;

/// <summary>What installing test telemetry into a repo would, or did, do.</summary>
/// <param name="Path">The file written.</param>
/// <param name="Created">True when the file did not exist before.</param>
/// <param name="Content">What the file now holds, or would hold.</param>
public sealed record TelemetryInstall(string Path, bool Created, string Content);

/// <summary>
/// Turns on always-on TRX reporting for a repository.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard reads test runs out of TRX reports, and neither runner writes
/// one unless told to. An agent typing <c>dotnet test</c> will not tell it to, so
/// without this its runs are invisible. One file at the repo root fixes that for
/// every test project in it, whichever runner they use.
/// </para>
/// <para>
/// <c>Directory.Build.targets</c> rather than <c>.props</c> on purpose:
/// <c>IsTestProject</c> is set by the test SDK during its own import, which
/// happens after props and before targets. A condition on it in props is
/// evaluated before anything has set it and silently matches nothing.
/// </para>
/// <para>
/// This edits the user's repository, so it is always offered and never done on
/// its own. <see cref="Preview"/> shows exactly what would be written.
/// </para>
/// </remarks>
public static class TelemetryInstaller
{
    private const string Marker = "agents-dashboard: test telemetry";

    private const string Block = $"""
          <!-- {Marker}
               Makes every test project write a TRX report on every run, so runs an
               agent starts are visible. Microsoft.Testing.Platform streams its
               report as the run progresses; VSTest writes its one at the end.
               Remove this block to turn it off. -->
          <PropertyGroup Condition="'$(IsTestProject)' == 'true'">
            <VSTestLogger Condition="'$(VSTestLogger)' == ''">trx%3BLogFileName=$(MSBuildProjectName).trx</VSTestLogger>
            <TestingPlatformCommandLineArguments>$(TestingPlatformCommandLineArguments) --report-trx</TestingPlatformCommandLineArguments>
          </PropertyGroup>
        """;

    /// <summary>What <see cref="Install"/> would write, without writing it.</summary>
    public static TelemetryInstall Preview(TestTelemetryReport report)
    {
        var existing = TryRead(report.TargetsPath);
        return new TelemetryInstall(
            report.TargetsPath,
            Created: existing is null,
            Content: Compose(existing));
    }

    /// <summary>Writes the block, creating or amending the file.</summary>
    public static TelemetryInstall Install(TestTelemetryReport report)
    {
        var preview = Preview(report);
        File.WriteAllText(preview.Path, preview.Content);
        return preview;
    }

    /// <summary>True when this repo already carries the block.</summary>
    public static bool IsInstalled(TestTelemetryReport report) =>
        TryRead(report.TargetsPath)?.Contains(Marker, StringComparison.Ordinal) == true;

    /// <summary>
    /// Adds the block to an existing file rather than replacing it: a repo's
    /// Directory.Build.targets usually has content of its own that must survive.
    /// </summary>
    private static string Compose(string? existing)
    {
        if (existing is null)
        {
            return $"<Project>\n\n{Block}\n\n</Project>\n";
        }

        if (existing.Contains(Marker, StringComparison.Ordinal))
        {
            return existing;
        }

        var close = existing.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (close < 0)
        {
            // Not a shape we recognize. Appending a second root would produce an
            // invalid file, so leave it alone and let the caller say so.
            return existing;
        }

        return existing[..close] + "\n" + Block + "\n\n" + existing[close..];
    }

    /// <summary>
    /// True when <see cref="Install"/> would leave the file unchanged because it
    /// could not find where to put the block. The UI offers a copyable snippet
    /// instead of pretending it worked.
    /// </summary>
    public static bool NeedsManualEdit(TestTelemetryReport report)
    {
        var existing = TryRead(report.TargetsPath);
        return existing is not null
               && !existing.Contains(Marker, StringComparison.Ordinal)
               && !existing.Contains("</Project>", StringComparison.Ordinal);
    }

    /// <summary>The block on its own, for showing or copying.</summary>
    public static string Snippet => Block;

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
