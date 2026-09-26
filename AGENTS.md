# Agents Dashboard - project notes

A cross-platform Blazor Server desktop app (Photino window over a loopback host)
for watching Claude Code agents across git worktrees: who needs an answer, and
reviewing their diffs. Anything else is an extension.

- `src/AgentsDashboard.Core` - all logic, no ASP.NET dependency. Claude readers,
  git layer and parsers, review writing, the monitor loop, extension discovery.
- `src/AgentsDashboard.App` - Blazor Server UI, the Photino window, the
  extension host.
- `src/AgentsDashboard.Extensions` - the extension API. Versioned: 1.x only adds.
- `extensions/DotnetTests` - the Tests tab, an extension, not shipped.
- `extensions/CSharpCode` - C# navigation from Roslyn, an extension, not shipped.
- `templates/extension` - the `dotnet new` template extensions start from.
- `tests/*` - xUnit v3 on Microsoft.Testing.Platform.

`dotnet build`, `dotnet test`, `dotnet run --project src/AgentsDashboard.App`.

## Documentation

`README.md` is the landing page: what it is, how to run it, the architecture. The
long rationale lives in `docs/`, one file per subsystem that is easy to get wrong
twice:

- `docs/extensions.md` - loading, the shared assemblies, reload, consent
- `docs/test-monitoring.md` - the Tests extension: streaming TRX, the process
  signal, the two clocks, the telemetry installer
- `docs/review.md` - diff bases, the comment draft, the submit order
- `docs/agents.md` - work summaries from transcripts, subagents, notification rules
- `docs/syntax.md` - Monaco colouring in the diff, the server fallback, the two
  passes a diff hunk needs
- `docs/agent-control.md` - ACP, the host, a turn, permissions, the bridge
- `docs/staging.md` - the two-character status field, unstaging with no HEAD
- `docs/editor.md` - Monaco in the editor, vendoring it, stamp-based saves,
  images, opening a file from outside the app
- `docs/workbench.md` - the VS Code-style layout, the panels, editor tabs, the
  shared ChangesModel, what is kept per machine
- `docs/code-intelligence.md` - the `ICodeIntelligence` extension point, the C#
  extension's Roslyn load (opt-in per worktree), MSBuild in a load context, why
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
tests with exit code 5. Never pass it. `TestRunner` in the Tests extension has a
comment saying so.

**xunit.v3 needs `UseMicrosoftTestingPlatformRunner`.** Without it the generated
entry point is the console runner, the TRX extension is never registered, and
`--report-trx` is an unknown option.

**MTP package versions must agree.** `Microsoft.Testing.Platform` newer than the
`Microsoft.Testing.Platform.MSBuild` that xunit brings in breaks the `dotnet test`
handshake. They are pinned level in `Directory.Packages.props`.

**Agents run over ACP, inside this process.** `AgentHost` launches the Claude
ACP bridge (`node acp/.../claude-agent-acp/dist/index.js`, installed by
`tools/vendor-acp.sh`) and every agent is a session on that one process. Closing
the app ends the agents; `agents.json` brings them back as stopped, and a message
resumes them. The bridge logs to stderr constantly, so its error stream must
always be drained, or the pipe fills and it blocks. See `docs/agent-control.md`.

**The dashboard does not perform file or terminal work for agents.** It
advertises no `fs` or `terminal` capability, so the agent uses its own tools. It
only answers `session/request_permission`. Do not add client capabilities without
reading what the agent will then route through us.

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

**SignalR caps a client-to-server message at 32 KB.** Saving from the editor
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

**The heavy components control their own rendering.** The layout re-renders every
second because the monitor publishes a snapshot every second. Re-rendering a diff
of a few thousand lines at that rate saturates the circuit and the page stops
answering clicks, so `DiffDocument`, `SourceControlPanel`, `FileDocument` and
`FileTreePanel` override `ShouldRender` and every handler calls `Touch()`. The
diff also only draws the lines of files near the screen: `app.js`
(`watchDiffWindow`) reports which files those are, and the rest are blocks the
height they measured at. The file trees draw only their visible rows through
`Virtualize`, which is why `.tree-row` has a pinned height.

**Vendored UMD scripts load through Monaco's `require`.** `vscode-textmate` and
`vscode-oniguruma` register as anonymous AMD modules when Monaco's loader is on
the page, so a script tag for either fails. `textmate.js` requires them. See
`docs/syntax.md`.

**The panels talk through `Workbench`, not parameters.** It is scoped (one per
window) and holds the agent the panels follow, the panels' state, the editor tabs
per worktree, and one `ChangesModel` per worktree, which Source control and the
Changes document share. A folded panel keeps its grid column at zero width
rather than leaving the grid, or every column after it shifts.

**The app stays minimal; features that are not about agents are extensions.**
Tests moved out for that reason. A new tab for one language or tool belongs in an
extension, and if the API cannot express it, the API grows (a minor version).

**Extension types must come from the app's copy.** `ExtensionLoadContext` sends
`AgentsDashboard.Extensions`, `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`
and `System.*` to the default context, and only falls back to the extension's
own copy when the app has none (Roslyn's `System.Composition`). Load a second copy of any of them and the
extension's `IComponent` is a different type from the app's, and nothing casts.
An extension project references the API with `Private="false"` for the same
reason.

**`@inject` in an extension resolves from the app's container.** Only the API
services are registered there. An extension's own services come from
`Context.Get<T>()`.

## Conventions

- Spaces, not tabs. No em dashes or emojis in UI copy or comments.
- Comments explain why, not what. Prefer a short paragraph on the non-obvious
  decision over a line-by-line narration.
- Nothing is ever written into `~/.claude`. The dashboard only reads it.
- Anything that edits the user's repository is offered, never done on its own.
- Don't commit unless asked.
