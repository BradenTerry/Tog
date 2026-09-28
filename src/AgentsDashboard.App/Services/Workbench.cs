using AgentsDashboard.App.Extensions;
using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Presentation;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Extensions;

namespace AgentsDashboard.App.Services;

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
/// preference. Two exceptions: the panels' layout, kept in
/// <see cref="PanelLayoutStore"/> because it is how you like the window, and the
/// agent and worktree in view, kept in <see cref="LastViewStore"/> so the app
/// reopens on them.
/// </remarks>
public sealed class Workbench : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly AgentDirectory _directory;
    private readonly LastViewStore _lastViewStore;
    private readonly PanelLayoutStore _layoutStore;
    private LastView? _lastView;

    /// <summary>The agent open when the app was last used, until a page has had the chance to reopen it.</summary>
    private string? _reopen;

    public Workbench(IServiceProvider services, AgentDirectory directory, LastViewStore lastViewStore, PanelLayoutStore layoutStore)
    {
        _services = services;
        _directory = directory;
        _lastViewStore = lastViewStore;
        _layoutStore = layoutStore;
        Layout = layoutStore.Load();
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

            if (agent.WorktreePath is not null)
            {
                _created.Remove(agent.SessionId);
                return agent.WorktreePath;
            }

            return _created.GetValueOrDefault(agent.SessionId);
        }

        return _lastWorktree is { } last ? Resolve(snapshot, last) : null;
    }

    /// <summary>
    /// Worktrees made for an agent as it started, by session, until the snapshot
    /// lists them. The monitor only sees a new worktree on its next pass, and
    /// until then the agent looks like one outside git, whose editor holds the
    /// tabs opened with no worktree in view (Settings, Worktrees). Those showed
    /// up over a new agent for a second, so its worktree is taken on trust.
    /// </summary>
    private readonly Dictionary<string, string> _created = new(StringComparer.Ordinal);

    /// <summary>Shows <paramref name="worktreePath"/> for an agent just started in it, before the monitor has listed it.</summary>
    public void StartedIn(string sessionId, string worktreePath)
    {
        _created[sessionId] = worktreePath;
        Raise();
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

    /// <summary>Where every view sits in the panels, and the panels' sizes. Kept in <see cref="PanelLayoutStore"/>.</summary>
    public PanelLayout Layout { get; }

    public DockedPanel Left => Layout.Left;

    public DockedPanel Right => Layout.Right;

    public DockedPanel Bottom => Layout.Bottom;

    public DockedPanel Panel(PanelSide side) => Layout.Panel(side);

    /// <summary>The panel a view goes to until it is moved: the app's own by what they are, an extension's where it asks.</summary>
    public PanelSide DefaultSide(string key) => key switch
    {
        FilesTab => PanelSide.Left,
        SourceControlTab => PanelSide.Right,
        ChatTab => PanelSide.Bottom,
        _ => Extensions.Views.FirstOrDefault(v => v.View.Key == key) is { } live ? SideOf(live.View.DefaultLocation) : PanelSide.Right,
    };

    /// <summary>Every view there is, the app's first, whether or not it applies to what is in view.</summary>
    public IReadOnlyList<PanelView> KnownViews =>
    [
        new(FilesTab, PanelSide.Left),
        new(SourceControlTab, PanelSide.Right),
        new(ChatTab, PanelSide.Bottom),
        .. Extensions.Views.Select(v => new PanelView(v.View.Key, SideOf(v.View.DefaultLocation))),
    ];

    private ExtensionHost Extensions => _services.GetRequiredService<ExtensionHost>();

    /// <summary>The panel view being dragged, which every panel draws drop targets for.</summary>
    public string? DraggingView { get; private set; }

    public void StartViewDrag(string key)
    {
        DraggingView = key;
        Raise();
    }

    public void EndViewDrag()
    {
        if (DraggingView is not null)
        {
            DraggingView = null;
            Raise();
        }
    }

    /// <summary>Drops the dragged view into a section's strip, before another view or at the end.</summary>
    public void DropView(PanelSide side, int section, string? before)
    {
        if (DraggingView is { } key)
        {
            DraggingView = null;
            Layout.Move(key, side, section, before, KnownViews);
            RaiseLayout();
        }
    }

    /// <summary>Drops the dragged view into a new section above or below another.</summary>
    public void DropViewSplit(PanelSide side, int section, bool below)
    {
        if (DraggingView is { } key)
        {
            DraggingView = null;
            Layout.Split(key, side, section, below, KnownViews);
            RaiseLayout();
        }
    }

    /// <summary>The tab menu's move, to the end of another panel.</summary>
    public void MoveView(string key, PanelSide side)
    {
        Layout.MoveToPanel(key, side, KnownViews);
        RaiseLayout();
    }

    /// <summary>The tab menu's split: the view into a section of its own under the one it is in.</summary>
    public void SplitView(string key)
    {
        var (side, section) = Layout.Locate(key, DefaultSide(key));
        Layout.Split(key, side, section, below: true, KnownViews);
        RaiseLayout();
    }

    public void ToggleSection(PanelSide side, int section)
    {
        Layout.ToggleCollapsed(side, section);
        RaiseLayout();
    }

    /// <summary>The heights app.js gave the sections of a panel when a border between them was dragged. Already on screen, so nothing is redrawn.</summary>
    public void SetSectionWeights(PanelSide side, IReadOnlyList<int> sections, IReadOnlyList<double> weights)
    {
        for (var i = 0; i < Math.Min(sections.Count, weights.Count); i++)
        {
            Layout.SetWeight(side, sections[i], weights[i]);
        }

        _layoutStore.Save(Layout);
    }

    /// <summary>A panel border's size as app.js stores it, or null when it was reset. Already on screen, so nothing is redrawn.</summary>
    public void SetSize(string name, string? value)
    {
        if (value is null)
        {
            Layout.Sizes.Remove(name);
        }
        else
        {
            Layout.Sizes[name] = value;
        }

        _layoutStore.Save(Layout);
    }

    /// <summary>Every view back in the panel it asks for.</summary>
    public void ResetViews()
    {
        Layout.ResetViews();
        RaiseLayout();
    }

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
    /// What the New agent dialog should start with, when whatever opened it knew:
    /// the Worktrees view's "New agent" names a repository and a worktree, and an
    /// extension's offer can add a worktree name and a prompt. Read by the form as
    /// it is built, and cleared when the dialog opens without one.
    /// </summary>
    public NewAgentPreset? NewAgentIn { get; private set; }

    /// <summary>
    /// Bumped on every preset, so the dialog builds its form afresh: a form already
    /// open has read the last preset and would never see the new one.
    /// </summary>
    public int NewAgentGeneration { get; private set; }

    /// <summary>
    /// How the offer the dialog holds ended, for the extension that made it.
    /// Settled with null by anything that takes the offer off the dialog without
    /// starting it, so an extension waiting on it is never left hanging.
    /// </summary>
    private TaskCompletionSource<string?>? _offer;

    public void OpenNewAgent()
    {
        SettleOffer(null);
        NewAgentIn = null;
        if (!NewAgentOpen)
        {
            NewAgentOpen = true;
            Raise();
        }
    }

    /// <summary>Opens the New agent dialog with a repository and one of its worktrees already picked.</summary>
    public void OpenNewAgent(string repoRoot, string worktreePath) =>
        OpenNewAgent(new NewAgentPreset(repoRoot, worktreePath));

    /// <summary>Opens the New agent dialog filled in, replacing whatever it held.</summary>
    public void OpenNewAgent(NewAgentPreset preset)
    {
        SettleOffer(null);
        NewAgentIn = preset;
        NewAgentGeneration++;
        NewAgentOpen = true;
        Raise();
    }

    /// <summary>
    /// Opens the dialog filled in with an extension's offer, and completes with
    /// the session it started, or null if it started none.
    /// </summary>
    public Task<string?> OfferNewAgent(NewAgentPreset preset)
    {
        OpenNewAgent(preset);

        // Continuations run off the circuit's thread, so an extension's await
        // never runs inside the dialog's own event handler.
        var offer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _offer = offer;
        return offer.Task;
    }

    /// <summary>The dialog started an agent: whatever offer it held is taken up.</summary>
    public void NewAgentStarted(string sessionId) => SettleOffer(sessionId);

    private void SettleOffer(string? sessionId)
    {
        var offer = _offer;
        _offer = null;
        offer?.TrySetResult(sessionId);
    }

    public void CloseNewAgent()
    {
        SettleOffer(null);
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

    /// <summary>Brings a view up wherever it is: its panel open, its section unfolded, it in front.</summary>
    public void Show(string view)
    {
        if (Layout.Show(view, DefaultSide(view)))
        {
            RaiseLayout();
        }
    }

    /// <summary>A click on a panel tab: in front of the section it is in.</summary>
    public void Activate(PanelSide side, int section, string view)
    {
        Layout.Activate(side, section, view);
        RaiseLayout();
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
    /// Opens a file on the other side of the editor, splitting it if it is not
    /// split yet: Go to File's Ctrl+Enter, VS Code's "open to the side". A file
    /// already open on this side moves across, since a document is only ever
    /// open on one side.
    /// </summary>
    public void OpenFileToSide(string worktreePath, string path, int? line = null)
    {
        var group = Editors(worktreePath);
        if (group.Find(EditorDoc.FileKey(path)) is { } open)
        {
            if (open.Pane == group.Focused)
            {
                group.Move(open, 1 - open.Pane);
            }
        }
        else
        {
            group.Focused = 1 - group.Focused;
        }

        Open(worktreePath, DocKind.File, path, line, keep: true);
    }

    /// <summary>
    /// Moves the caret of the file in front to a line, for Go to Line. False
    /// when what is in front is not a file, so there is no line to go to.
    /// </summary>
    public bool GoToLine(string worktreePath, int line)
    {
        if (Editors(worktreePath).Active is not { Kind: DocKind.File } doc)
        {
            return false;
        }

        doc.Line = line;
        doc.Reveal++;
        Raise();
        return true;
    }

    /// <summary>
    /// Asks the window to open Go to File, with <paramref name="prefix"/>
    /// already typed (":" for Go to Line). Asked again while it is open, it
    /// moves down the list, as pressing Cmd+P twice does in VS Code.
    /// </summary>
    public void ShowQuickOpen(string prefix = "") => QuickOpenRequested?.Invoke(prefix);

    public event Action<string>? QuickOpenRequested;

    /// <summary>
    /// Asks the title bar to open its list of agents from the keyboard, or shut
    /// it if it is open. Arrows move through it and Enter picks, as in any menu.
    /// </summary>
    public void ToggleAgentPicker() => AgentPickerRequested?.Invoke();

    public event Action? AgentPickerRequested;

    /// <summary>
    /// Brings the conversation up and puts the caret in its box. The chat
    /// panel is only built once its tab has been opened, and a new one takes
    /// focus as it appears, so the event is for the one already built.
    /// </summary>
    public void FocusChat()
    {
        Show(ChatTab);
        ChatFocusRequested?.Invoke();
    }

    public event Action? ChatFocusRequested;

    /// <summary>How many recently opened files are kept per worktree.</summary>
    private const int RecentLimit = 50;

    private readonly Dictionary<string, List<string>> _recent = new(StringComparer.Ordinal);

    /// <summary>
    /// A worktree's files in the order they were last brought to the front, the
    /// most recent first, for Go to File's history. Memory only, like the tabs.
    /// </summary>
    public IReadOnlyList<string> RecentFiles(string worktreePath) =>
        _recent.TryGetValue(worktreePath, out var recent) ? recent : [];

    private void Touch(string worktreePath, EditorDoc? doc)
    {
        if (doc is not { Kind: DocKind.File, Path: { } path })
        {
            return;
        }

        if (!_recent.TryGetValue(worktreePath, out var recent))
        {
            recent = [];
            _recent[worktreePath] = recent;
        }

        recent.Remove(path);
        recent.Insert(0, path);
        if (recent.Count > RecentLimit)
        {
            recent.RemoveAt(recent.Count - 1);
        }
    }

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
            doc.Reveal++;
        }

        group.ActiveKey = key;
        Touch(worktreePath, doc);
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
    /// Opens Settings as a dialog over the window, like New agent: it belongs to
    /// no file and no worktree, so it has no place among an agent's tabs, and
    /// closing it leaves you where you were.
    /// </summary>
    public void OpenSettings(string? section = null)
    {
        SettingsSection = section ?? SettingsSection;
        SettingsOpen = true;
        Raise();
    }

    public bool SettingsOpen { get; private set; }

    public void CloseSettings()
    {
        if (SettingsOpen)
        {
            SettingsOpen = false;
            Raise();
        }
    }

    /// <summary>A section Settings should turn to, taken once by the page: Open Keyboard Shortcuts asks for "keys".</summary>
    public string? SettingsSection { get; set; }

    /// <summary>Opens the Worktrees view, every worktree of every repo, the way Settings opens.</summary>
    public void OpenWorktrees(string? worktreePath) =>
        OpenPage(worktreePath, EditorDoc.WorktreesKey, DocKind.Worktrees);

    /// <summary>
    /// Asks to remove a worktree: <c>RemoveWorktreeDialog</c> takes the request
    /// and comes up over whatever is on screen, rather than switching to the
    /// Worktrees view, so asking from the agent list leaves the agent in view.
    /// </summary>
    public void AskRemoveWorktree(string worktreePath)
    {
        RemoveRequest = worktreePath;
        Raise();
    }

    /// <summary>A worktree whose remove dialog should open, taken once.</summary>
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
        Touch(worktreePath, group.Active);
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
        _layoutStore.Save(Layout);
        Raise();
    }

    public void Dispose()
    {
        SettleOffer(null);
        foreach (var model in _changes.Values)
        {
            model.Dispose();
        }
    }
}

public enum DocKind
{
    /// <summary>A file, in an editor.</summary>
    File,

    /// <summary>Every change in the worktree, in one document.</summary>
    Changes,

    /// <summary>One file's changes.</summary>
    Diff,

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
        DocKind.Worktrees => "Worktrees",
        DocKind.External => System.IO.Path.GetFileName(Path!),
        _ => Path![(Path!.LastIndexOf('/') + 1)..],
    };
}

/// <summary>A place in the editor: a tab, and the line the caret was on when known.</summary>
public sealed record NavPoint(string Key, int? Line);

/// <summary>What the New agent dialog opens with. Null keeps the form's own default.</summary>
public sealed record NewAgentPreset(
    string? RepoRoot,
    string? WorktreePath = null,
    string? WorktreeName = null,
    string? Prompt = null);
