
namespace AgentsDashboard.Extensions.DotnetTests;

/// <summary>
/// Caches what each repository's test projects look like.
/// </summary>
/// <remarks>
/// The probe walks a repository's project files, which is far too slow to repeat
/// on every render of the Tests tab, and the answer only changes when someone
/// edits a project file. Cached until asked to look again, which the install
/// action does.
/// </remarks>
public sealed class TelemetryCache
{
    private readonly Dictionary<string, TestTelemetryReport> _reports = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public TestTelemetryReport Get(string repoRoot)
    {
        lock (_gate)
        {
            if (_reports.TryGetValue(repoRoot, out var cached))
            {
                return cached;
            }
        }

        // Probed outside the lock: it is filesystem work, and two views asking at
        // once should not queue behind each other.
        var report = TestProjectProbe.Probe(repoRoot);

        lock (_gate)
        {
            _reports[repoRoot] = report;
            return report;
        }
    }

    public void Invalidate(string repoRoot)
    {
        lock (_gate)
        {
            _reports.Remove(repoRoot);
        }
    }
}
