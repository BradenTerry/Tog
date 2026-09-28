using AgentsDashboard.Core.Presentation;

namespace AgentsDashboard.Core.Tests;

public class FmtClockTests
{
    private static readonly DateTimeOffset Afternoon = new(2026, 9, 29, 14, 5, 0, TimeSpan.Zero);

    // One test for both settings: the clock is a static, and two tests flipping it
    // could interleave with each other.
    [Fact]
    public void Clock_follows_the_setting()
    {
        var before = Fmt.TwentyFourHourClock;
        try
        {
            Fmt.TwentyFourHourClock = false;
            Assert.Equal("2:05 PM", Fmt.Clock(Afternoon));
            Assert.Equal("Tue 2:05 PM", Fmt.DayClock(Afternoon));

            Fmt.TwentyFourHourClock = true;
            Assert.Equal("14:05", Fmt.Clock(Afternoon));
            Assert.Equal("Tue 14:05", Fmt.DayClock(Afternoon));
        }
        finally
        {
            Fmt.TwentyFourHourClock = before;
        }
    }

    [Fact]
    public void Twelve_hour_is_the_default()
    {
        Assert.False(new AgentsDashboard.Core.Repos.Settings().TwentyFourHourClock);
    }
}
