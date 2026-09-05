using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Monitoring;

/// <summary>
/// Decides which blocked agents deserve an interruption.
/// </summary>
/// <remarks>
/// <para>
/// The rules are deliberately conservative, because a notification is the one
/// signal that reaches you when the dashboard is behind another window, and so
/// the one that becomes noise fastest. One per entry into waiting, never a repeat
/// while the agent stays there, and nothing at all for the agents that were
/// already waiting when the app opened.
/// </para>
/// <para>
/// That last rule is the important one: opening the dashboard onto three blocked
/// agents should show you three blocked agents, not fire three notifications
/// about a state you are already looking at.
/// </para>
/// </remarks>
public sealed class WaitingWatch
{
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private bool _seeded;

    /// <summary>
    /// The agents to notify about, given who is waiting now. The first call
    /// returns nothing and records the current set instead.
    /// </summary>
    public IReadOnlyList<WaitingAgent> Take(IReadOnlyList<WaitingAgent> waiting)
    {
        var current = waiting.Select(w => w.Session.SessionId).ToHashSet(StringComparer.Ordinal);

        // Agents that have stopped waiting can be announced again next time they
        // block, which is a different question from the same one.
        _announced.IntersectWith(current);

        if (!_seeded)
        {
            _seeded = true;
            _announced.UnionWith(current);
            return [];
        }

        var fresh = waiting.Where(w => _announced.Add(w.Session.SessionId)).ToList();
        return fresh;
    }
}
