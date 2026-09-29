using Togue.Core.Model;

namespace Togue.Core.Monitoring;

/// <summary>Where the monitor learns which agents are running.</summary>
public interface IAgentSessionSource
{
    IReadOnlyList<AgentSession> Read();
}
