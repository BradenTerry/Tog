using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Monitoring;
using AgentsDashboard.Core.Presentation;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.App.Services;
using AgentsDashboard.Extensions;

namespace AgentsDashboard.App.Extensions;

/// <summary>
/// The app's own types, turned into the API's. The API is versioned and the
/// app's model is not, so nothing from the app crosses except through here.
/// </summary>
public static class ApiModel
{
    public static WorktreeContext Worktree(WorktreeView view, string repoName) => new(
        view.Worktree.Path,
        view.Worktree.Name,
        view.Worktree.Branch,
        view.Worktree.IsPrimary,
        view.RepoRoot,
        repoName,
        view.Status is { } s ? new GitSummary(s.Changed, s.Staged, s.Untracked, s.Ahead, s.Behind) : null);

    public static DashboardView View(DashboardSnapshot snapshot, IReadOnlyList<ChatTarget> agents)
    {
        var worktrees = snapshot.Repos.SelectMany(r => r.Worktrees.Select(w => Worktree(w, r.Name))).ToList();
        var byPath = worktrees.ToDictionary(w => w.Path, StringComparer.Ordinal);

        return new DashboardView(worktrees, snapshot.TakenAt)
        {
            Agents = agents
                .Select(a => Agent(a, a.WorktreePath is { } path ? byPath.GetValueOrDefault(path) : null))
                .ToList(),
        };
    }

    /// <summary>A worktree in the latest snapshot, or null when it is not in it.</summary>
    public static WorktreeContext? Worktree(string path, DashboardState state, string? repoName = null) =>
        state.Worktree(path) is { } view
            ? Worktree(view, state.Repo(view.RepoRoot)?.Name ?? repoName ?? "")
            : null;

    public static AgentContext Agent(ChatTarget agent, DashboardState state)
    {
        var worktree = agent.WorktreePath is { } path ? Worktree(path, state, agent.RepoName) : null;

        return Agent(agent, worktree);
    }

    private static AgentContext Agent(ChatTarget agent, WorktreeContext? worktree) =>
        new(agent.SessionId, agent.Label, "claude", State(agent.State), worktree) { TurnEndedAt = agent.TurnEndedAt };

    private static AgentState State(ChatState state) => state switch
    {
        ChatState.Active => AgentState.Active,
        ChatState.Waiting => AgentState.Waiting,
        ChatState.Parked => AgentState.Parked,
        ChatState.Failed => AgentState.Failed,
        _ => AgentState.Idle,
    };
}

/// <summary><see cref="IDashboardView"/> over the monitor's published state.</summary>
public sealed class DashboardViewAdapter : IDashboardView, IDisposable
{
    private readonly DashboardState _state;
    private readonly AgentDirectory _agents;
    private DashboardSnapshot? _for;
    private DashboardView _view = new([], DateTimeOffset.MinValue);

    public DashboardViewAdapter(DashboardState state, AgentDirectory agents)
    {
        _state = state;
        _agents = agents;
        _state.Changed += Raise;
    }

    public DashboardView Current
    {
        get
        {
            // Converted once per snapshot, not once per reader. The agents are
            // taken with it, so they are up to a tick old like the rest.
            var snapshot = _state.Snapshot;
            if (!ReferenceEquals(snapshot, _for))
            {
                _view = ApiModel.View(snapshot, _agents.Targets(snapshot));
                _for = snapshot;
            }

            return _view;
        }
    }

    public event Action? Changed;

    private void Raise() => Changed?.Invoke();

    public void Dispose() => _state.Changed -= Raise;
}

/// <summary>One window's editor, for an extension's view.</summary>
public sealed class EditorTabs(Workbench bench, DashboardState state) : IEditorTabs
{
    public void OpenFile(string absolutePath, int? line = null) =>
        bench.OpenExternal(bench.WorktreeInView(state.Snapshot) ?? "", absolutePath, line);
}

/// <summary>
/// One window's New agent dialog, for an extension's view. Only fills it in: the
/// user presses Start, so an extension never starts an agent itself.
/// </summary>
public sealed class AgentOffers(Workbench bench) : IAgentOffers
{
    public void Offer(AgentOffer offer) => _ = OfferAsync(offer);

    public Task<string?> OfferAsync(AgentOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        // A relative folder would resolve against the app's working directory,
        // which means nothing to the extension; drop it rather than guess.
        return bench.OfferNewAgent(new NewAgentPreset(
            Folder(offer.Repository),
            Folder(offer.Worktree),
            Blank(offer.WorktreeName),
            Blank(offer.Prompt)));
    }

    private static string? Folder(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}

public sealed class Navigation : INavigation
{
    public string FileHref(string agentId, string relativePath, int? line = null) =>
        Urls.AgentFile(agentId, relativePath, line);
}

/// <summary>The same linking the app's own <c>FileLinks</c> does, for extensions.</summary>
public sealed class TextLinker : ITextLinker
{
    public IReadOnlyList<LinkedRun> Link(string text, string worktreePath, string agentId)
    {
        var references = FileReferences.Find(text, worktreePath, p => File.Exists(WorktreeFiles.Resolve(worktreePath, p)));

        return FileReferences.Segments(text, references)
            .Select(s => s.Reference is { } r
                ? new LinkedRun(
                    s.Text,
                    Urls.AgentFile(agentId, r.Path, r.Line),
                    WorktreeFiles.Resolve(worktreePath, r.Path) is { } absolute ? EditorLinks.VsCode(absolute, r.Line) : null)
                {
                    Path = WorktreeFiles.Resolve(worktreePath, r.Path),
                    Line = r.Line,
                }
                : new LinkedRun(s.Text))
            .ToList();
    }
}

internal sealed class ExtensionStorage(AppPaths paths, string id) : IExtensionStorage
{
    public string DataDirectory
    {
        get
        {
            var dir = paths.ExtensionDataDir(id);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
