using Tog.Core.Git;
using Tog.Core.Model;
using Tog.Core.Monitoring;
using Tog.Core.Presentation;
using Tog.Core.Repos;
using Tog.App.Services;
using Tog.Extensions;
using Microsoft.AspNetCore.Components;

namespace Tog.App.Extensions;

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

    public static TogView View(TogSnapshot snapshot, IReadOnlyList<ChatTarget> agents)
    {
        var worktrees = snapshot.Repos.SelectMany(r => r.Worktrees.Select(w => Worktree(w, r.Name))).ToList();
        var byPath = worktrees.ToDictionary(w => w.Path, StringComparer.Ordinal);

        return new TogView(worktrees, snapshot.TakenAt)
        {
            Agents = agents
                .Select(a => Agent(a, a.WorktreePath is { } path ? byPath.GetValueOrDefault(path) : null))
                .ToList(),
        };
    }

    /// <summary>A worktree in the latest snapshot, or null when it is not in it.</summary>
    public static WorktreeContext? Worktree(string path, TogState state, string? repoName = null) =>
        state.Worktree(path) is { } view
            ? Worktree(view, state.Repo(view.RepoRoot)?.Name ?? repoName ?? "")
            : null;

    public static AgentContext Agent(ChatTarget agent, TogState state)
    {
        var worktree = agent.WorktreePath is { } path ? Worktree(path, state, agent.RepoName) : null;

        return Agent(agent, worktree);
    }

    private static AgentContext Agent(ChatTarget agent, WorktreeContext? worktree) =>
        new(agent.SessionId, agent.Label, agent.Backend, State(agent.State), worktree) { TurnEndedAt = agent.TurnEndedAt };

    private static AgentState State(ChatState state) => state switch
    {
        ChatState.Active => AgentState.Active,
        ChatState.Waiting => AgentState.Waiting,
        ChatState.Parked => AgentState.Parked,
        ChatState.Failed => AgentState.Failed,
        _ => AgentState.Idle,
    };
}

/// <summary><see cref="ITogView"/> over the monitor's published state.</summary>
public sealed class TogViewAdapter : ITogView, IDisposable
{
    private readonly TogState _state;
    private readonly AgentDirectory _agents;
    private TogSnapshot? _for;
    private TogView _view = new([], DateTimeOffset.MinValue);

    public TogViewAdapter(TogState state, AgentDirectory agents)
    {
        _state = state;
        _agents = agents;
        _state.Changed += Raise;
    }

    public TogView Current
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
public sealed class EditorTabs(Workbench bench, TogState state, ExtensionHost host) : IEditorTabs
{
    public void OpenFile(string absolutePath, int? line = null) =>
        bench.OpenExternal(bench.WorktreeInView(state.Snapshot) ?? "", absolutePath, line);

    /// <remarks>
    /// Which extension the tab belongs to comes from the component's load
    /// context, as a dialog's does, so one extension cannot open a tab under
    /// another's name or keep a tab alive on a copy that has been unloaded.
    /// </remarks>
    public void OpenView<TComponent>(string title, IReadOnlyDictionary<string, object?>? parameters = null, string? id = null)
        where TComponent : IComponent
    {
        var (context, _) = host.ContextFor(typeof(TComponent))
            ?? throw new InvalidOperationException($"{typeof(TComponent).Name} is not a component of a loaded extension.");

        bench.OpenView(
            bench.WorktreeInView(state.Snapshot) ?? "",
            new ExtensionTab(
                context.Info.Id,
                typeof(TComponent).FullName!,
                string.IsNullOrEmpty(id) ? null : id,
                string.IsNullOrWhiteSpace(title) ? typeof(TComponent).Name : title,
                new Dictionary<string, object?>(parameters ?? new Dictionary<string, object?>())));
    }
}

/// <summary>
/// One window's chat, for an extension's view. Only fills in the message box:
/// the user presses Enter, so an extension never sends an agent anything itself.
/// </summary>
public sealed class AgentMessages(AgentDirectory directory, TogState state, ChatDrafts drafts, Workbench bench, NavigationManager nav) : IAgentMessages
{
    public bool Offer(string agentId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!directory.Targets(state.Snapshot).Any(t => t.SessionId == agentId))
        {
            return false;
        }

        // After what is already in the box, so half a message the user was
        // writing is not lost to an extension's button.
        var draft = drafts.Get(agentId).TrimEnd();
        drafts.Offer(agentId, draft.Length == 0 ? text.Trim() : draft + "\n\n" + text.Trim());

        if (bench.SessionId != agentId)
        {
            nav.NavigateTo(Urls.Chat(agentId));
        }

        bench.FocusChat();
        return true;
    }
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
            Blank(offer.Prompt),
            Blank(offer.Branch)));
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

    public string AgentHref(string agentId) => Urls.Chat(agentId);
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

/// <summary>Draws an extension's <see cref="DiffView"/> with the editor's own diff.</summary>
public sealed class DiffViewHost : IDiffViewHost
{
    public Type Component => typeof(Components.Shared.DiffViewEditor);
}
