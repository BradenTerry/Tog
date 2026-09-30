using System.Text.Json;
using Tog.Core.Agents;

namespace Tog.Core.Claude;

/// <summary>What the Claude ACP bridge does its own way, for Claude's <see cref="AgentBackend"/>.</summary>
public static class ClaudeAgent
{
    /// <summary>
    /// A session's environment where the bridge takes it for that session alone:
    /// every session shares the bridge, so its own environment cannot hold
    /// anything one session's alone. The Claude CLI expands the <c>${NAME}</c> in
    /// an MCP server's header from it.
    /// </summary>
    public static object SessionMeta(IReadOnlyDictionary<string, string> env) =>
        new { claudeCode = new { options = new { env } } };

    /// <summary>
    /// The plan's rate limits, which the bridge passes along on a
    /// <c>usage_update</c> only when the SDK reports that they changed.
    /// </summary>
    public static IReadOnlyList<PlanLimit> ReadUsage(JsonElement meta, DateTimeOffset now) =>
        meta.TryGetProperty("_claude/rateLimit", out var rateLimit) ? PlanLimit.Read(rateLimit, now) : [];

    /// <summary>Claude's definition, without its launch command, which depends on where the app runs from.</summary>
    public static AgentBackend Hooked(this AgentBackend backend, IAgentTranscripts? transcripts = null) => backend with
    {
        SessionMeta = SessionMeta,
        ReadUsage = ReadUsage,
        Transcripts = transcripts,
        Defaults = new StartDefaults(StartChoices.Models, StartChoices.Efforts, StartChoices.Modes),
    };
}
