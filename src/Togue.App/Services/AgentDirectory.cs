using Togue.Core.Agents;
using Togue.Core.Model;
using Togue.Core.Presentation;
using Togue.Core.Repos;

namespace Togue.App.Services;

public enum ChatState { Waiting, Active, Idle, Parked, Failed }

/// <summary>An agent in the agent list, with where it works and what it is doing.</summary>
/// <param name="LiveText">What it has said so far in the turn running now, before the transcript has it.</param>
/// <param name="CurrentTool">The tool call it is making, by title.</param>
/// <param name="Permission">A permission it is waiting on you for.</param>
/// <param name="Questions">Questions it is waiting on you to answer.</param>
/// <param name="LabelIsPrompt">The label is its first prompt, standing in until the agent names the conversation.</param>
/// <param name="Context">How full its context window was when its last turn ended.</param>
/// <param name="Detached">Its worktree has no branch checked out, only a commit.</param>
/// <param name="Subagents">Subagents running under it right now.</param>
/// <param name="BackgroundCommands">Shell commands it started in the background that are still running.</param>
/// <param name="TurnEndedAt">When its last turn ended.</param>
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
    IReadOnlyList<AcpConfigOption> Options,
    ContextUsage? Context,
    bool Detached = false,
    IReadOnlyList<Subagent>? Subagents = null,
    IReadOnlyList<BackgroundCommand>? BackgroundCommands = null,
    bool FolderGone = false,
    IReadOnlyList<AcpCommand>? Commands = null,
    QuestionForm? Questions = null,
    DateTimeOffset? TurnEndedAt = null)
{
    /// <summary>Subagents and background commands still running.</summary>
    public int BackgroundCount => (Subagents?.Count ?? 0) + (BackgroundCommands?.Count ?? 0);
}

/// <summary>
/// The agents the agent list shows and the panels open: the ones the
/// app hosts, running or stopped.
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
    public IReadOnlyList<ChatTarget> Targets(TogueSnapshot snapshot)
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
                    agent.WaitingFor,
                    seen?.LastReply,
                    agent.StateSince,
                    agent.Error,
                    agent.LiveText,
                    agent.CurrentTool,
                    agent.Permission,
                    agent.Options,
                    agent.Context,
                    home?.Worktree.Worktree.Detached == true,
                    seen?.Subagents ?? [],
                    seen?.BackgroundCommands ?? [],
                    agent.FolderGone,
                    agent.Commands,
                    agent.Questions,
                    agent.TurnEndedAt);
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

    private static (RepoView Repo, WorktreeView Worktree)? WorktreeFor(TogueSnapshot snapshot, string cwd)
    {
        (RepoView, WorktreeView)? best = null;
        var bestLength = -1;
        foreach (var repo in snapshot.Repos)
        {
            foreach (var worktree in repo.Worktrees)
            {
                // Through RealPaths: git names a worktree by its real path, and
                // an agent started under a link (/tmp on macOS) keeps the link.
                var path = worktree.Worktree.Path;
                if (RealPaths.IsUnder(cwd, path) && path.Length > bestLength)
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
        ChatState.Parked or ChatState.Failed when target.FolderGone => "Folder removed",
        ChatState.Waiting => "Waiting on you",
        // Not the tool it is in: that changes every second or two, and a status
        // line that keeps rewriting itself to "grep -rn ..." reads as noise. The
        // tool is in the transcript and in the tooltip for whoever wants it.
        ChatState.Active => "Working",
        ChatState.Idle when target.BackgroundCount > 0 => $"Idle, {target.BackgroundCount} running in background",
        ChatState.Parked => "Stopped",
        ChatState.Failed => "Failed",
        _ => "Idle",
    };
}
