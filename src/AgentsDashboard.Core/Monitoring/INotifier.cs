using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Monitoring;

/// <summary>Raises an OS notification when an agent starts waiting.</summary>
public interface INotifier
{
    void AgentWaiting(WaitingAgent agent);
}

/// <summary>Notifications turned off.</summary>
public sealed class NoNotifier : INotifier
{
    public void AgentWaiting(WaitingAgent agent)
    {
    }
}
