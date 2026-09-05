using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Testing;

/// <summary>
/// Works out what a repository's test projects look like, and whether a run
/// started by an agent will be visible while it happens.
/// </summary>
/// <remarks>
/// <para>
/// Two questions decide that. Which runner the projects use, because only
/// Microsoft.Testing.Platform streams its report as the run progresses; and
/// whether a report is produced at all, because neither runner writes one unless
/// asked, and an agent typing <c>dotnet test</c> does not ask.
/// </para>
/// <para>
/// Answered by reading project files as text rather than by evaluating them with
/// MSBuild. Evaluation would be exact but costs a design-time build per project,
/// which is far too slow for something the Tests view wants on open. The
/// consequence is that this reports what it can see, and the UI states it as an
/// observation rather than a fact.
/// </para>
/// </remarks>
public static class TestProjectProbe
{
    private const int MaxDepth = 10;

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", "packages", ".venv", "venv",
    };

    /// <summary>Marks that put a project on Microsoft.Testing.Platform.</summary>
    private static readonly string[] MtpMarkers =
    [
        "UseMicrosoftTestingPlatformRunner",
        "EnableMSTestRunner",
        "EnableNUnitRunner",
        "Sdk=\"MSTest.Sdk\"",
        "Microsoft.Testing.Platform",
        "Microsoft.Testing.Extensions",
        "TestingPlatformCommandLineArguments",
    ];

    /// <summary>Marks that make a project a test project at all.</summary>
    private static readonly string[] TestMarkers =
    [
        "Microsoft.NET.Test.Sdk",
        "Sdk=\"MSTest.Sdk\"",
        "xunit",
        "NUnit",
        "MSTest",
        "Microsoft.Testing.Platform",
        "<IsTestProject>true</IsTestProject>",
    ];

    public static TestTelemetryReport Probe(string repoRoot)
    {
        var shared = ReadShared(repoRoot);
        var projects = new List<TestProjectInfo>();

        foreach (var projectFile in FindProjects(repoRoot))
        {
            var text = TryRead(projectFile);
            if (text is null || !TestMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var combined = text + shared;
            var runner = MtpMarkers.Any(m => combined.Contains(m, StringComparison.OrdinalIgnoreCase))
                ? TestRunnerKind.MicrosoftTestingPlatform
                : TestRunnerKind.VsTest;

            projects.Add(new TestProjectInfo(
                projectFile,
                Path.GetFileNameWithoutExtension(projectFile),
                runner));
        }

        projects.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        return new TestTelemetryReport
        {
            RepoRoot = repoRoot,
            Projects = projects,
            TrxAlwaysOn = HasAlwaysOnTrx(shared, projects, repoRoot),
            TargetsPath = Path.Combine(repoRoot, "Directory.Build.targets"),
        };
    }

    /// <summary>
    /// Whether something already forces a report on every run. Both runners have
    /// their own switch, so both are looked for.
    /// </summary>
    private static bool HasAlwaysOnTrx(
        string shared,
        IReadOnlyList<TestProjectInfo> projects,
        string repoRoot)
    {
        if (shared.Contains("--report-trx", StringComparison.OrdinalIgnoreCase)
            || shared.Contains("<VSTestLogger>", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (projects.Any(p => TryRead(p.ProjectPath) is { } t
                              && (t.Contains("--report-trx", StringComparison.OrdinalIgnoreCase)
                                  || t.Contains("<VSTestLogger>", StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        // A .runsettings with the TRX logger does the same thing for VSTest.
        try
        {
            return Directory
                .EnumerateFiles(repoRoot, "*.runsettings", SearchOption.TopDirectoryOnly)
                .Any(f => TryRead(f)?.Contains("TrxLogger", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The repo-wide files whose properties every project inherits, read once and
    /// appended to each project's own text before matching.
    /// </summary>
    private static string ReadShared(string repoRoot)
    {
        var parts = new List<string>();
        foreach (var name in new[]
                 {
                     "Directory.Build.props", "Directory.Build.targets",
                     "Directory.Packages.props", "global.json", "dotnet.config",
                 })
        {
            var text = TryRead(Path.Combine(repoRoot, name));
            if (text is not null)
            {
                parts.Add(text);
            }
        }

        return string.Join('\n', parts);
    }

    private static IEnumerable<string> FindProjects(string repoRoot)
    {
        var found = new List<string>();
        Walk(repoRoot, 0, found);
        return found;
    }

    private static void Walk(string dir, int depth, List<string> found)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        try
        {
            foreach (var pattern in new[] { "*.csproj", "*.fsproj", "*.vbproj" })
            {
                found.AddRange(Directory.EnumerateFiles(dir, pattern));
            }

            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                if (!Skip.Contains(Path.GetFileName(child)))
                {
                    Walk(child, depth + 1, found);
                }
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Nothing readable here.
        }
    }

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
