using System.Collections.Concurrent;

namespace AgentsDashboard.App.Services;

/// <summary>
/// What you had typed to each agent and not sent yet.
/// </summary>
/// <remarks>
/// The agent view is one page whose agent changes under it, so switching agents
/// used to clear the box. Kept here, outside the page, a half-written message
/// waits for you when you come back, including after a visit to another page.
/// Held in memory only: it is a draft, not something worth writing to disk.
/// </remarks>
public sealed class ChatDrafts
{
    private readonly ConcurrentDictionary<string, string> _drafts = new(StringComparer.Ordinal);

    public string Get(string sessionId) => _drafts.TryGetValue(sessionId, out var text) ? text : "";

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
