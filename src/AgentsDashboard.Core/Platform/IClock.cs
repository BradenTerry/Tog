namespace AgentsDashboard.Core.Platform;

/// <summary>
/// Wall clock, injectable so anything that measures elapsed time is testable
/// without sleeping.
/// </summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

/// <inheritdoc />
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
