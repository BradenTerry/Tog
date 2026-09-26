using AgentsDashboard.Core.Model;

namespace AgentsDashboard.App.Services;

/// <summary>
/// What each worktree's views remember while the app runs, so leaving an agent
/// and coming back lands where you were.
/// </summary>
/// <remarks>
/// The panels are rebuilt every time you switch agents, and the app serves more
/// than one window. Anything worth keeping across that therefore lives here,
/// keyed by worktree, rather than in the component. Memory only: it is where you
/// were looking, not a preference, and a restart starting fresh is expected.
/// </remarks>
public sealed class WorktreeViews
{
    private readonly Dictionary<string, RememberedView> _views = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Raised with the worktree's path when its diff base changes.</summary>
    public event Action<string>? BaseChanged;

    public RememberedView For(string worktreePath)
    {
        lock (_gate)
        {
            if (!_views.TryGetValue(worktreePath, out var view))
            {
                view = new RememberedView();
                _views[worktreePath] = view;
            }

            return view;
        }
    }

    /// <summary>
    /// Records the base Source control compared against. The editor marks its
    /// lines against the same one, so the two never disagree about what changed.
    /// </summary>
    public void SetBase(string worktreePath, DiffBase diffBase, string customRef)
    {
        var view = For(worktreePath);
        if (view.Base == diffBase && string.Equals(view.CustomRef, customRef, StringComparison.Ordinal))
        {
            return;
        }

        view.Base = diffBase;
        view.CustomRef = customRef;
        BaseChanged?.Invoke(worktreePath);
    }
}

/// <summary>One worktree's remembered view.</summary>
public sealed class RememberedView
{
    public DiffBase Base { get; set; } = DiffBase.WorkingTree;

    public string CustomRef { get; set; } = "";

    /// <summary>
    /// Folded directories in the Files tree. Null until the tree is first drawn,
    /// which is when every directory starts folded.
    /// </summary>
    public HashSet<string>? Collapsed { get; set; }

    /// <summary>Whether Markdown files show rendered rather than in the editor.</summary>
    public bool MarkdownPreview { get; set; }
}
