using AgentsDashboard.Extensions.DotnetTests;
using AgentsDashboard.Extensions.DotnetTests.Tests.Support;

namespace AgentsDashboard.Extensions.DotnetTests.Tests;

public class TestRunTrackerTests
{
    private const string Head = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="a" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Times creation="2026-09-05T10:00:00Z" start="2026-09-05T10:00:00Z" />
          <Results>

        """;

    private static string Result(string name, string outcome) =>
        "    <UnitTestResult executionId=\"" + name + "\" testId=\"" + name + "\" testName=\"" + name
        + "\" outcome=\"" + outcome + "\" duration=\"00:00:00.0100000\" />\n";

    private const string Tail = """
          </Results>
          <ResultSummary outcome="Completed"><Counters total="3" passed="2" failed="1" /></ResultSummary>
        </TestRun>
        """;

    /// <summary>A scanner that reports whichever worktrees the test says are busy.</summary>
    private sealed class FakeScanner(params string[] busy) : ITestProcessScanner
    {
        public IReadOnlyDictionary<string, int> Scan(IReadOnlyCollection<string> worktreePaths) =>
            busy.Where(worktreePaths.Contains).ToDictionary(w => w, _ => 4242);
    }

    [Fact]
    public void Follows_a_report_that_is_still_being_written()
    {
        using var dir = new TempDir();
        var worktree = dir.Path;
        var trx = Path.Combine(dir.Dir("TestResults"), "My.Tests_net10.0_arm64.trx");

        using var tracker = new TestRunTracker(new FakeClock());
        tracker.SetWorktrees([worktree]);

        // Partway through: two results written, no summary, no closing tag.
        File.WriteAllText(trx, Head + Result("A.One", "Passed") + Result("A.Two", "Failed"));
        tracker.Poll(new FakeScanner());

        var run = Assert.Single(tracker.RunsFor(worktree));
        Assert.Equal(TestRunState.Running, run.State);
        Assert.Equal(2, run.Completed);
        Assert.Equal(1, run.Passed);
        Assert.Equal(1, run.Failed);

        // The total is not known until the run says so, and a bar without a
        // denominator is not drawn.
        Assert.False(run.HasTotal);
        Assert.Null(run.Progress);
        Assert.Equal("My.Tests", run.ProjectName);
    }

    [Fact]
    public void Adds_results_as_they_arrive_without_repeating_the_ones_it_has()
    {
        using var dir = new TempDir();
        var worktree = dir.Path;
        var trx = Path.Combine(dir.Dir("TestResults"), "My.Tests_net10.0_arm64.trx");

        var clock = new FakeClock();
        using var tracker = new TestRunTracker(clock);
        tracker.SetWorktrees([worktree]);

        File.WriteAllText(trx, Head + Result("A.One", "Passed"));
        tracker.Poll(new FakeScanner());
        Assert.Equal(1, tracker.RunsFor(worktree)[0].Completed);

        File.AppendAllText(trx, Result("A.Two", "Passed") + Result("A.Three", "Failed"));
        clock.Advance(TimeSpan.FromSeconds(6));
        tracker.Poll(new FakeScanner());

        var run = tracker.RunsFor(worktree)[0];
        Assert.Equal(3, run.Completed);
        Assert.Equal(["A.One", "A.Two", "A.Three"], run.Results.Select(r => r.Name));
    }

    [Fact]
    public void Closes_the_run_when_the_report_closes()
    {
        using var dir = new TempDir();
        var worktree = dir.Path;
        var trx = Path.Combine(dir.Dir("TestResults"), "My.Tests_net10.0_arm64.trx");

        using var tracker = new TestRunTracker(new FakeClock());
        tracker.SetWorktrees([worktree]);

        File.WriteAllText(
            trx,
            Head + Result("A.One", "Passed") + Result("A.Two", "Passed") + Result("A.Three", "Failed") + Tail);
        tracker.Poll(new FakeScanner());

        var run = tracker.RunsFor(worktree)[0];
        Assert.Equal(TestRunState.Failed, run.State);
        Assert.Equal(3, run.Total);
        Assert.Equal(1.0, run.Progress);
        Assert.NotNull(run.CompletedAt);
    }

    [Fact]
    public void Reads_a_report_that_was_already_there_when_watching_began()
    {
        using var dir = new TempDir();
        var worktree = dir.Path;
        File.WriteAllText(
            Path.Combine(dir.Dir("TestResults"), "Old.Tests_net10.0_arm64.trx"),
            Head + Result("A.One", "Passed") + Tail);

        using var tracker = new TestRunTracker(new FakeClock());
        tracker.SetWorktrees([worktree]);
        tracker.Poll(new FakeScanner());

        Assert.Single(tracker.RunsFor(worktree));
    }

    [Fact]
    public void Shows_a_run_from_the_process_alone_while_there_is_no_report_yet()
    {
        using var dir = new TempDir();
        var worktree = dir.Path;

        using var tracker = new TestRunTracker(new FakeClock());
        tracker.SetWorktrees([worktree]);

        // This is the restore-and-build stretch: a test process exists and has
        // written nothing.
        tracker.Poll(new FakeScanner(worktree));

        var run = Assert.Single(tracker.RunsFor(worktree));
        Assert.Equal(TestRunState.Running, run.State);
        Assert.Null(run.TrxPath);
        Assert.Equal(4242, run.Pid);
        Assert.Equal(0, run.Completed);
    }

    [Fact]
    public void Replaces_the_process_placeholder_once_the_report_appears()
    {
        using var dir = new TempDir();
        var worktree = dir.Path;

        var clock = new FakeClock();
        using var tracker = new TestRunTracker(clock);
        tracker.SetWorktrees([worktree]);
        tracker.Poll(new FakeScanner(worktree));
        Assert.Single(tracker.RunsFor(worktree));

        File.WriteAllText(
            Path.Combine(dir.Dir("TestResults"), "My.Tests_net10.0_arm64.trx"),
            Head + Result("A.One", "Passed"));
        clock.Advance(TimeSpan.FromSeconds(6));
        tracker.Poll(new FakeScanner(worktree));

        var run = Assert.Single(tracker.RunsFor(worktree));
        Assert.NotNull(run.TrxPath);
    }

    [Fact]
    public void Calls_a_run_stopped_when_its_process_is_gone_and_nothing_has_been_written()
    {
        using var dir = new TempDir();
        var worktree = dir.Path;
        var clock = new FakeClock();
        var trx = Path.Combine(dir.Dir("TestResults"), "My.Tests_net10.0_arm64.trx");

        using var tracker = new TestRunTracker(clock);
        tracker.SetWorktrees([worktree]);

        File.WriteAllText(trx, Head + Result("A.One", "Passed"));
        tracker.Poll(new FakeScanner(worktree));
        Assert.Equal(TestRunState.Running, tracker.RunsFor(worktree)[0].State);

        // The host died: no process, no further writes.
        clock.Advance(TimeSpan.FromMinutes(3));
        tracker.Poll(new FakeScanner());

        Assert.Equal(TestRunState.Aborted, tracker.RunsFor(worktree)[0].State);
    }

    [Fact]
    public void Starts_over_when_the_report_is_rewritten_by_a_new_run()
    {
        using var dir = new TempDir();
        var worktree = dir.Path;
        var trx = Path.Combine(dir.Dir("TestResults"), "My.Tests_net10.0_arm64.trx");

        var clock = new FakeClock();
        using var tracker = new TestRunTracker(clock);
        tracker.SetWorktrees([worktree]);

        File.WriteAllText(
            trx,
            Head + Result("A.One", "Passed") + Result("A.Two", "Passed") + Result("A.Three", "Passed") + Tail);
        tracker.Poll(new FakeScanner());
        Assert.Equal(3, tracker.RunsFor(worktree)[0].Completed);

        // A second run overwrites the same path. Its results must not be merged
        // with the first run's.
        File.WriteAllText(trx, Head + Result("A.One", "Failed"));
        clock.Advance(TimeSpan.FromSeconds(6));
        tracker.Poll(new FakeScanner());

        var run = tracker.RunsFor(worktree)[0];
        Assert.Equal(1, run.Completed);
        Assert.Equal(TestRunState.Running, run.State);
    }

    [Fact]
    public void Drops_a_worktree_that_is_no_longer_watched()
    {
        using var dir = new TempDir();
        var worktree = dir.Path;
        File.WriteAllText(
            Path.Combine(dir.Dir("TestResults"), "My.Tests_net10.0_arm64.trx"),
            Head + Result("A.One", "Passed") + Tail);

        using var tracker = new TestRunTracker(new FakeClock());
        tracker.SetWorktrees([worktree]);
        tracker.Poll(new FakeScanner());
        Assert.Single(tracker.RunsFor(worktree));

        tracker.SetWorktrees([]);
        Assert.Empty(tracker.RunsFor(worktree));
    }
}
