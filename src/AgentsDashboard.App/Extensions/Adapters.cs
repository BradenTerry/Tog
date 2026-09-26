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

    public static DashboardView View(DashboardSnapshot snapshot) => new(
        snapshot.Repos.SelectMany(r => r.Worktrees.Select(w => Worktree(w, r.Name))).ToList(),
        snapshot.TakenAt);

    public static AgentContext Agent(ChatTarget agent, DashboardState state)
    {
        var worktree = agent.WorktreePath is { } path && state.Worktree(path) is { } view
            ? Worktree(view, state.Repo(view.RepoRoot)?.Name ?? agent.RepoName ?? "")
            : null;

        return new AgentContext(agent.SessionId, agent.Label, "claude", State(agent.State), worktree);
    }

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
    private DashboardSnapshot? _for;
    private DashboardView _view = new([], DateTimeOffset.MinValue);

    public DashboardViewAdapter(DashboardState state)
    {
        _state = state;
        _state.Changed += Raise;
    }

    public DashboardView Current
    {
        get
        {
            // Converted once per snapshot, not once per reader.
            var snapshot = _state.Snapshot;
            if (!ReferenceEquals(snapshot, _for))
            {
                _view = ApiModel.View(snapshot);
                _for = snapshot;
            }

            return _view;
        }
    }

    public event Action? Changed;

    private void Raise() => Changed?.Invoke();

    public void Dispose() => _state.Changed -= Raise;
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
