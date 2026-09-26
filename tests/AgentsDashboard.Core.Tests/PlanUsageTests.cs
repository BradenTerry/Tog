using System.Text.Json;
using AgentsDashboard.Core.Agents;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class PlanUsageTests
{
    private static readonly AgentBackend Backend = new("claude", "Claude", "fake", []);

    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<PlanLimit> Read(string json) =>
        PlanLimit.Read(JsonDocument.Parse(json).RootElement, Now);

    [Fact]
    public void Reads_the_headline_limit_with_its_status_and_reset()
    {
        var limit = Assert.Single(Read("""
            {"status":"allowed_warning","rateLimitType":"five_hour","utilization":0.62,"resetsAt":1788285600}
            """));

        Assert.Equal("five_hour", limit.Window);
        Assert.Equal(62, limit.Percent);
        Assert.False(limit.AtLeast);
        Assert.True(limit.Warning);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788285600), limit.ResetsAt);
        Assert.Equal("5-hour", limit.Label);
    }

    [Fact]
    public void Reads_every_unified_window_and_lets_the_headline_add_its_status()
    {
        var limits = Read("""
            {"status":"allowed","rateLimitType":"seven_day",
             "unifiedWindows":{"five_hour":{"utilization":0.1,"resetsAt":1788285600},
                               "seven_day":{"utilization":0.4,"resetsAt":1788800000}}}
            """).ToDictionary(l => l.Window);

        Assert.Equal(10, limits["five_hour"].Percent);
        Assert.Null(limits["five_hour"].Status);
        Assert.Equal(40, limits["seven_day"].Percent);
        Assert.Equal("allowed", limits["seven_day"].Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788800000), limits["seven_day"].ResetsAt);
    }

    [Fact]
    public void Falls_back_to_the_threshold_passed_when_there_is_no_figure()
    {
        var limit = Assert.Single(Read("""
            {"status":"allowed_warning","rateLimitType":"seven_day","surpassedThreshold":0.75}
            """));

        Assert.Equal(75, limit.Percent);
        Assert.True(limit.AtLeast);
    }

    [Fact]
    public void Elapsed_is_the_share_of_the_window_before_its_reset()
    {
        var fiveHour = new PlanLimit("five_hour", 0.5, null, null, Now.AddHours(3), Now);
        var weekly = new PlanLimit("seven_day_opus", 0.5, null, null, Now.AddDays(5.25), Now);
        var overage = new PlanLimit("overage", 0.5, null, null, Now.AddHours(3), Now);
        var noReset = new PlanLimit("five_hour", 0.5, null, null, null, Now);

        Assert.Equal(0.4, fiveHour.Elapsed(Now)!.Value, 6);
        Assert.Equal(0.25, weekly.Elapsed(Now)!.Value, 6);
        Assert.Equal(1, fiveHour.Elapsed(Now.AddHours(4)));
        Assert.Null(overage.Elapsed(Now));
        Assert.Null(noReset.Elapsed(Now));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"status":"allowed"}""")]
    public void Reads_nothing_from_a_payload_without_a_window(string json) =>
        Assert.Empty(Read(json));

    [Fact]
    public void A_status_only_reading_keeps_the_figure_for_the_same_window()
    {
        var reset = Now.AddHours(3);
        var earlier = new PlanLimit("five_hour", 0.8, null, "allowed", reset, Now);
        var later = new PlanLimit("five_hour", null, null, "allowed_warning", reset, Now.AddMinutes(5));

        var merged = later.After(earlier);

        Assert.Equal(0.8, merged.Utilization);
        Assert.Equal("allowed_warning", merged.Status);
        Assert.Equal(later, later.After(earlier with { ResetsAt = Now.AddHours(-2) }));
    }

    [Fact]
    public async Task The_host_keeps_what_the_bridge_reports_across_a_restart_until_it_resets()
    {
        var dir = new TempDir();
        using var _ = dir;
        var paths = new AppPaths(dir.Path);
        var clock = new FakeClock(Now);
        var reset = Now.AddHours(2).ToUnixTimeSeconds();
        var agent = new FakeAcpAgent
        {
            OnPrompt = async script =>
            {
                await script.RateLimit(new { status = "allowed", rateLimitType = "five_hour", utilization = 0.5, resetsAt = reset });
                return "end_turn";
            },
        };

        await using (var host = new AgentHost(Backend, agent, new HostedAgentStore(paths), clock, new PlanUsageStore(paths)))
        {
            Assert.Empty(host.PlanLimits);
            await host.StartAsync(new AgentStart("/repo", Prompt: "go"), Ct);
            for (var i = 0; i < 200 && host.PlanLimits.Count == 0; i++)
            {
                await Task.Delay(20, Ct);
            }

            Assert.Equal(50, Assert.Single(host.PlanLimits).Percent);
            await Task.Delay(250, Ct);
        }

        await using var second = new AgentHost(Backend, new FakeAcpAgent(), new HostedAgentStore(paths), clock, new PlanUsageStore(paths));
        Assert.Equal(50, Assert.Single(second.PlanLimits).Percent);

        clock.Advance(TimeSpan.FromHours(3));
        Assert.Empty(second.PlanLimits);
    }
}
