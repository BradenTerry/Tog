using AgentsDashboard.Core.Agents;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Presentation;

namespace AgentsDashboard.App.Services;

public enum ChatState { Waiting, Active, Idle, Parked, Failed }

/// <summary>An agent in the sidebar, with where it works and what it is doing.</summary>
/// <param name="LiveText">What it has said so far in the turn running now, before the transcript has it.</param>
/// <param name="CurrentTool">The tool call it is making, by title.</param>
/// <param name="Permission">A permission it is waiting on you for.</param>
/// <param name="LabelIsPrompt">The label is its first prompt, standing in until the agent names the conversation.</param>
public sealed record ChatTarget(
    string SessionId,
    string Label,
    bool LabelIsPrompt,
    ChatState State,
    string Cwd,
    string? WorktreePath,
    string? RepoName,
    string? WorktreeName,
    string? Branch,
    string Where,
    string? WaitingFor,
    string? LastReply,
    DateTimeOffset Since,
    string? Error,
    string LiveText,
    string? CurrentTool,
    PermissionAsk? Permission,
    IReadOnlyList<AcpConfigOption> Options);

/// <summary>
/// The agents the sidebar lists and the agent view opens: the ones the
/// dashboard hosts, running or stopped.
/// </summary>
/// <remarks>
/// What an agent is doing comes from the host, which hears it over ACP as it
/// happens. Where it works comes from the monitor's snapshot, which knows the
/// worktrees, and so does the summary of its last reply, which the monitor reads
/// from the transcript.
/// </remarks>
public sealed class AgentDirectory(AgentHost host)
{
    /// <summary>Raised when any agent changes.</summary>
    public event Action? Changed
    {
        add => host.Changed += value;
        remove => host.Changed -= value;
    }

    public AgentHost Host => host;

    /// <summary>Every agent, the ones that need you first.</summary>
    public IReadOnlyList<ChatTarget> Targets(DashboardSnapshot snapshot)
    {
        var now = snapshot.TakenAt;
        var sessions = snapshot.Repos
            .SelectMany(r => r.Worktrees.SelectMany(w => w.Agents))
            .GroupBy(a => a.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return host.Agents
            .Select(agent =>
            {
                var home = WorktreeFor(snapshot, agent.Cwd);
                sessions.TryGetValue(agent.SessionId, out var seen);
                var (label, isPrompt) = LabelFor(agent, seen);
                return new ChatTarget(
                    agent.SessionId,
                    label,
                    isPrompt,
                    agent.State switch
                    {
                        HostedState.Waiting => ChatState.Waiting,
                        HostedState.Working => ChatState.Active,
                        HostedState.Idle => ChatState.Idle,
                        HostedState.Failed => ChatState.Failed,
                        _ => ChatState.Parked,
                    },
                    agent.Cwd,
                    home?.Worktree.Worktree.Path,
                    home?.Repo.Name,
                    home?.Worktree.Worktree.Name,
                    home?.Worktree.Worktree.Branch,
                    home is { } h ? Where(h.Repo, h.Worktree) : Fmt.Leaf(agent.Cwd),
                    agent.Permission?.Title,
                    seen?.LastReply,
                    agent.StateSince,
                    agent.Error,
                    agent.LiveText,
                    agent.CurrentTool,
                    agent.Permission,
                    agent.Options);
            })
            .OrderBy(t => t.State)
            // Waiting: blocked longest first, since that one costs the most.
            // Everyone else: most recently active first.
            .ThenBy(t => t.State == ChatState.Waiting ? -(now - t.Since).Ticks : (now - t.Since).Ticks)
            .ToList();
    }

    /// <summary>
    /// What an agent is called: the title it gave the conversation, or failing
    /// that the one in its transcript, and only then its first prompt, marked as
    /// such. The bridge names a conversation when its first turn ends, so a long
    /// first turn runs under its prompt for a while.
    /// </summary>
    private static (string Label, bool IsPrompt) LabelFor(HostedAgent agent, AgentSession? seen)
    {
        if (agent.Title is { Length: > 0 } title)
        {
            return (title, false);
        }

        if (seen?.Summary is { Length: > 0 } summary)
        {
            return (summary, false);
        }

        if (agent.Prompt is { Length: > 0 } prompt)
        {
            return (AgentSession.Shorten(prompt, 60), true);
        }

        return seen is not null
            ? (seen.Label, seen.LabelIsPrompt)
            : ("Agent " + agent.SessionId[..Math.Min(8, agent.SessionId.Length)], false);
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
        // Not the tool it is in: that changes every second or two, and a status
        // line that keeps rewriting itself to "grep -rn ..." reads as noise. The
        // tool is in the transcript and in the tooltip for whoever wants it.
        ChatState.Active => "Working",
        ChatState.Parked => "Stopped",
        ChatState.Failed => "Failed",
        _ => "Idle",
    };
}
