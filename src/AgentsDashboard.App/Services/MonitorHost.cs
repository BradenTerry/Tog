using AgentsDashboard.Core.Monitoring;

namespace AgentsDashboard.App.Services;

/// <summary>
/// Ties the monitor's lifetime to the host's.
/// </summary>
/// <remarks>
/// The monitor itself has no dependency on ASP.NET, so this is the only place
/// that knows it should start with the app. That keeps Core testable without a
/// web host and reusable outside one.
/// </remarks>
public sealed class MonitorHost(MonitorService monitor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        monitor.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await monitor.DisposeAsync();
}
