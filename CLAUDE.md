# Agents Dashboard - project notes

A cross-platform Blazor Server desktop app (Photino window over a loopback host)
for watching Claude Code agents across git worktrees: who needs an answer, what
their tests are doing, and reviewing their diffs.

- `src/AgentsDashboard.Core` - all logic, no ASP.NET dependency. Claude readers,
  git layer and parsers, TRX reader, review writing, the monitor loop.
- `src/AgentsDashboard.App` - Blazor Server UI and the Photino window.
- `tests/AgentsDashboard.Core.Tests` - xUnit v3 on Microsoft.Testing.Platform.

`dotnet build`, `dotnet test`, `dotnet run --project src/AgentsDashboard.App`.

## Documentation

`README.md` is the landing page: what it is, how to run it, the architecture. The
long rationale lives in `docs/`, one file per subsystem that is easy to get wrong
twice:

- `docs/test-monitoring.md` - streaming TRX, the process signal, the two clocks,
  the telemetry installer
- `docs/review.md` - diff bases, the comment draft, the submit order
- `docs/agents.md` - the session registry, work summaries, notification rules
- `docs/syntax.md` - Monaco colouring in the diff, the server fallback, the two
  passes a diff hunk needs
- `docs/agent-control.md` - the chat, the CLI lifecycle, why sending stops the agent first
- `docs/staging.md` - the two-character status field, unstaging with no HEAD
- `docs/editor.md` - Monaco in the Files tab, vendoring it, stamp-based saves
- `docs/code-intelligence.md` - Roslyn in-process, the on-demand load, why
  references land in a panel rather than a peek widget

A change to one of those subsystems belongs in its doc, with at most a line in
the README. Keep the README a landing page.

## Things that will bite you

**Blazor render modes.** The app sets `@rendermode` on `<Routes>` and
`<HeadOutlet>` in `App.razor` with `prerender: false`. Without a render mode
every page renders statically: it looks right and no event handler ever fires.

**Static web assets.** `Program.cs` calls `builder.WebHost.UseStaticWebAssets()`
explicitly and `app.MapStaticAssets()`. `CreateBuilder` only wires static web
assets in the Development environment, and without them `_framework/blazor.web.js`
404s and the app is inert.

**`dotnet test` and `--nologo`.** In Microsoft.Testing.Platform mode `dotnet test`
forwards `--nologo` to the test application, which rejects it and reports zero
tests with exit code 5. Never pass it. `TestRunner` has a comment saying so.

**xunit.v3 needs `UseMicrosoftTestingPlatformRunner`.** Without it the generated
entry point is the console runner, the TRX extension is never registered, and
`--report-trx` is an unknown option.

**MTP package versions must agree.** `Microsoft.Testing.Platform` newer than the
`Microsoft.Testing.Platform.MSBuild` that xunit brings in breaks the `dotnet test`
handshake. They are pinned level in `Directory.Packages.props`.

**`--resume` on a running session starts a copy.** It does not continue it: it
clones the conversation under a new id, prints a note on **stderr**, and exits 0.
`ClaudeCli.SendAsync` therefore stops the agent, waits for it to actually be gone,
and reads both output streams. Never read only stdout from the CLI.

**Liveness is presence in `claude agents --json` without `--all`.** A stopped
session keeps its entry under `--all`; a running one that has not transitioned yet
has no status and no pid. Judging by either reads a live agent as stopped, and
the resume that follows clones the conversation. `pid` can also be JSON null, and
`JsonElement.TryGetInt32` throws on a null element rather than returning false.

**Worktree URLs are not prefix-comparable.** A linked worktree lives inside the
primary one, so the primary's URL is a prefix of every other worktree's. Use
`WorktreeRoute.Shows`, which compares whole paths. A prefix test lights up the
primary whenever any of its worktrees is selected.

**Grid columns in the diff need `minmax(0, 1fr)`.** A bare `1fr` has an `auto`
minimum, so one long line pushes the column past its share and scrolls the whole
page sideways. `.main` also pins `overflow-x: hidden`.

**Line selection is dragged in JavaScript, not on the circuit.** `app.js` follows
mousedown/mousemove/mouseup and calls `SelectLines` once on release. Its preview
class is `picking`; the server renders `in-range`. Never let the two share a class.

**A unified diff interleaves two line numberings.** Removed rows are numbered on
the left, added and context rows on the right, so consecutive rows on screen are
not consecutive numbers and are often not even the same side. Anything that walks
a run of rows has to walk them in the order they are drawn and pick the side at
the end, not filter by side from the start: filtering stops dead at the first
row of the other kind, which in a one-line replacement is the very next row.

**SignalR caps a client-to-server message at 32 KB.** Saving from the Files tab
sends the whole edited file up the circuit, and most source files are over that.
The hub does not report it as an error, it drops the circuit. `Program.cs` sets
`MaximumReceiveMessageSize` to 4 MB, which covers `WorktreeFiles.MaxBytes` (2 MB)
plus the interop envelope. Anything that ships file contents to the server has to
stay inside that.

**A Monaco model has to be named after its file.** `monaco.js` creates every
model with `monaco.Uri.file(absolutePath)`, and swapping files swaps the model
rather than calling `setValue`. Monaco addresses a definition in another file by
URI and has nothing to compare an anonymous `inmemory://model/1` against, so with
an unnamed model every cross-file F12 is silently dropped. The C# providers are
registered once for the page, not per editor: Monaco's registries are global and
a second registration answers every hover twice.

**The Changes tab controls its own rendering.** The worktree page re-renders every
second because the monitor publishes a snapshot every second. Re-rendering a diff
of a few thousand lines at that rate saturates the circuit and the page stops
answering clicks, so `ChangesTab` overrides `ShouldRender` and every handler calls
`Touch()`. There is also a total line budget past which files start folded.

## Conventions

- Spaces, not tabs. No em dashes or emojis in UI copy or comments.
- Comments explain why, not what. Prefer a short paragraph on the non-obvious
  decision over a line-by-line narration.
- Nothing is ever written into `~/.claude`. The dashboard only reads it.
- Anything that edits the user's repository is offered, never done on its own.
- Don't commit unless asked.
