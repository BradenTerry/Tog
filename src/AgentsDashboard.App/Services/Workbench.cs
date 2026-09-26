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

    /// <summary>
    /// Files changed on disk in the worktree in view, with their worktree-relative
    /// paths; <c>.git</c> stands for the index or HEAD. Raised on the circuit.
    /// </summary>
    public event Action<string, IReadOnlySet<string>>? FilesChanged;

    /// <summary>
    /// Passes on a change the layout's watcher saw, and reads the worktree's diff
    /// again if anything has shown it, so Source control and the open diffs follow
    /// the agent's edits without a refresh.
    /// </summary>
    public void NotifyFilesChanged(string worktreePath, IReadOnlySet<string> paths)
    {
        if (_changes.TryGetValue(worktreePath, out var model) && (model.Diff is not null || model.Loading))
        {
            _ = model.Load();
        }

        FilesChanged?.Invoke(worktreePath, paths);
    }

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

    /// <summary>Whether the New agent dialog is up over the window.</summary>
    public bool NewAgentOpen { get; private set; }

    /// <summary>
    /// Where the New agent dialog should start, when whatever opened it knew: the
    /// Worktrees view's "New agent" names a repository and a worktree. Read by
    /// the form as it is built, and cleared when the dialog opens without one.
    /// </summary>
    public (string RepoRoot, string WorktreePath)? NewAgentIn { get; private set; }

    public void OpenNewAgent()
    {
        NewAgentIn = null;
        if (!NewAgentOpen)
        {
            NewAgentOpen = true;
            Raise();
        }
    }

    /// <summary>Opens the New agent dialog with a repository and one of its worktrees already picked.</summary>
    public void OpenNewAgent(string repoRoot, string worktreePath)
    {
        NewAgentIn = (repoRoot, worktreePath);
        NewAgentOpen = true;
        Raise();
    }

    public void CloseNewAgent()
    {
        if (NewAgentOpen)
        {
            NewAgentOpen = false;
            Raise();
        }
    }

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
    public void OpenFile(string worktreePath, string path, int? line = null, bool keep = false) =>
        Open(worktreePath, DocKind.File, path, line, keep);

    /// <summary>
    /// Opens one file's changes, the way clicking a file in VS Code's Source
    /// Control view does: a tab of its own, previewed like a file until kept.
    /// </summary>
    public void OpenDiff(string worktreePath, string path, bool keep = false) =>
        Open(worktreePath, DocKind.Diff, path, null, keep);

    private void Open(string worktreePath, DocKind kind, string path, int? line, bool keep)
    {
        var group = Editors(worktreePath);
        var key = EditorDoc.KeyFor(kind, path);
        var doc = group.Find(key);

        if (doc is null)
        {
            doc = new EditorDoc(key, kind, path) { Preview = !keep };
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

    /// <summary>Opens the worktree's whole diff, every file in one document, scrolled to a file when one is given.</summary>
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

    /// <summary>
    /// Opens Settings as a tab among the agent's documents, so it closes like one
    /// and leaves you on the agent. Without an agent in view the tabs are kept
    /// under the empty worktree, which is where the editor looks then.
    /// </summary>
    public void OpenSettings(string? worktreePath) => OpenPage(worktreePath, EditorDoc.SettingsKey, DocKind.Settings);

    /// <summary>
    /// Opens the Worktrees view, every worktree of every repo, the way Settings
    /// opens. With <paramref name="remove"/> it opens straight onto the dialog
    /// that removes that worktree, which is how the agent list's offer lands.
    /// </summary>
    public void OpenWorktrees(string? worktreePath, string? remove = null)
    {
        RemoveRequest = remove;
        OpenPage(worktreePath, EditorDoc.WorktreesKey, DocKind.Worktrees);
    }

    /// <summary>A worktree whose remove dialog the Worktrees view should open, taken once.</summary>
    public string? RemoveRequest { get; set; }

    private void OpenPage(string? worktreePath, string key, DocKind kind)
    {
        var group = Editors(worktreePath ?? "");
        if (group.Find(key) is null)
        {
            group.Docs.Insert(group.InsertAt(), new EditorDoc(key, kind, null));
        }

        group.ActiveKey = key;
        Raise();
    }

    /// <summary>
    /// Records where a jump left from: go to definition, into another file or
    /// down the same one, or a row picked in the references panel. Going back
    /// returns there. A new jump drops whatever going back had left to go
    /// forward to, as in a browser.
    /// </summary>
    public void RecordJump(string worktreePath, string key, int? line)
    {
        var group = Editors(worktreePath);
        group.Back.Add(new NavPoint(key, line));
        if (group.Back.Count > EditorGroup.HistoryLimit)
        {
            group.Back.RemoveAt(0);
        }

        group.Forward.Clear();
    }

    /// <summary>Back to where the last jump left from. The line is where the caret is now, for coming forward again.</summary>
    public void GoBack(string worktreePath, int? line) => Step(Editors(worktreePath), line, back: true);

    public void GoForward(string worktreePath, int? line) => Step(Editors(worktreePath), line, back: false);

    private void Step(EditorGroup group, int? line, bool back)
    {
        var from = back ? group.Back : group.Forward;
        var to = back ? group.Forward : group.Back;
        if (from.Count == 0)
        {
            return;
        }

        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        if (group.ActiveKey is { } current)
        {
            to.Add(new NavPoint(current, line));
        }

        // A tab closed since the jump comes back, as VS Code reopens it: going
        // back is to the place, not only to the tab.
        if (group.Find(target.Key) is not { } doc)
        {
            var path = target.Key[(target.Key.IndexOf(':') + 1)..];
            doc = target.Key.StartsWith("diff:", StringComparison.Ordinal)
                ? new EditorDoc(target.Key, DocKind.Diff, path)
                : target.Key.StartsWith("file:", StringComparison.Ordinal)
                    ? new EditorDoc(target.Key, DocKind.File, path)
                    : null;

            if (doc is null)
            {
                return;
            }

            group.Docs.Insert(group.InsertAt(), doc);
        }

        group.ActiveKey = doc.Key;
        if (target.Line is not null)
        {
            doc.Line = target.Line;
            doc.Reveal++;
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
    /// <summary>A file, in an editor.</summary>
    File,

    /// <summary>Every change in the worktree, in one document.</summary>
    Changes,

    /// <summary>One file's changes.</summary>
    Diff,

    /// <summary>The app's settings, which belong to no file.</summary>
    Settings,

    /// <summary>Every worktree of every repo, and cleaning them up.</summary>
    Worktrees,
}

/// <summary>A worktree's open documents, in the order they were opened.</summary>
public sealed class EditorGroup
{
    public List<EditorDoc> Docs { get; } = [];

    public string? ActiveKey { get; set; }

    public EditorDoc? Active => ActiveKey is null ? null : Find(ActiveKey);

    public EditorDoc? Find(string key) => Docs.FirstOrDefault(d => d.Key == key);

    /// <summary>How many jumps back are kept. Past this the oldest go, which nobody walks back to.</summary>
    internal const int HistoryLimit = 50;

    /// <summary>Where jumps left from, most recent last.</summary>
    internal List<NavPoint> Back { get; } = [];

    /// <summary>Where going back left from, for going forward again.</summary>
    internal List<NavPoint> Forward { get; } = [];

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
    public const string SettingsKey = "settings";
    public const string WorktreesKey = "worktrees";

    public static string FileKey(string path) => "file:" + path;

    public static string KeyFor(DocKind kind, string path) => kind == DocKind.Diff ? "diff:" + path : FileKey(path);

    public string Key { get; } = key;

    public DocKind Kind { get; } = kind;

    /// <summary>The worktree-relative path of a file or a file's diff.</summary>
    public string? Path { get; } = path;

    public bool Pinned { get; set; }

    /// <summary>Opened by a single click, and replaced by the next one until kept.</summary>
    public bool Preview { get; set; }

    public bool Dirty { get; set; }

    /// <summary>The line a link asked for, revealed when it changes.</summary>
    public int? Line { get; set; }

    /// <summary>Bumped to reveal <see cref="Line"/> again when it has not changed but the caret has moved off it.</summary>
    public int Reveal { get; set; }

    public string Title => Kind switch
    {
        DocKind.Changes => "Changes",
        DocKind.Settings => "Settings",
        DocKind.Worktrees => "Worktrees",
        _ => Path![(Path!.LastIndexOf('/') + 1)..],
    };
}

/// <summary>A place in the editor: a tab, and the line the caret was on when known.</summary>
public sealed record NavPoint(string Key, int? Line);
