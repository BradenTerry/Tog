namespace Tog.App.Services;

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
}

/// <summary>One worktree's remembered view.</summary>
public sealed class RememberedView
{
    /// <summary>
    /// Folded directories in the Files tree. Null until the tree is first drawn,
    /// which is when every directory starts folded.
    /// </summary>
    public HashSet<string>? Collapsed { get; set; }

    /// <summary>Whether Markdown files show rendered rather than in the editor.</summary>
    public bool MarkdownPreview { get; set; }

    /// <summary>
    /// "Keep" was pressed on the agent list's offer to remove this merged
    /// worktree, so it is not offered again while the app runs.
    /// </summary>
    public bool CleanupDismissed { get; set; }
}
