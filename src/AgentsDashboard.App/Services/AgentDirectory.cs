using AgentsDashboard.Core.Claude;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Presentation;

namespace AgentsDashboard.App.Services;

public enum ChatState { Waiting, Active, Idle, Parked, Failed }

/// <summary>Someone you can talk to: a live session, or a background agent that is parked.</summary>
public sealed record ChatTarget(
    string SessionId,
    string Label,
    ChatState State,
    string? Cwd,
    string? WorktreePath,
    string? RepoName,
    string? WorktreeName,
    string? Branch,
    string Where,
    string? JobId,
    bool IsBackground,
    string? WaitingFor,
    string? LastReply,
    DateTimeOffset Since);

/// <summary>
/// Every agent the sidebar lists and the agent view opens, live or parked.
/// </summary>
/// <remarks>
/// Live sessions come from the monitor's snapshot. Parked background agents have
/// no registry entry, so they come from the CLI's own list, which is a process
/// spawn: it is fetched at most every fifteen seconds and shared by every view,
/// rather than asked for by the sidebar and the agent view separately.
/// </remarks>
public sealed class AgentDirectory(ClaudeCli cli)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private IReadOnlyList<BackgroundAgent> _known = [];
    private DateTimeOffset _knownAt = DateTimeOffset.MinValue;
    private Task? _loading;

    /// <summary>Raised when the parked list has been read again.</summary>
    public event Action? Changed;

    /// <summary>Reads the CLI's list again when the last read is older than <see cref="MaxAge"/>.</summary>
    public void RefreshIfStale()
    {
        if (DateTimeOffset.Now - _knownAt > MaxAge)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Reads the CLI's list now, joining a read already under way.</summary>
    public Task RefreshAsync()
    {
        lock (_gate)
        {
            return _loading ??= LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var known = await cli.ListAsync(includeStopped: true).ConfigureAwait(false);
            lock (_gate)
            {
                _known = known;
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException)
        {
            // Keep the last list. Parked agents are the extra, not the point.
        }
        finally
        {
            lock (_gate)
            {
                _knownAt = DateTimeOffset.Now;
                _loading = null;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Every agent you can talk to, the ones that need you first.</summary>
    public IReadOnlyList<ChatTarget> Targets(DashboardSnapshot snapshot)
    {
        IReadOnlyList<BackgroundAgent> known;
        lock (_gate)
        {
            known = _known;
        }

        var now = snapshot.TakenAt;
        var list = new List<ChatTarget>();
        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (var repo in snapshot.Repos)
        {
            foreach (var worktree in repo.Worktrees)
            {
                foreach (var agent in worktree.Agents)
                {
                    live.Add(agent.SessionId);
                    list.Add(new ChatTarget(
                        agent.SessionId,
                        agent.Label,
                        agent.Status switch
                        {
                            AgentStatus.Waiting => ChatState.Waiting,
                            AgentStatus.Active => ChatState.Active,
                            _ => ChatState.Idle,
                        },
                        agent.Cwd,
                        worktree.Worktree.Path,
                        repo.Name,
                        worktree.Worktree.Name,
                        worktree.Worktree.Branch,
                        Where(repo, worktree),
                        agent.JobId,
                        agent.IsBackground,
                        agent.WaitingFor,
                        agent.LastReply,
                        agent.StatusSince));
                }
            }
        }

        foreach (var parked in known.Where(k => !live.Contains(k.SessionId)))
        {
            var home = WorktreeFor(snapshot, parked.Cwd);
            list.Add(new ChatTarget(
                parked.SessionId,
                parked.Name ?? "Agent " + parked.Id,
                parked.IsFailed ? ChatState.Failed : ChatState.Parked,
                parked.Cwd,
                home?.Worktree.Worktree.Path,
                home?.Repo.Name,
                home?.Worktree.Worktree.Name,
                home?.Worktree.Worktree.Branch,
                home is { } h ? Where(h.Repo, h.Worktree) : Fmt.Leaf(parked.Cwd),
                parked.Id,
                true,
                null,
                null,
                parked.StartedAt ?? now));
        }

        return list
            .OrderBy(t => t.State)
            // Waiting: blocked longest first, since that one costs the most.
            // Everyone else: most recently active first.
            .ThenBy(t => t.State == ChatState.Waiting ? -(now - t.Since).Ticks : (now - t.Since).Ticks)
            .ToList();
    }

    private static (RepoView Repo, WorktreeView Worktree)? WorktreeFor(DashboardSnapshot snapshot, string cwd)
    {
        (RepoView, WorktreeView)? best = null;
        var bestLength = -1;
        foreach (var repo in snapshot.Repos)
        {
            foreach (var worktree in repo.Worktrees)
            {
                var path = worktree.Worktree.Path;
                if ((cwd == path || cwd.StartsWith(path + "/", StringComparison.Ordinal)) && path.Length > bestLength)
                {
                    best = (repo, worktree);
                    bestLength = path.Length;
                }
            }
        }

        return best;
    }

    /// <summary>The primary worktree is named after its repository, so naming both would stutter.</summary>
    private static string Where(RepoView repo, WorktreeView worktree) =>
        string.Equals(repo.Name, worktree.Worktree.Name, StringComparison.Ordinal)
            ? repo.Name
            : $"{repo.Name} / {worktree.Worktree.Name}";

    public static string DotClass(ChatState state) => state switch
    {
        ChatState.Waiting => "waiting",
        ChatState.Active => "active",
        ChatState.Failed => "danger",
        _ => "",
    };

    public static string StateWord(ChatTarget target) => target.State switch
    {
        ChatState.Waiting => "Waiting on you",
        ChatState.Active => "Working",
        ChatState.Parked => "Parked",
        ChatState.Failed => "Failed",
        _ => "Idle",
    } + (target.IsBackground ? "" : ", in its own terminal");
}
