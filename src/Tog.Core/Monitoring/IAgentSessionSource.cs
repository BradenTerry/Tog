using Tog.Core.Agents;
using Tog.Core.Model;

namespace Tog.Core.Monitoring;

/// <summary>Where the monitor learns which agents are running.</summary>
public interface IAgentSessionSource
{
    IReadOnlyList<AgentSession> Read();

    /// <summary>Where a kind of agent's history is read from, for what its sessions are doing.</summary>
    IAgentTranscripts? Transcripts(string backendId);
}
