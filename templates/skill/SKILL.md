---
name: agents-dashboard-extension
description: Create a new Agents Dashboard extension (a Razor project the running dashboard loads at runtime and shows as a tab, an indicator, a background worker, an agent tool or editor code intelligence), scaffold it from the template the dashboard ships, build it, and tell the user how to turn it on. Use when the user asks to create, scaffold, start, add or write an extension, plugin, new tab or new panel for the Agents Dashboard, or asks for a dashboard feature that is not about agents (a language, a tool, a test runner, a build status).
---

# Create an Agents Dashboard extension

An extension is a folder with an `extension.json` and a Razor class library
built into `bin/dashboard/`. The dashboard loads it into its own load context
and reloads it on every build. The template's `AGENTS.md` is the whole API on
one page; read it once the project exists.

This copy was written by the dashboard for API {{api}}. What it installed:

- The API to compile against: `{{sdk}}`
- The project template: `{{template}}`

Both are refreshed every time the dashboard starts. If either is missing, the
dashboard has not run since it was updated; ask the user to open it once.

## 1. Decide what and where

From the request, settle:

- **Name** in PascalCase (`BuildStatus`). The template derives the id from it
  in lower case (`buildstatus`); change it in `extension.json` to kebab case if
  that reads better (`build-status`). The id appears in URLs and in
  `/_ext/<id>/`.
- **What it contributes**: a view about an agent (`AddView`) or about the
  worktree on screen (`AddWorktreeView`), a count on its tab (`AddIndicator`,
  `AddWorktreeIndicator`), background work (`AddWorker`), a tool the
  dashboard's agents can call (`AddAgentTool`), editor navigation for a
  language (`AddCodeIntelligence`).
- **Which panel** a view is a tab in: `RightPanel` (default, beside Source
  control), `LeftPanel` (beside Files), `BottomPanel` (beside Chat).
- **Where the project lives**: the folder the user names, or a new folder in
  the current working directory. Ask only if the request leaves the
  contribution or the location genuinely unclear. Do not ask about the name;
  pick one.

## 2. Scaffold from the template

Install the template (reinstalling is harmless and picks up a newer one), then
create the project:

```
dotnet new install "{{template}}" --force
dotnet new agents-dashboard-extension -n <Name> -o <path>/<Name>
```

That gives `extension.json`, `<Name>.csproj`, `<Name>Extension.cs`,
`<Name>View.razor`, `_Imports.razor`, `assets/extension.css`, and `AGENTS.md`
plus a one-line `CLAUDE.md` for whoever works on it later.

## 3. Set the API version

The template targets API 1.0, which every dashboard since 1.0 loads. If the
extension uses anything added later (naming a panel is 1.1, `AddAgentTool`
1.2, `IEditorTabs` 1.3, `AddWorktreeView` 1.4, `IAgentOffers` 1.5, `OfferAsync` 1.7; the template's `AGENTS.md`
marks each), set `"apiVersion"` in `extension.json` to `{{api}}` and point the
csproj's `AgentsDashboardSdk` default at `{{sdk}}`. Compiling against the 1.0
SDK fails on those members. Never set `apiVersion` newer than {{api}}: this
dashboard will not load it.

## 4. Write it

Follow the template's `AGENTS.md`. The things that break an extension, in the
order they bite:

- **Exactly one public `IDashboardExtension`** in the entry assembly.
- **Own services through `Context.Get<T>()`**, registered on `b.Services`.
  `@inject` resolves from the dashboard's container, which has only the API
  services (`IDashboardView`, `INavigation`, `ITextLinker`,
  `IExtensionStorage`, `ILogger<T>`, and `IEditorTabs` and `IAgentOffers` in a view).
- **Never ship a shared assembly.** The API reference stays `Private="false"`,
  and do not add packages for `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`
  or `System.*` the shared framework already has. A second copy makes the
  extension's `IComponent` a different type from the dashboard's and nothing
  casts.
- **`appliesTo` and an indicator's `For` run on every render.** Answer from
  memory. Do IO in a worker or a service and cache the result.
- **The view re-renders about once a second.** Anything that draws more than a
  card overrides `ShouldRender`. `IDashboardView.Changed` fires on a background
  thread; marshal with `InvokeAsync`.
- **Workers stop when `stopping` is cancelled.** A reload waits for them. Never
  start a bare thread: an exception on it ends the dashboard.
- **Agent tools answer quickly.** Start long work and return, with a second
  tool to ask how it is going.
- **Nothing written into `~/.claude`.** Anything that changes the user's
  repository is a button they press. Data goes in
  `IExtensionStorage.DataDirectory`.
- **No `Microsoft.Build.Locator` registration**: the dashboard's C# extension
  owns it, and MSBuild registers once per process.
- **Look native**: the dashboard's classes and CSS variables listed in the
  template's `AGENTS.md`. Own styles go in `assets/extension.css`, prefixed
  with the extension id.

Keep the template's comments and structure; replace the placeholder view rather
than growing it. Set a real `description` in `extension.json`.

## 5. Build and turn it on

```
dotnet build <path>/<Name>
```

It must build with no errors and put `<Name>.dll` in `bin/dashboard/`, matching
`entry` and `output` in the manifest. Then tell the user how to load it; do not
change the dashboard's settings yourself:

- If it was created inside a folder the user added with `/*` (Settings,
  Extensions, Extension folders), it is already found and loads on this build.
- Otherwise Settings, Extensions, Extension folders, Choose folder... and pick
  it, or type its path and Add. Every later build reloads the tab in the open
  window.

## Report

The project path, what it registers (views with their panels, indicators,
workers, tools), the `apiVersion`, that it built, and how to turn it on.
