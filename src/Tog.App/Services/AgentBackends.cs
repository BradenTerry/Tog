using Tog.Core.Agents;
using Tog.Core.Claude;

namespace Tog.App.Services;

/// <summary>The agents this build can run.</summary>
/// <remarks>
/// Claude is the only one today. Another agent that speaks ACP is another
/// method here and a line in <c>Program.cs</c>; see docs/agent-control.md for
/// what a definition has to say.
/// </remarks>
public static class AgentBackends
{
    public const string BridgePackage = "@agentclientprotocol/claude-agent-acp";

    /// <summary>
    /// Claude, through the ACP bridge installed in <c>acp/</c> next to the project.
    /// A missing bridge is reported rather than thrown, so the app still opens and
    /// says what to do instead of failing at the first agent started.
    /// </summary>
    public static AgentBackend Claude(string contentRoot, ClaudeTranscripts transcripts)
    {
        var entry = Path.Combine(contentRoot, "acp", "node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js");

        return new AgentBackend(
            "claude",
            "Claude",
            "node",
            [entry],
            File.Exists(entry)
                ? null
                : $"The Claude ACP bridge is not installed ({BridgePackage}). Install Node 22 or newer, then run tools/vendor-acp.mjs or build the app again.")
            .Hooked(transcripts);
    }
}
