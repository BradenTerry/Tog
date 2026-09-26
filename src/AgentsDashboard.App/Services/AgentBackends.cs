using AgentsDashboard.Core.Agents;

namespace AgentsDashboard.App.Services;

/// <summary>The agents this build can run.</summary>
public static class AgentBackends
{
    public const string BridgePackage = "@agentclientprotocol/claude-agent-acp";

    /// <summary>
    /// Claude, through the ACP bridge installed in <c>acp/</c> next to the project.
    /// A missing bridge is reported rather than thrown, so the app still opens and
    /// says what to do instead of failing at the first agent started.
    /// </summary>
    public static AgentBackend Claude(string contentRoot)
    {
        var entry = Path.Combine(contentRoot, "acp", "node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js");

        return new AgentBackend(
            "claude",
            "Claude",
            "node",
            [entry],
            File.Exists(entry)
                ? null
                : $"The Claude ACP bridge is not installed ({BridgePackage}). Install Node 22 or newer, then run tools/vendor-acp.sh or build the app again.");
    }
}
