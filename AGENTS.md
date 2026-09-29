# Agents Dashboard - project notes

A cross-platform Blazor Server desktop app (Photino window over a loopback host)
for watching Claude Code agents across git worktrees: who needs an answer, and
reviewing their diffs. Anything else is an extension.

- `src/AgentsDashboard.Core` - all logic, no ASP.NET dependency. Claude readers,
  git layer and parsers, the monitor loop, extension discovery.
- `src/AgentsDashboard.App` - Blazor Server UI, the Photino window, the
  extension host.
- `src/AgentsDashboard.Extensions` - the extension API. Versioned: 1.x only adds.
- `templates/extension` - the `dotnet new` template extensions start from.
- `tests/*` - xUnit v3 on Microsoft.Testing.Platform.

`dotnet build`, `dotnet test`, `dotnet run --project src/AgentsDashboard.App`.

## Documentation

`README.md` is the landing page for someone arriving on GitHub: what it is, why,
how to run it, two screenshots (`assets/screenshots`, taken of a sample
project by `tools/screenshots/take.mjs`, never by hand). `CONTRIBUTING.md` holds
building, testing and the architecture. The long rationale lives in `docs/`, one
file per subsystem that is easy to get wrong twice:

- `docs/extensions.md` - loading, the shared assemblies, reload, consent,
  the skill and the MCP tools that let an agent write one
- `docs/review.md` - Source control and diffs: always uncommitted, which sides
  each diff tab compares, the model URI query, restoring a blob's trailing
  newlines, push and pull counts
- `docs/agents.md` - work summaries from transcripts, subagents, notification rules
- `docs/syntax.md` - VS Code's TextMate grammars in Monaco, loaded through
  `textmate.js`
- `docs/agent-control.md` - ACP, the host, a turn, permissions, the bridge
- `docs/staging.md` - the two-character status field, unstaging with no HEAD
- `docs/editor.md` - Monaco in the editor, vendoring it, stamp-based saves,
  images, opening a file from outside the app
- `docs/workbench.md` - the VS Code-style layout, the panels, editor tabs, the
  shared ChangesModel, what is kept per machine
- `docs/code-intelligence.md` - the `ICodeIntelligence` extension point, the C#
  extension's Roslyn load (opt-in per worktree), MSBuild in a load context, why
  references land in a panel rather than a peek widget
- `docs/release.md` - build from source, no binaries: the version from git
  tags, the lock files, the pinned actions, and what a security review asks

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
tests with exit code 5. Never pass it.

**xunit.v3 needs `UseMicrosoftTestingPlatformRunner`.** Without it the generated
entry point is the console runner, the TRX extension is never registered, and
`--report-trx` is an unknown option.

**MTP package versions must agree.** `Microsoft.Testing.Platform` newer than the
`Microsoft.Testing.Platform.MSBuild` that xunit brings in breaks the `dotnet test`
handshake. They are pinned level in `Directory.Packages.props`.

**Agents run over ACP, inside this process.** `AgentHost` launches the Claude
ACP bridge (`node acp/.../claude-agent-acp/dist/index.js`, installed by
`tools/vendor-acp.mjs`) and every agent is a session on that one process. Closing
the app ends the agents; `agents.json` brings them back as stopped, and a message
resumes them. The bridge logs to stderr constantly, so its error stream must
always be drained, or the pipe fills and it blocks. See `docs/agent-control.md`.

**The dashboard does not perform file or terminal work for agents.** It
advertises no `fs` or `terminal` capability, so the agent uses its own tools. It
answers `session/request_permission` and, since it advertises form elicitation,
`elicitation/create`: Claude's AskUserQuestion, an MCP server's form, and the
bridge's model-retry prompt, told apart on the card. Do not add client
capabilities without reading what the agent will then route through us.

**Agents get the dashboard's own MCP server.** Every session is handed
`agents-dashboard`, which serves the tools extensions add with `AddAgentTool`
(API 1.2). The key is never in the server entry, which ends up on the CLI's
command line: a header names `${AGENTS_DASHBOARD_MCP_KEY}` and the value is only
in that session's CLI environment, sent in `session/new`'s `_meta`. Each session
has its own key, and the key alone says which agent and worktree a call is
from; never trust anything the caller puts in the request for that. The one
exception is the terminal key in `mcp-link.json`, used by the stdio bridge
(`agents-dashboard mcp`) that Settings adds to Claude for terminal agents: it
takes its folder from a header and has no agent id. Nothing may write to stdout
in the `mcp` path of `Program.cs`, it is the MCP channel. See
`docs/extensions.md`.

**Pages need this start's key.** `UiAccess` refuses every request but
`/_mcp` without a cookie it sets when the address carries `?ui-key=`, which
only the window and the `--browser` printout have. The port is in
`mcp-link.json`, so without it an agent could drive this window's app in a
headless browser and approve its own requests. It does not stop an agent
starting a second copy of the app and driving that; see `docs/extensions.md`.
A script or test that loads a page has to start from the printed address.
`DesktopWindow` sets Photino's log verbosity to 0, since at its default it
prints `Load(url)`, key included, to stdout.

**Secrets never reach an agent.** Extensions ask `ISecrets` (API 1.8) only for
the needs their `extension.json` declares (1.12), each bound by the user to a
stored secret per build; values live in the OS store (`ISecretVault`), names and grants in
`secrets.json`, never `settings.json`. No agent tool serves them, no session's
environment carries them, and `SecretBroker.ForAgent` wraps every MCP
request, so the broker and the OS store itself throw on any secret read or
write made while answering one. See `docs/extensions.md`.

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
a second registration answers every hover twice. A diff tab's model is the
file's URI plus a query (`?diff` or `?staged`), since it can be open beside the
file's own tab and two models cannot share a URI; F12 from it lands on the
plain URI and so opens the file tab.

**The heavy components control their own rendering.** The layout re-renders every
second because the monitor publishes a snapshot every second. Re-rendering a
tree of thousands of rows at that rate saturates the circuit and the page stops
answering clicks, so `SourceControlPanel`, `FileDocument` and `FileTreePanel`
override `ShouldRender` and every handler calls `Touch()`. The file trees draw
only their visible rows through
`Virtualize`, which is why `.tree-row` has a pinned height.

**Never start a `FileSystemWatcher` on macOS.** .NET's watcher calls `sync()`
as it starts, which waits for every disk on the machine to flush: seconds while
agents are building. Watch through `PathWatcher` in Core, which talks to FSEvents
directly. For the same reason `Program.cs` switches off the host builder's
appsettings reload watcher, which was most of the app's startup time.

**Vendored UMD scripts load through Monaco's `require`.** `vscode-textmate` and
`vscode-oniguruma` register as anonymous AMD modules when Monaco's loader is on
the page, so a script tag for either fails. `textmate.js` requires them. See
`docs/syntax.md`.

**The panels talk through `Workbench`, not parameters.** It is scoped (one per
window) and holds the agent the panels follow, the panels' state, the editor tabs
per worktree, and one `ChangesModel` per worktree, which Source control and the
open diff tabs share. A folded panel keeps its grid column at zero width
rather than leaving the grid, or every column after it shifts.

**The app stays minimal; features that are not about agents are extensions.**
Extensions live outside this repository. Tests moved out for that reason. A new tab for one language or tool belongs in an
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
- Nothing the dashboard or an agent does on its own writes into `~/.claude`;
  the app only reads it. You can still open and edit a file there yourself,
  and it is saved when you press Save, like any file. The only changes the app
  makes to Claude's config are the extension skill and the MCP server entry
  (through `claude mcp`), each from its button in Settings.
- Anything that edits the user's repository is offered, never done on its own.
- Don't commit unless asked.
