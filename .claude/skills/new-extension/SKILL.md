---
name: new-extension
description: Create a new Agents Dashboard extension (a Razor project the running app loads at runtime and shows as a tab, an indicator, a background worker or editor code intelligence), scaffold it from templates/extension, build it, and link it into the app. Use when the user asks to create, scaffold, start, add or write an extension, plugin, new tab or new panel for the dashboard, or asks for a feature that is not about agents (a language, a tool, a test runner, a build status), since those belong in an extension rather than the app.
---

# Create a dashboard extension

An extension is a folder with an `extension.json` and a Razor class library
built into `bin/dashboard/`. The app loads it into its own load context and
reloads it on every build. `docs/extensions.md` is the reference; read it before
doing anything unusual. The template's `AGENTS.md` is the API on one page.

## 1. Decide what and where

From the request, settle:

- **Name** in PascalCase (`BuildStatus`). The template derives the id from it
  in lower case (`buildstatus`); change it in `extension.json` to kebab case if
  that reads better (`build-status`). The id appears in URLs and in
  `/_ext/<id>/`.
- **What it contributes**: a view (`AddView`), a count on its tab
  (`AddIndicator`), background work (`AddWorker`), editor navigation for a
  language (`AddCodeIntelligence`, see `docs/code-intelligence.md`).
- **Which panel** the view is a tab in: `RightPanel` (default, beside Source
  control), `LeftPanel` (beside Files), `BottomPanel` (beside Chat).
- **Where the project lives**: outside this repository, never in it. A folder
  the user names, or their extension folder
  (`agents-dashboard-extensions`). It compiles against the SDK the app copies
  to `~/.agents-dashboard/sdk/<major>.<minor>/`.

Ask only if the request leaves the contribution or the location genuinely
unclear. Do not ask about the name; pick one.

## 2. Scaffold from the template

Install the template from this checkout (reinstalling is harmless and picks up
template changes), then create the project:

```
dotnet new install templates/extension --force
dotnet new agents-dashboard-extension -n <Name> -o <path>/<Name>
```

That gives `extension.json`, `<Name>.csproj`, `<Name>Extension.cs`,
`<Name>View.razor`, `_Imports.razor`, `assets/extension.css`, and `AGENTS.md`
plus a one-line `CLAUDE.md` for whoever works on it later.

## 3. Fix the API version

The template targets API 1.0. The app's current API is
`ExtensionManifests.Api` in
`src/AgentsDashboard.Core/Extensions/ExtensionManifest.cs`; the SDK for
it is in `~/.agents-dashboard/sdk/` under the newest folder there (run the app
once if the folder is missing).

- If the extension names a panel (`LeftPanel`, `RightPanel`, `BottomPanel`) or
  uses anything else added after 1.0 (`AddCodeIntelligence`), set
  `"apiVersion"` in `extension.json` to that version, and point the csproj's
  `AgentsDashboardSdk` default at the matching `sdk/<major>.<minor>` folder.
  Compiling against `sdk/1.0` fails on those members.
- Never set `apiVersion` newer than the app's: it will not load.

## 4. Write it

Follow the template's `AGENTS.md` and the repository's conventions. The things
that break an extension, in the order they bite:

- **Exactly one public `IDashboardExtension`** in the entry assembly.
- **Own services through `Context.Get<T>()`**, registered on `b.Services`.
  `@inject` resolves from the app's container, which has only the API services
  (`IDashboardView`, `INavigation`, `ITextLinker`, `IExtensionStorage`,
  `ILogger<T>`).
- **Never ship a shared assembly.** The API reference stays `Private="false"`,
  and do not add packages for `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`
  or `System.*` the shared framework already has. A second copy makes the
  extension's `IComponent` a different type and nothing casts.
- **`appliesTo` and `IAgentIndicator.For` run on every render.** Answer from
  memory. Do IO in a worker or a service and cache the result.
- **The view re-renders about once a second.** Anything that draws more than a
  card overrides `ShouldRender`. `IDashboardView.Changed` fires on the monitor
  thread; marshal with `InvokeAsync`.
- **Workers stop when `stopping` is cancelled.** A reload waits for them. Never
  start a bare thread: an exception on it ends the app.
- **Nothing written into `~/.claude`.** Anything that changes the user's
  repository is a button they press. Data goes in
  `IExtensionStorage.DataDirectory`.
- **No `Microsoft.Build.Locator` registration**: the C# extension owns it.
- **Look native**: the dashboard's classes and CSS variables listed in the
  template's `AGENTS.md`. Own styles go in `assets/extension.css`, prefixed
  with the extension id.

Keep the template's comments and structure; replace the placeholder view rather
than growing it. Set a real `description` in `extension.json`.

## 5. Build and load

```
dotnet build <path>/<Name>
```

It must build with no errors and put `<Name>.dll` in `bin/dashboard/`, matching
`entry` and `output` in the manifest. Then tell the user how to load it; do not
change their settings yourself:

- If it was created inside a folder the user added with `/*` (Settings,
  Extensions, Extension folders), it is already found and loads on this build.
- Otherwise Settings, Extensions, Extension folders, Choose folder... and pick
  it, or type its path and Add. Every later build reloads the tab in the open
  window.
- Or start the app with `dotnet run --project src/AgentsDashboard.App -- --extension <path>/<Name>`.

If the user wants to see it running, use the `run` skill with `--extension`.

## 6. Tests, if it has logic

For an extension with logic beyond drawing, add a `<Name>.Tests` project
beside it (xUnit v3 on Microsoft.Testing.Platform, with
`UseMicrosoftTestingPlatformRunner`), and `InternalsVisibleTo` in the
extension's csproj. Run with `dotnet test`, never with `--nologo`.

## Report

The project path, what it registers (views with their panels, indicators,
workers), the `apiVersion`, that it built, and how to link it.
