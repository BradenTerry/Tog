using AgentsDashboard.Extensions;

namespace AgentsDashboard.App.Services;

/// <summary>The three panels around the editor.</summary>
public enum PanelSide
{
    Left,
    Right,
    Bottom,
}

/// <summary>
/// The window's layout and what is open in it: which agent the panels follow,
/// which panels are showing and on which tab, and the editor's open documents.
/// </summary>
/// <remarks>
/// Scoped, so one per window. Every panel is its own component, and they talk to
/// each other through this rather than through parameters: the Files tree opens
/// a document in the editor, Source control opens the diff at a file, and the
/// title bar folds the panels away. Documents are kept per worktree, so switching
/// agents swaps the editor's tabs and switching back finds them as they were.
/// Memory only, like <see cref="WorktreeViews"/>: where you were looking, not a
/// preference. The panels' layout is the exception, kept in the browser by the
/// layout component, because it is how you like the window.
/// </remarks>
public sealed class Workbench(IServiceProvider services) : IDisposable
{
    public const string FilesTab = "files";
    public const string SourceControlTab = "scm";
    public const string ChatTab = "chat";

    private readonly Dictionary<string, EditorGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChangesModel> _changes = new(StringComparer.Ordinal);

    /// <summary>Anything the panels draw moved: the agent, a document, a tab.</summary>
    public event Action? Changed;

    /// <summary>A panel was shown, hidden or switched tab, which is worth remembering.</summary>
    public event Action? LayoutChanged;

    /// <summary>The agent every panel follows. Kept while a page that is not an agent's is open.</summary>
    public string? SessionId { get; private set; }

    public PanelState Left { get; } = new(FilesTab);

    public PanelState Right { get; } = new(SourceControlTab);

    public PanelState Bottom { get; } = new(ChatTab);

    public PanelState Panel(PanelSide side) => side switch
    {
        PanelSide.Left => Left,
        PanelSide.Right => Right,
        _ => Bottom,
    };

    /// <summary>
    /// Where an extension's view goes. <see cref="ViewLocation.AgentTab"/> was the
    /// only place there was in API 1.0, and the agent view's tab strip it named is
    /// gone, so a view built against 1.0 lands in the default place.
    /// </summary>
    public static PanelSide SideOf(ViewLocation location) => location switch
    {
        ViewLocation.LeftPanel => PanelSide.Left,
        ViewLocation.BottomPanel => PanelSide.Bottom,
        _ => PanelSide.Right,
    };

    public void Select(string? sessionId)
    {
        if (sessionId == SessionId)
        {
            return;
        }

        SessionId = sessionId;
        Raise();
    }

    public void Toggle(PanelSide side)
    {
        var panel = Panel(side);
        panel.Open = !panel.Open;
        RaiseLayout();
    }

    /// <summary>Brings a panel up on one of its tabs.</summary>
    public void Show(PanelSide side, string tab)
    {
        var panel = Panel(side);
        if (panel.Open && panel.Tab == tab)
        {
            return;
        }

        panel.Open = true;
        panel.Tab = tab;
        RaiseLayout();
    }

    /// <summary>Puts back a layout read from the browser, without writing it straight back.</summary>
    public void Restore(PanelSide side, bool open, string? tab)
    {
        var panel = Panel(side);
        panel.Open = open;
        if (tab is { Length: > 0 })
        {
            panel.Tab = tab;
        }

        Raise();
    }

    public EditorGroup Editors(string worktreePath)
    {
        if (!_groups.TryGetValue(worktreePath, out var group))
        {
            group = new EditorGroup();
            _groups[worktreePath] = group;
        }

        return group;
    }

    /// <summary>
    /// A worktree's diff, staging and review draft, shared by Source control and
    /// the Changes document so the two never disagree.
    /// </summary>
    public ChangesModel Changes(string worktreePath)
    {
        if (!_changes.TryGetValue(worktreePath, out var model))
        {
            model = ActivatorUtilities.CreateInstance<ChangesModel>(services, worktreePath);
            _changes[worktreePath] = model;
        }

        return model;
    }

    /// <summary>
    /// Opens a file in the editor. A single click opens it as the preview tab,
    /// which the next single click replaces, as VS Code does, so browsing the
    /// tree does not leave a tab behind for every file looked at. Keeping it,
    /// a double click, an edit or a link, makes the tab stay.
    /// </summary>
    public void OpenFile(string worktreePath, string path, int? line = null, bool keep = false)
    {
        var group = Editors(worktreePath);
        var key = EditorDoc.FileKey(path);
        var doc = group.Find(key);

        if (doc is null)
        {
            doc = new EditorDoc(key, DocKind.File, path) { Preview = !keep };
            int? preview = keep ? null : group.Docs.FindIndex(d => d.Preview);
            if (preview is >= 0)
            {
                group.Docs[preview.Value] = doc;
            }
            else
            {
                group.Docs.Insert(group.InsertAt(), doc);
            }
        }
        else if (keep)
        {
            doc.Preview = false;
        }

        if (line is not null)
        {
            doc.Line = line;
        }

        group.ActiveKey = key;
        Raise();
    }

    /// <summary>Opens the worktree's whole diff, scrolled to a file when one is given.</summary>
    public void OpenChanges(string worktreePath, string? file = null)
    {
        var group = Editors(worktreePath);
        if (group.Find(EditorDoc.ChangesKey) is null)
        {
            group.Docs.Insert(group.InsertAt(), new EditorDoc(EditorDoc.ChangesKey, DocKind.Changes, null));
        }

        group.ActiveKey = EditorDoc.ChangesKey;
        if (file is not null)
        {
            Changes(worktreePath).RequestScroll(file);
        }

        Raise();
    }

    public void Activate(string worktreePath, string key)
    {
        var group = Editors(worktreePath);
        if (group.ActiveKey == key || group.Find(key) is null)
        {
            return;
        }

        group.ActiveKey = key;
        Raise();
    }

    /// <summary>Closes a tab. Asking about unsaved work is the editor area's job, before this.</summary>
    public void Close(string worktreePath, string key)
    {
        var group = Editors(worktreePath);
        var order = group.InTabOrder.ToList();
        var at = order.FindIndex(d => d.Key == key);
        if (at < 0)
        {
            return;
        }

        group.Docs.Remove(order[at]);
        order.RemoveAt(at);
        if (group.ActiveKey == key)
        {
            // The neighbour in the strip, the way closing a tab does everywhere.
            group.ActiveKey = order.Count == 0 ? null : order[Math.Min(at, order.Count - 1)].Key;
        }

        Raise();
    }

    public void TogglePin(string worktreePath, string key)
    {
        if (Editors(worktreePath).Find(key) is not { } doc)
        {
            return;
        }

        doc.Pinned = !doc.Pinned;
        doc.Preview = false;
        Raise();
    }

    public void Keep(string worktreePath, string key)
    {
        if (Editors(worktreePath).Find(key) is { Preview: true } doc)
        {
            doc.Preview = false;
            Raise();
        }
    }

    /// <summary>An edit in a preview tab keeps it: replacing it would throw the edit away.</summary>
    public void SetDirty(string worktreePath, string key, bool dirty)
    {
        if (Editors(worktreePath).Find(key) is not { } doc || doc.Dirty == dirty)
        {
            return;
        }

        doc.Dirty = dirty;
        if (dirty)
        {
            doc.Preview = false;
        }

        Raise();
    }

    private void Raise() => Changed?.Invoke();

    private void RaiseLayout()
    {
        LayoutChanged?.Invoke();
        Raise();
    }

    public void Dispose()
    {
        foreach (var model in _changes.Values)
        {
            model.Dispose();
        }
    }
}

/// <summary>One panel: whether it is showing, and the tab in front.</summary>
public sealed class PanelState(string tab)
{
    public bool Open { get; set; } = true;

    public string Tab { get; set; } = tab;
}

public enum DocKind
{
    File,
    Changes,
}

/// <summary>A worktree's open documents, in the order they were opened.</summary>
public sealed class EditorGroup
{
    public List<EditorDoc> Docs { get; } = [];

    public string? ActiveKey { get; set; }

    public EditorDoc? Active => ActiveKey is null ? null : Find(ActiveKey);

    public EditorDoc? Find(string key) => Docs.FirstOrDefault(d => d.Key == key);

    /// <summary>Pinned tabs first, then the rest, each in the order opened.</summary>
    public IEnumerable<EditorDoc> InTabOrder => Docs.Where(d => d.Pinned).Concat(Docs.Where(d => !d.Pinned));

    /// <summary>A new tab goes after the one in front, where you are looking.</summary>
    internal int InsertAt()
    {
        var active = ActiveKey is null ? -1 : Docs.FindIndex(d => d.Key == ActiveKey);
        return active < 0 ? Docs.Count : active + 1;
    }
}

/// <summary>A tab in the editor.</summary>
public sealed class EditorDoc(string key, DocKind kind, string? path)
{
    public const string ChangesKey = "changes";

    public static string FileKey(string path) => "file:" + path;

    public string Key { get; } = key;

    public DocKind Kind { get; } = kind;

    /// <summary>The worktree-relative path of a file document.</summary>
    public string? Path { get; } = path;

    public bool Pinned { get; set; }

    /// <summary>Opened by a single click, and replaced by the next one until kept.</summary>
    public bool Preview { get; set; }

    public bool Dirty { get; set; }

    /// <summary>The line a link asked for, revealed when it changes.</summary>
    public int? Line { get; set; }

    public string Title => Kind == DocKind.Changes ? "Changes" : Path![(Path!.LastIndexOf('/') + 1)..];
}
