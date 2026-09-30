using Tog.Core.Agents;
using Tog.Core.Model;

namespace Tog.Core.Claude;

/// <summary>
/// Claude's history, read from the transcripts the Claude CLI keeps under
/// <c>~/.claude/projects</c>: the same ones a conversation run in a terminal
/// writes, so a session picked up from there shows everything said before.
/// </summary>
public sealed class ClaudeTranscripts(
    TranscriptLocator locator,
    ConversationReader conversations,
    TranscriptReader transcripts,
    SubagentReader subagents,
    PastSessionReader past) : IAgentTranscripts
{
    public IReadOnlyList<ChatEntry>? Conversation(string sessionId, string cwd) =>
        locator.Locate(sessionId, cwd) is { } path ? conversations.Read(sessionId, path) : null;

    /// <summary>Kept apart from the agent's own cursor, under a key of its own, and read with the side-chain lines the agent's conversation leaves out.</summary>
    public IReadOnlyList<ChatEntry>? SubagentConversation(string sessionId, string cwd, string subagentId) =>
        locator.SubagentTranscript(sessionId, cwd, subagentId) is { } own
            ? conversations.Read(sessionId + "/" + subagentId, own, sidechain: true)
            : null;

    public SessionActivity? Activity(string sessionId, string cwd)
    {
        var transcript = locator.Locate(sessionId, cwd);
        var facts = transcript is null
            ? new TranscriptFacts(null, [], null, null)
            : transcripts.Read(sessionId, transcript);

        return new SessionActivity(
            facts.Summary,
            facts.LastPrompt,
            facts.LastReply,
            facts.Skills,
            [.. subagents.Read(sessionId, cwd).Where(s => !Finished(s, facts.FinishedSubagents))],
            facts.BackgroundCommands ?? []);
    }

    public IReadOnlyList<PastSession> PastSessions(string cwd) => past.For(cwd);

    public void Forget(IReadOnlySet<string> liveSessionIds)
    {
        locator.Forget(liveSessionIds);
        transcripts.Forget(liveSessionIds);
    }

    /// <summary>
    /// A subagent the session heard back from, and whose own transcript has not
    /// been written since, which a resumed one would be. A few seconds' grace,
    /// since its last line lands just before the notice about it.
    /// </summary>
    public static bool Finished(Subagent subagent, IReadOnlyDictionary<string, DateTimeOffset>? finished) =>
        subagent.ToolUseId is { } call
        && finished?.TryGetValue(call, out var at) == true
        && (subagent.LastActivity is not { } last || last <= at + TimeSpan.FromSeconds(10));
}
