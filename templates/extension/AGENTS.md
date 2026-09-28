# An Agents Dashboard extension

This project adds a view to the Agents Dashboard: a tab in each agent's view,
next to Chat, Changes and Files. The dashboard loads it at runtime; it is not
part of the dashboard's source.

## The loop

1. `dotnet build` (or `dotnet watch build`). Output goes to `bin/dashboard/`.
2. Once: in the dashboard, Settings, Extensions, Extension folders, Choose
   folder..., and pick this folder. If it sits in a folder added there with
   `/*` it is already found. Or start the dashboard with
   `--extension <this folder>`.
3. Every later build is picked up by the running dashboard and the tab reloads.
   Nothing needs restarting. Build errors show in the terminal; load errors show
   on the Settings card; a view that throws shows its error in the tab with Retry.

## The API

Everything comes from `AgentsDashboard.Extensions` (API 1.0), referenced from
`~/.agents-dashboard/sdk/1.0/` with its XML docs. Nothing else from the
dashboard is available, on purpose.

- `IDashboardExtension.Configure(IExtensionBuilder b)` is the entry point. Keep
  exactly one public class implementing it.
- `b.AddView<TComponent>(id, title, defaultLocation:, order:, appliesTo:)` adds a
  tab to a panel: `ViewLocation.RightPanel` (the default, beside Source control),
  `LeftPanel` (beside Files) or `BottomPanel` (beside Chat). Naming a panel needs
  `"apiVersion": "1.1"` in `extension.json`. `appliesTo` runs on every render:
  answer from memory, no IO.
- `b.AddWorktreeView<TComponent>(...)` (API 1.4) adds a tab about the worktree
  in view rather than an agent, shown with no agent picked too. Use it for
  anything about what is on disk (tests, a build). Its `appliesTo` takes a
  `WorktreeContext`.
- `b.AddIndicator<T>(viewId)` puts a short count on the tab. `IAgentIndicator.For`
  also runs on every render. `b.AddWorktreeIndicator<T>(viewId)` (API 1.4) is
  the same with `IWorktreeIndicator.For(WorktreeContext)`.
- `b.AddWorker<T>(id)` runs `IExtensionWorker.RunAsync(stopping)` in the
  background while the extension is loaded, restarted with a pause if it throws.
  Stop when `stopping` is cancelled: a reload waits for it.
- `b.AddCodeIntelligence<T>()` gives the editor navigation for a language:
  `ICodeIntelligence` answers hover, definition, references, callers and
  colouring by symbol for the files its `Handles` accepts, in Monaco's
  `Language`. The app draws the chip, its Load button and the results. Load
  nothing until `LoadAsync`; a query must never start a load. Positions are
  one-based.
- `b.AddAgentTool<T>()` (API 1.2) gives the agents the dashboard runs a tool:
  `IAgentTool` has a `Name` (lower case, prefixed, e.g. `tests_run`), a
  `Description` written for the agent, an `InputSchema` (JSON Schema text) and
  `CallAsync(AgentToolCall, ct)`, which gets the arguments, the agent's
  folder and (API 1.6) its session as `AgentId`, both vouched for by the app,
  and returns text. Start long work and return; do not hold the call.
- `b.Services` is the extension's own DI container. Views reach it through
  `Context.Get<T>()`, not `@inject`: `@inject` resolves from the dashboard's
  container, which only has the API services below.

A view inherits `AgentViewBase` and gets `Agent` (an `AgentContext`: id, label,
state, and its `Worktree` with path, branch, repo and git counts), `IsVisible`,
and `Context`. A worktree view inherits `WorktreeViewBase` and gets `Worktree`,
`Agent` (null unless the agent on screen works in that worktree), `IsVisible`
and `Context`; pass `<LinkedText Text="..." Worktree="Worktree" />` there.

Services available to `@inject` and to your own services' constructors:
`IDashboardView` (every worktree, with git counts only for the one on screen, and a `Changed` event about once a second, on
a background thread: use `InvokeAsync`), `INavigation` (a link that opens a file in the
agent's editor), `ITextLinker` and the `<LinkedText Text="..." Agent="Agent" />`
component (paths in text become links), `IExtensionStorage` (a data folder of
your own), and `ILogger<T>`. In a view only, `IEditorTabs` (API 1.3) opens a
file as a tab in that window's editor, such as a long report you wrote to your
data folder, and `IAgentOffers` (API 1.5) opens the New agent dialog filled
in with an `AgentOffer` (repository, worktree or new worktree name, prompt).
It never starts the agent: the user presses Start. Call it from a click.
`OfferAsync` (API 1.7) also returns the started session id, or null if none
was started.

`ISecrets` (API 1.8) is only in your own container: a service's constructor
or `Context.Get<ISecrets>()` in a view, never `@inject`. Ask for a secret by
name (`github`, `jira`, `linear`); the user approves your extension, per
build, before it gets anything, and the call waits until they answer, so pass
a cancellation token. Prefer `SendAsync(name, request, SecretAuth.Bearer)`:
the dashboard sends the https request with the secret added and you never hold
it. `GetAsync(name)` hands you the value, for a library that wants it. Never
give a secret to an agent: not in a tool result, a prompt or a file. A secret
asked for while answering an agent tool call throws.

## Rules

- The view re-renders about once a second, because the page does. If it draws a
  lot, override `ShouldRender`.
- Never write into `~/.claude`.
- Anything that changes the user's repository must be a button the user clicks,
  never done on its own.
- Do not register MSBuild through `Microsoft.Build.Locator`: the C# navigation
  extension does, MSBuild can only be registered once per process, and the two
  would collide.

## Looking native

Use the dashboard's classes and it will look like part of the app, light and
dark: `card`, `card-head`, `card-body`, `row`, `stack`, `inline`, `spacer`,
`chip` (`active`, `danger`, `accent`), `banner` (`info`, `danger`), `faint`,
`muted`, `mono`, `truncate`, `empty`, and buttons with `primary`, `ghost`,
`small`. Colours as CSS variables: `--text`, `--text-dim`, `--text-faint`,
`--surface`, `--surface-2`, `--border`, `--accent`, `--active`, `--danger`,
`--waiting`. Your own styles go in `assets/extension.css`.

The dashboard's own Tests tab is an extension built this way:
`extensions/DotnetTests` in the dashboard repository.
