# The file editor

The Files tab is an editor, not a viewer. An agent leaves a worktree in a state
you want to nudge rather than rewrite: a wrong constant, a stray line, a comment
that is now a lie. Opening VS Code for that is a context switch away from the
dashboard you are watching, so the tab edits in place and keeps the VS Code link
for the times a real editor is the right tool.

## Monaco, and why it is not committed

The editor is Monaco, the one VS Code itself uses, at a pinned version. That
buys real syntax colouring for every language this repository contains, plus
find, multi-cursor and a minimap, for no code here.

The cost is size: the AMD build is 24 MB across 150 files. It is a dependency
rather than source, so it is not in the repository. `tools/vendor-monaco.sh`
fetches it with `npm pack`, copies `package/min/vs` into
`wwwroot/monaco/vs`, and writes a `VERSION` marker beside it. An MSBuild target
in the App project runs the script when `wwwroot/monaco/vs/loader.js` is
missing, so a fresh clone needs nothing but `dotnet build`.

The `VERSION` marker is the part that is easy to get wrong. `loader.js` exists
after *any* version has been vendored, so a check for the file alone would pin
whatever version a machine fetched first, forever. The script compares the
marker and re-fetches when it disagrees.

Two consequences worth knowing:

- **The vendored files are added as Content by the target itself.** The
  `wwwroot` Content glob is evaluated before any target runs, so files that
  appear during the build are invisible to the static web assets pipeline
  unless something adds them. The `VendorMonaco` target runs before
  `ResolveCurrentProjectStaticWebAssetsInputs` and adds `wwwroot/monaco/**`
  with a project-relative identity, which is the only shape that pipeline
  keeps. That is what makes the first build on a fresh clone serve the editor
  rather than only the second.
- **Nothing is fetched until an editor is created.** `monaco.js` is a small
  shim that registers `window.agentsEditor`; it injects Monaco's AMD loader on
  the first `create()` and memoises the promise. Every page in the app except
  this tab therefore pays nothing for it.

Monaco 0.56's `editor.main.js` installs its own `MonacoEnvironment.getWorker`
and injects its own stylesheet, and both resolve next to the bundle. Since the
bundle is served from the app's own origin there is nothing to override, which
is why there is no `getWorkerUrl` here. The loader's `paths` are made absolute
from `document.baseURI` rather than left relative, because the Files tab lives
at `/worktree/<escaped path>/files` and a relative `monaco/vs` would be looked
for under the worktree segment.

## Saving

Saving is stamp-based, not lock-based. `WorktreeFiles.ReadText` returns the
SHA-256 of the bytes it read, and `Write` re-hashes the file on disk and refuses
when the two disagree. That matters here more than in a normal editor: the agent
whose work you are reading is usually still writing in the same worktree, and a
blind save would silently undo whatever it did while the file was open.

A conflict is offered as a choice rather than resolved: **Reload from disk**
re-reads and throws the editor's text away, **Overwrite** writes anyway. There
is no merge, because the other writer is an agent that can be asked to redo its
change far more cheaply than a three-way merge can be got right.

The write itself goes to a temp file in the same directory and is moved over the
original, so an interrupted save leaves the old file rather than half of a new
one. The line ending and BOM the file was read with are carried back into the
write: normalising them would turn a one line edit into a whole-file diff.

```mermaid
sequenceDiagram
    participant U as User
    participant M as Monaco (browser)
    participant T as FilesTab (circuit)
    participant F as WorktreeFiles
    participant D as Disk

    U->>M: Ctrl+S
    M->>T: SaveFromEditor(text)
    T->>F: Write(path, text, stamp, eol, bom)
    F->>D: sha256 of current bytes
    alt stamp matches
        F->>D: write temp, move over original
        F-->>T: Ok, new stamp
        T-->>M: clear dirty, "Saved just now"
    else changed underneath
        F-->>T: Conflict
        T-->>U: Reload from disk / Overwrite
    end
```

## Undo survives a save

A save comes back with a new stamp, and a new stamp sends the text back into
the editor. Monaco's `setValue` resets the undo stack, so doing that after every
save meant Ctrl+Z stopped working the moment you saved. `setText` in `monaco.js`
only swaps the model when the file itself changes. For the same file it leaves a
buffer that already holds the saved text alone, and puts different text (a
reload after a conflict) in as one undoable edit. Undoing past the save point
makes the file dirty again, and it saves like any other change.

## The size limit that bites

The saved text travels from the browser to the server over the Blazor circuit,
and SignalR caps a client-to-server message at 32 KB by default. Most source
files are over that, and the failure is not a friendly error: the hub tears the
circuit down. `Program.cs` therefore sets `MaximumReceiveMessageSize` to 4 MB,
which is `WorktreeFiles.MaxBytes` (2 MB) with room for the interop envelope and
UTF-8 growth. Anything larger than `MaxBytes` comes back marked truncated and is
shown read-only, so it never reaches the hub at all.

Only the text crosses on a save. Typing does not: the browser compares the
buffer against its own baseline and sends a single boolean when the dirty flag
flips, so a keystroke costs nothing on the circuit.

## Deep links

`Urls.AgentFile(session, file, line)` builds
`chat/<session>/files?file=<rel>&line=N`: a path an agent mentions opens in that
agent's own Files tab, so following it never leaves the agent. The file is in the
query string rather than the route so the tab stays one route per agent.

The agent view keys the Files tab on the worktree alone, deliberately. Keying
it on the file as well would tear the editor down and build a new one for every
link followed, losing the loaded Monaco instance and the scroll position with
it; instead the tab pushes the new text and line into the editor that is already
there.

`CodeEditor` renders its host `div` once and returns `false` from `ShouldRender`
forever after, for the same reason `ChangesTab` does: the agent view
re-renders every second because the monitor publishes a snapshot that often, and
the host is full of children Blazor did not create.

## Change marks

The editor marks lines that differ from the diff base the way VS Code does: a
green bar for added lines, blue for modified, a red wedge at the foot of the line
above a deletion, and matching ticks in the scrollbar and the minimap. The base is
the one the Changes tab last compared against, kept per worktree in
`WorktreeViews`, so the two tabs never disagree about what changed; switching the
base in Changes re-marks an open file.

`DiffReader.ReadFileAsync` diffs the one file with `-U0`, so every hunk is exactly
one change, and `ChangeMarks.From` turns hunks into marks: only additions is
added, only removals is a deletion, a mix marks every added line modified. An
untracked file has no diff and comes back as one hunk adding every line. Marks
are read against the disk, so they are refreshed on open, save and reload;
between those, Monaco's decorations move with edits and stay on their lines.

A link into a file with no line in it lands on the first change, since that is
almost always what the agent was pointing at.

## Coming back to the tab

Every tab opened for an agent stays built while that agent is on screen; the
ones not in front are hidden with `visibility`, not `display`. Rebuilding the
Files tab meant listing the worktree, reading the file and starting a fresh
Monaco, which flickered on every switch, and a `display: none` box loses both its
scroll position and Monaco's size. Switching agents does rebuild them.

What should survive that lives outside the tab. `WorktreeViews` keeps the folded
directories and the open file per worktree; the tree starts fully folded the
first time, apart from the path to the open file. `monaco.js` keeps each file's
view state (scroll and caret) by model URI for the life of the page, and
`keepScroll` in `app.js` keeps the tree's scroll position, retrying the restore
as content arrives until it lands or you scroll yourself.
