using System.Collections.Concurrent;
using AgentsDashboard.Core.Agents;

namespace AgentsDashboard.App.Services;

/// <summary>
/// What you had typed to each agent and not sent yet.
/// </summary>
/// <remarks>
/// The chat panel is rebuilt whenever the agent changes under it, so switching
/// agents used to clear the box. Kept here, outside the page, a half-written message
/// waits for you when you come back, including after a visit to another page.
/// Held in memory only: it is a draft, not something worth writing to disk.
/// </remarks>
public sealed class ChatDrafts
{
    private readonly ConcurrentDictionary<string, string> _drafts = new(StringComparer.Ordinal);

    /// <summary>Half-given answers to the questions an agent has open, one form per agent.</summary>
    private readonly ConcurrentDictionary<string, QuestionDraft> _answers = new(StringComparer.Ordinal);

    public string Get(string sessionId) => _drafts.TryGetValue(sessionId, out var text) ? text : "";

    /// <summary>
    /// Your answers so far to an agent's form. A form the agent has moved past is
    /// replaced by the next, so at most one per agent is kept.
    /// </summary>
    public QuestionDraft Answers(string sessionId, QuestionForm form) =>
        _answers.AddOrUpdate(
            sessionId,
            _ => new QuestionDraft(form),
            (_, draft) => draft.Form.Key == form.Key ? draft : new QuestionDraft(form));

    public void Set(string sessionId, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _drafts.TryRemove(sessionId, out _);
        }
        else
        {
            _drafts[sessionId] = text;
        }
    }
}
