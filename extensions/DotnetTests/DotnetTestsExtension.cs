using AgentsDashboard.Extensions.DotnetTests.Components;
using Microsoft.Extensions.DependencyInjection;

namespace AgentsDashboard.Extensions.DotnetTests;

/// <summary>Registers the Tests tab, its failing-count indicator, and the tracker.</summary>
public sealed class DotnetTestsExtension : IDashboardExtension
{
    public void Configure(IExtensionBuilder builder)
    {
        builder.Services.AddSingleton<ITestProcessScanner, TestProcessScanner>();
        builder.Services.AddSingleton<TestRunTracker>();
        builder.Services.AddSingleton<TestRunner>();

        // A single cache shared by the tab filter below and the tab itself.
        var telemetry = new TelemetryCache();
        builder.Services.AddSingleton(telemetry);

        builder.AddWorker<TrackerWorker>("tracker");
        // About the worktree rather than an agent, so the tab is there for a
        // worktree opened from the title bar with no agent picked.
        builder.AddWorktreeView<TestsTab>(
            "tests",
            "Tests",
            order: 40,
            appliesTo: w => telemetry.Get(w.RepoRoot).Projects.Count > 0);
        builder.AddWorktreeIndicator<FailingTestsIndicator>("tests");
    }
}

/// <summary>
/// Follows test runs in every worktree the dashboard knows, once a second. What
/// the app's monitor loop used to do on its own tick.
/// </summary>
internal sealed class TrackerWorker(IDashboardView view, TestRunTracker tracker, ITestProcessScanner scanner)
    : IExtensionWorker
{
    public async Task RunAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                tracker.SetWorktrees(view.Current.Worktrees.Select(w => w.Path).ToList());
                tracker.Poll(scanner);
            }
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false));
        }
        finally
        {
            tracker.Dispose();
        }
    }
}

/// <summary>The failing count from the latest run, in red on the tab.</summary>
internal sealed class FailingTestsIndicator(TestRunTracker tracker) : IWorktreeIndicator
{
    public Indicator? For(WorktreeContext worktree) =>
        tracker.RunsFor(worktree.Path) is [{ Failed: > 0 and var failed }, ..]
            ? new Indicator(failed.ToString(System.Globalization.CultureInfo.InvariantCulture), Tone.Danger, "Failing in the latest run")
            : null;
}

/// <summary>Wall clock, injectable so anything that measures elapsed time is testable without sleeping.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

/// <inheritdoc />
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

/// <summary>The few formatting helpers the tab uses, kept here so the extension needs nothing from the app.</summary>
internal static class Fmt
{
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 60)
        {
            return $"{(int)span.TotalSeconds}s";
        }

        if (span.TotalMinutes < 60)
        {
            var seconds = span.Seconds;
            return seconds == 0 ? $"{(int)span.TotalMinutes}m" : $"{(int)span.TotalMinutes}m {seconds}s";
        }

        var minutes = span.Minutes;
        return minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {minutes}m";
    }

    public static string Ago(DateTimeOffset at, DateTimeOffset now) =>
        now - at < TimeSpan.FromSeconds(5) ? "just now" : Duration(now - at) + " ago";

    public static string Count(int n, string noun) => $"{n} {(n == 1 ? noun : noun + "s")}";

    public static string RunStateName(TestRunState state) => state switch
    {
        TestRunState.Running => "running",
        TestRunState.Passed => "passed",
        TestRunState.Failed => "failed",
        _ => "stopped",
    };
}
