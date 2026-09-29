namespace Togue.Core.Repos;

/// <summary>
/// For each agent, the end of the last turn you had on screen, so an agent that
/// finished while you were looking at another can be marked unread.
/// </summary>
/// <remarks>
/// A file for the same reason as <see cref="LastViewStore"/>, and one shared by
/// every window, since having read a turn in one window is having read it. Kept
/// in memory after the first read: it is asked about on every render of the
/// agent list, and only written when a turn is newly seen.
/// </remarks>
public sealed class SeenTurnsStore(AppPaths paths)
{
    private readonly Lock _gate = new();
    private Dictionary<string, DateTimeOffset>? _seen;

    private Dictionary<string, DateTimeOffset> Seen => _seen ??= new(
        JsonFile.Read<Dictionary<string, DateTimeOffset>>(paths.SeenTurnsFile, _gate) ?? [],
        StringComparer.Ordinal);

    /// <summary>
    /// Whether a turn that ended at <paramref name="turnEndedAt"/> has not been
    /// on screen. An agent with no turn end has nothing to read.
    /// </summary>
    public bool IsUnread(string sessionId, DateTimeOffset? turnEndedAt)
    {
        if (turnEndedAt is not { } ended)
        {
            return false;
        }

        lock (_gate)
        {
            return !Seen.TryGetValue(sessionId, out var seen) || seen < ended;
        }
    }

    /// <summary>
    /// Records a turn end as seen, and forgets agents no longer in the list.
    /// False when it was already seen, so nothing needs redrawing.
    /// </summary>
    public bool MarkSeen(string sessionId, DateTimeOffset turnEndedAt, IEnumerable<string> known)
    {
        lock (_gate)
        {
            if (Seen.TryGetValue(sessionId, out var seen) && seen >= turnEndedAt)
            {
                return false;
            }

            Seen[sessionId] = turnEndedAt;
            var keep = known.ToHashSet(StringComparer.Ordinal);
            keep.Add(sessionId);
            foreach (var gone in Seen.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                Seen.Remove(gone);
            }

            JsonFile.Write(paths, paths.SeenTurnsFile, Seen, _gate);
            return true;
        }
    }
}
