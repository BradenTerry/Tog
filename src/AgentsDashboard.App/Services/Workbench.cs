using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Repos;
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
/// preference. Two exceptions: the panels' layout, kept in the browser by the
/// layout component because it is how you like the window, and the agent and
/// worktree in view, kept in <see cref="LastViewStore"/> so the app reopens on
/// them.
/// </remarks>
public sealed class Workbench : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly AgentDirectory _directory;
    private readonly LastViewStore _lastViewStore;
    private LastView? _lastView;

    /// <summary>The agent open when the app was last used, until a page has had the chance to reopen it.</summary>
    private string? _reopen;

    public Workbench(IServiceProvider services, AgentDirectory directory, LastViewStore lastViewStore)
    {
        _services = services;
        _directory = directory;
        _lastViewStore = lastViewStore;
        _lastView = lastViewStore.Load();

        if (_lastView is { WorktreePath: { } path, RepoRoot: { } root } saved)
        {
            if (saved.Pinned)
            {
                _pinned = (path, root);
            }
            else
            {
                _lastWorktree = (path, root);
            }
        }

        _reopen = _lastView?.SessionId;
    }

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

    /// <summary>The last worktree an agent in view worked in, and its repository.</summary>
    private (string Path, string RepoRoot)? _lastWorktree;

    /// <summary>A worktree picked in the title bar, which holds whichever agent is picked.</summary>
    private (string Path, string RepoRoot)? _pinned;

    /// <summary>The agent every panel follows, when it is still in the list.</summary>
    public ChatTarget? Agent(DashboardSnapshot snapshot) =>
        SessionId is { } id ? _directory.Targets(snapshot).FirstOrDefault(t => t.SessionId == id) : null;

    /// <summary>Whether the worktree was picked by hand, rather than following the agent.</summary>
    public bool Pinned => _pinned is not null;

    /// <summary>
    /// The worktree the editor, the Files tree and Source control show: one
    /// picked in the title bar, else the agent's, else the last one an agent
    /// was in.
    /// </summary>
    /// <remarks>
    /// The files follow the agent until a worktree is picked, because reading
    /// in one place while answering agents elsewhere is common, and moving the
    /// files under someone mid-thought on every click in the agent list is not
    /// what they want. With nothing picked, closing the last agent, or removing
    /// the one on screen, keeps its worktree rather than emptying the window.
    /// A worktree removed since is stood in for by its repository's main one,
    /// which is where the work usually went.
    /// </remarks>
    public string? WorktreeInView(DashboardSnapshot snapshot)
    {
        if (_pinned is { } pinned)
        {
            return Resolve(snapshot, pinned);
        }

        if (Agent(snapshot) is { } agent)
        {
            if (agent.WorktreePath is { } path && Find(snapshot, path) is { } seen)
            {
                _lastWorktree = (path, seen.Worktree.RepoRoot);
                SaveLastView();
            }

            return agent.WorktreePath;
        }

        return _lastWorktree is { } last ? Resolve(snapshot, last) : null;
    }

    /// <summary>A worktree in the snapshot, with its repository.</summary>
    public static (RepoView Repo, WorktreeView Worktree)? Find(DashboardSnapshot snapshot, string path)
    {
        foreach (var repo in snapshot.Repos)
        {
            if (repo.Worktrees.FirstOrDefault(w => w.Worktree.Path == path) is { } worktree)
            {
                return (repo, worktree);
            }
        }

        return null;
    }

    private static string? Resolve(DashboardSnapshot snapshot, (string Path, string RepoRoot) wanted)
    {
        var worktrees = snapshot.Repos.FirstOrDefault(r => r.Root == wanted.RepoRoot)?.Worktrees ?? [];
        if (worktrees.Any(w => w.Worktree.Path == wanted.Path && !w.Worktree.Prunable))
        {
            return wanted.Path;
        }

        if (worktrees.FirstOrDefault(w => w.Worktree.IsPrimary && !w.Worktree.Prunable) is { } primary)
        {
            return primary.Worktree.Path;
        }

        // The repository is not in the snapshot, or failed to list: trust the disk.
        return System.IO.Directory.Exists(wanted.Path) ? wanted.Path : null;
    }

    /// <summary>Shows a worktree, and keeps showing it whichever agent is picked.</summary>
    public void PinWorktree(string path, string repoRoot)
    {
        if (_pinned == (path, repoRoot))
        {
            return;
        }

        _pinned = (path, repoRoot);
        Raise();
    }

    /// <summary>Goes back to showing the worktree of the agent in view.</summary>
    public void FollowAgent()
    {
        if (_pinned is null)
        {
            return;
        }

        _pinned = null;
        Raise();
    }

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
            model = ActivatorUtilities.CreateInstance<ChangesModel>(_services, worktreePath);
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
            doc = new EditorDoc(key, kind, path) { Preview = !keep, Pane = group.Focused };
            int? preview = keep ? null : group.Docs.FindIndex(d => d.Preview && d.Pane == group.Focused);
            if (preview is >= 0)
            {
                group.Docs[preview.Value] = doc;
            }
            else
            {
                group.Add(doc);
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

    /// <summary>
    /// Opens a file by its absolute path, for a request from outside the app.
    /// Inside the worktree in view it opens as that worktree's file, editable as
    /// any other; anywhere else, a screenshot in a temp folder say, it opens as
    /// an external document among the worktree's tabs, which the user can still
    /// edit and save. Always kept rather than previewed: somebody asked for it by
    /// name. Asking again for a file already open brings it to the front and
    /// reads it again, unless it has unsaved changes.
    /// </summary>
    public void OpenExternal(string worktreePath, string absolutePath, int? line = null)
    {
        if (worktreePath.Length > 0
            && System.IO.Path.GetRelativePath(worktreePath, absolutePath).Replace('\\', '/') is var relative
            && WorktreeFiles.Resolve(worktreePath, relative) is { } resolved
            && string.Equals(resolved, System.IO.Path.GetFullPath(absolutePath), AgentsDashboard.Core.Repos.RealPaths.Comparison))
        {
            OpenFile(worktreePath, relative, line, keep: true);
            if (Editors(worktreePath).Find(EditorDoc.FileKey(relative)) is { } opened)
            {
                opened.Reveal++;
            }

            return;
        }

        var group = Editors(worktreePath);
        var key = EditorDoc.ExternalKey(absolutePath);
        if (group.Find(key) is not { } doc)
        {
            doc = new EditorDoc(key, DocKind.External, absolutePath);
            group.Add(doc);
        }

        doc.Line = line;
        doc.Reveal++;
        group.ActiveKey = key;
        Raise();
    }

    /// <summary>Opens the worktree's whole diff, every file in one document, scrolled to a file when one is given.</summary>
    public void OpenChanges(string worktreePath, string? file = null)
    {
        var group = Editors(worktreePath);
        if (group.Find(EditorDoc.ChangesKey) is null)
        {
            group.Add(new EditorDoc(EditorDoc.ChangesKey, DocKind.Changes, null));
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
            group.Add(new EditorDoc(key, kind, null));
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

            group.Add(doc);
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
    public void Close(string worktreePath, string key) => Close(worktreePath, [key]);

    /// <summary>
    /// Closes several tabs at once, for Close Others, Close All and the rest of
    /// the tab menu, so the strip redraws once rather than once a tab.
    /// </summary>
    public void Close(string worktreePath, IReadOnlyCollection<string> keys)
    {
        var group = Editors(worktreePath);
        if (!group.Docs.Any(d => keys.Contains(d.Key)))
        {
            return;
        }

        group.Remove(keys.ToHashSet(StringComparer.Ordinal));
        Raise();
    }

    /// <summary>
    /// Moves a tab to the other side of the editor, splitting it when it is not
    /// split yet. A document is only ever open on one side: two editors on one
    /// file would be two Monaco models with one URI, and two sets of unsaved text
    /// to reconcile. The tab's component is kept, so its undo stack, scroll and
    /// unsaved work go with it.
    /// </summary>
    public void MoveToOtherSide(string worktreePath, string key)
    {
        var group = Editors(worktreePath);
        if (group.Find(key) is not { } doc)
        {
            return;
        }

        group.Move(doc, 1 - doc.Pane);
        Raise();
    }

    /// <summary>The side of a split editor that was last clicked in, where files open next.</summary>
    public void FocusPane(string worktreePath, int pane)
    {
        var group = Editors(worktreePath);
        if (group.Focused == pane || !group.Docs.Any(d => d.Pane == pane))
        {
            return;
        }

        group.Focused = pane;
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

    private void Raise()
    {
        SaveLastView();
        Changed?.Invoke();
    }

    /// <summary>
    /// The agent to open in place of an empty window, once: the one open when the
    /// app was last used, if it is still in the list. Asked for by the page that
    /// would otherwise say "Pick an agent", so a link to another agent, or
    /// having picked one already, wins.
    /// </summary>
    public string? TakeAgentToReopen(DashboardSnapshot snapshot)
    {
        var id = _reopen;
        _reopen = null;
        return id is not null && SessionId is null && _directory.Targets(snapshot).Any(t => t.SessionId == id) ? id : null;
    }

    /// <summary>
    /// Writes down the agent and worktree in view when either moved. The agent is
    /// kept after it is closed, since reopening on an empty window helps nobody,
    /// and the worktree is whichever one is showing.
    /// </summary>
    private void SaveLastView()
    {
        var worktree = _pinned ?? _lastWorktree;
        var view = new LastView(
            SessionId ?? _reopen ?? _lastView?.SessionId,
            worktree?.Path,
            worktree?.RepoRoot,
            _pinned is not null);

        if (view != _lastView)
        {
            _lastView = view;
            _lastViewStore.Save(view);
        }
    }

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

    /// <summary>A file outside the worktree, by absolute path, asked for from outside the app. Read-only.</summary>
    External,
}

/// <summary>
/// A worktree's open documents, in the order they were opened, on one side of
/// the editor or split across two.
/// </summary>
/// <remarks>
/// One list for both sides rather than a list each, so the editor area can draw
/// every document in one keyed list and moving a tab across only changes where
/// it is drawn, never rebuilds it. A side is never left empty: when its last tab
/// goes, the other side takes the whole width.
/// </remarks>
public sealed class EditorGroup
{
    public List<EditorDoc> Docs { get; } = [];

    /// <summary>The tab in front on each side.</summary>
    private readonly string?[] _active = new string?[2];

    /// <summary>The side new documents open on and <see cref="ActiveKey"/> speaks for: 0 left, 1 right.</summary>
    public int Focused { get; set; }

    public bool Split => Docs.Any(d => d.Pane == 1);

    /// <summary>
    /// The tab in front on the focused side. Setting it to a document on the
    /// other side focuses that side, so opening a file already open over there
    /// goes to it rather than opening it twice.
    /// </summary>
    public string? ActiveKey
    {
        get => _active[Focused];
        set
        {
            if (value is not null && Find(value) is { } doc)
            {
                Focused = doc.Pane;
            }

            _active[Focused] = value;
        }
    }

    public EditorDoc? Active => ActiveKey is null ? null : Find(ActiveKey);

    /// <summary>The tab in front on one side, whether or not that side is focused.</summary>
    public string? ActiveIn(int pane) => _active[pane];

    public EditorDoc? Find(string key) => Docs.FirstOrDefault(d => d.Key == key);

    /// <summary>How many jumps back are kept. Past this the oldest go, which nobody walks back to.</summary>
    internal const int HistoryLimit = 50;

    /// <summary>Where jumps left from, most recent last.</summary>
    internal List<NavPoint> Back { get; } = [];

    /// <summary>Where going back left from, for going forward again.</summary>
    internal List<NavPoint> Forward { get; } = [];

    /// <summary>One side's tabs: pinned first, then the rest, each in the order opened.</summary>
    public IEnumerable<EditorDoc> TabsIn(int pane)
    {
        var tabs = Docs.Where(d => d.Pane == pane);
        return tabs.Where(d => d.Pinned).Concat(tabs.Where(d => !d.Pinned));
    }

    /// <summary>Opens a document on the focused side, after the tab in front there.</summary>
    internal void Add(EditorDoc doc)
    {
        doc.Pane = Focused;
        Docs.Insert(InsertAt(), doc);
    }

    /// <summary>A new tab goes after the one in front, where you are looking.</summary>
    private int InsertAt()
    {
        var active = ActiveKey is null ? -1 : Docs.FindIndex(d => d.Key == ActiveKey);
        return active < 0 ? Docs.Count : active + 1;
    }

    /// <summary>
    /// Takes documents away. A side whose tab in front went shows its neighbour
    /// in the strip, the one to the right if there is one, the way closing a tab
    /// does everywhere.
    /// </summary>
    internal void Remove(IReadOnlySet<string> keys)
    {
        for (var pane = 0; pane < 2; pane++)
        {
            if (_active[pane] is { } active && keys.Contains(active))
            {
                _active[pane] = Neighbour(pane, active, keys);
            }
        }

        Docs.RemoveAll(d => keys.Contains(d.Key));
        Collapse();
    }

    /// <summary>Moves a document to a side, after the tab in front there, and puts it in front.</summary>
    internal void Move(EditorDoc doc, int pane)
    {
        if (doc.Pane == pane)
        {
            return;
        }

        if (_active[doc.Pane] == doc.Key)
        {
            _active[doc.Pane] = Neighbour(doc.Pane, doc.Key, new HashSet<string>(StringComparer.Ordinal) { doc.Key });
        }

        Docs.Remove(doc);
        doc.Pane = pane;
        doc.Preview = false;
        var after = _active[pane] is { } front ? Docs.FindIndex(d => d.Key == front) : -1;
        Docs.Insert(after < 0 ? Docs.Count : after + 1, doc);
        _active[pane] = doc.Key;
        Focused = pane;
        Collapse();
    }

    private string? Neighbour(int pane, string key, IReadOnlySet<string> going)
    {
        var order = TabsIn(pane).ToList();
        var at = order.FindIndex(d => d.Key == key);
        return (order.Skip(at).FirstOrDefault(d => !going.Contains(d.Key))
            ?? order.Take(at).LastOrDefault(d => !going.Contains(d.Key)))?.Key;
    }

    /// <summary>An empty side goes, and the other takes the width.</summary>
    private void Collapse()
    {
        if (Docs.Count > 0 && Docs.All(d => d.Pane == 1))
        {
            foreach (var doc in Docs)
            {
                doc.Pane = 0;
            }

            _active[0] = _active[1];
        }

        if (!Split)
        {
            _active[1] = null;
            Focused = 0;
        }
    }
}

/// <summary>A tab in the editor.</summary>
public sealed class EditorDoc(string key, DocKind kind, string? path)
{
    public const string ChangesKey = "changes";
    public const string SettingsKey = "settings";
    public const string WorktreesKey = "worktrees";

    public static string FileKey(string path) => "file:" + path;

    public static string ExternalKey(string absolutePath) => "ext:" + absolutePath;

    public static string KeyFor(DocKind kind, string path) => kind == DocKind.Diff ? "diff:" + path : FileKey(path);

    public string Key { get; } = key;

    public DocKind Kind { get; } = kind;

    /// <summary>The worktree-relative path of a file or a file's diff, or an external file's absolute path.</summary>
    public string? Path { get; } = path;

    public bool Pinned { get; set; }

    /// <summary>The side of the editor the tab is on: 0 left, or 1 right when split.</summary>
    public int Pane { get; set; }

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
        DocKind.External => System.IO.Path.GetFileName(Path!),
        _ => Path![(Path!.LastIndexOf('/') + 1)..],
    };
}

/// <summary>A place in the editor: a tab, and the line the caret was on when known.</summary>
public sealed record NavPoint(string Key, int? Line);
