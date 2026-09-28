using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace AgentsDashboard.Extensions;

/// <summary>
/// The entry point of an extension. The app finds the one public class in the
/// entry assembly that implements this, creates it, and calls
/// <see cref="Configure"/> once per load.
/// </summary>
public interface IDashboardExtension
{
    /// <summary>Registers what the extension adds and the services it needs.</summary>
    void Configure(IExtensionBuilder builder);
}

/// <summary>What an extension registers its contributions with.</summary>
public interface IExtensionBuilder
{
    /// <summary>The extension's manifest, as the app read it.</summary>
    ExtensionInfo Info { get; }

    /// <summary>
    /// The extension's own service container. Its components reach these through
    /// <see cref="ExtensionContext.Get{T}"/>, not <c>@inject</c>: <c>@inject</c>
    /// resolves from the app's container, which only has the API services.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>Adds a view: a component shown for an agent.</summary>
    /// <param name="id">Unique within the extension. Appears in the URL.</param>
    /// <param name="title">What the tab or panel header says.</param>
    /// <param name="defaultLocation">
    /// Which panel it is a tab in. The right panel, beside Source control, unless
    /// the extension says otherwise. The panels arrived in API 1.1; an extension
    /// that asks for one needs <c>"apiVersion": "1.1"</c> in its manifest.
    /// </param>
    /// <param name="order">Lower comes first, after the app's own tabs.</param>
    /// <param name="appliesTo">Hides the view for agents it has nothing for. Answer from memory: it runs on every render.</param>
    void AddView<TComponent>(
        string id,
        string title,
        ViewLocation defaultLocation = ViewLocation.RightPanel,
        int order = 100,
        Func<AgentContext, bool>? appliesTo = null)
        where TComponent : IComponent;

    /// <summary>
    /// Adds a view about a worktree rather than an agent: shown whenever a
    /// worktree is in view, with or without an agent picked, and following the
    /// worktree picked in the title bar. Write it against
    /// <see cref="WorktreeViewBase"/>. Since API 1.4.
    /// </summary>
    /// <param name="id">Unique within the extension. Appears in the URL.</param>
    /// <param name="title">What the tab or panel header says.</param>
    /// <param name="defaultLocation">Which panel it is a tab in.</param>
    /// <param name="order">Lower comes first, after the app's own tabs.</param>
    /// <param name="appliesTo">Hides the view for worktrees it has nothing for. Answer from memory: it runs on every render.</param>
    void AddWorktreeView<TComponent>(
        string id,
        string title,
        ViewLocation defaultLocation = ViewLocation.RightPanel,
        int order = 100,
        Func<WorktreeContext, bool>? appliesTo = null)
        where TComponent : IComponent;

    /// <summary>Adds a small count or label shown on one of this extension's views.</summary>
    void AddIndicator<TProvider>(string viewId) where TProvider : class, IAgentIndicator;

    /// <summary>
    /// Adds a small count or label shown on one of this extension's views,
    /// worked out from the worktree. Since API 1.4.
    /// </summary>
    void AddWorktreeIndicator<TProvider>(string viewId) where TProvider : class, IWorktreeIndicator;

    /// <summary>
    /// Adds background work that runs while the extension is enabled. An
    /// exception is logged and the worker restarted after a pause.
    /// </summary>
    void AddWorker<TWorker>(string id) where TWorker : class, IExtensionWorker;

    /// <summary>
    /// Adds navigation for a language in the editor: hover, go to definition,
    /// references, callers and colouring by symbol. Off in every worktree until
    /// the user presses Load on a file it handles, or opens one when the provider
    /// answers <see cref="ICodeIntelligence.LoadsOnOpen"/> with true.
    /// </summary>
    void AddCodeIntelligence<TProvider>() where TProvider : class, ICodeIntelligence;

    /// <summary>
    /// Adds a tool the agents the dashboard runs can call. Since API 1.2; an
    /// extension that adds one needs <c>"apiVersion": "1.2"</c> in its manifest.
    /// </summary>
    void AddAgentTool<TTool>() where TTool : class, IAgentTool;

    /// <summary>
    /// Adds a setting the user changes under this extension in Settings,
    /// Extensions. Read it through <see cref="IExtensionSettings"/>. Since API 1.9.
    /// </summary>
    void AddSetting(ExtensionSetting setting);

    /// <summary>
    /// Adds a component drawn once per window, over everything, for as long as
    /// the extension is loaded: a celebration, a toast, a banner. Write it
    /// against <see cref="OverlayBase"/>. Since API 1.10.
    /// </summary>
    /// <remarks>
    /// The layer it is drawn in lets clicks through, so an overlay never blocks
    /// the app by accident; give an element <c>pointer-events: auto</c> to make
    /// it clickable. The app does not re-render it: it re-renders itself, from
    /// <see cref="IDashboardView.Changed"/> or its own timers.
    /// </remarks>
    /// <param name="id">Unique within the extension.</param>
    void AddOverlay<TComponent>(string id) where TComponent : IComponent;
}

/// <summary>
/// Where a view goes. The app draws the frame; the extension draws what is in it.
/// </summary>
/// <remarks>
/// A location is a place in the app's layout, not a kind of component, so a view
/// written for one works in any other. New locations are added in minor versions.
/// </remarks>
public enum ViewLocation
{
    /// <summary>
    /// The agent view's tab strip in API 1.0. That strip is gone, and a view that
    /// still asks for it is shown in <see cref="RightPanel"/>.
    /// </summary>
    AgentTab,

    /// <summary>A tab in the left panel, after Files. Since API 1.1.</summary>
    LeftPanel,

    /// <summary>A tab in the right panel, after Source control. The default. Since API 1.1.</summary>
    RightPanel,

    /// <summary>A tab in the bottom panel, after Chat. Since API 1.1.</summary>
    BottomPanel,
}

/// <summary>An extension's manifest, as far as the extension needs it.</summary>
public sealed record ExtensionInfo(string Id, string Name, string Version, string Directory);

/// <summary>Background work owned by an extension.</summary>
public interface IExtensionWorker
{
    /// <summary>Runs until <paramref name="stopping"/> is cancelled.</summary>
    Task RunAsync(CancellationToken stopping);
}

/// <summary>
/// Supplies an indicator for an agent. Called on every render of the panel,
/// so it must answer from memory: no IO, no git, no waiting.
/// </summary>
public interface IAgentIndicator
{
    /// <summary>The indicator to show, or null for none.</summary>
    Indicator? For(AgentContext agent);
}

/// <summary>
/// Supplies an indicator for a worktree. Since API 1.4. Called on every render
/// of the panel, so it must answer from memory: no IO, no git, no waiting.
/// </summary>
public interface IWorktreeIndicator
{
    /// <summary>The indicator to show, or null for none.</summary>
    Indicator? For(WorktreeContext worktree);
}

/// <summary>A short count or label on a view's tab.</summary>
public sealed record Indicator(string Text, Tone Tone = Tone.Neutral, string? Tooltip = null);

/// <summary>How an indicator is coloured.</summary>
public enum Tone
{
    /// <summary>The page's own text colour.</summary>
    Neutral,

    /// <summary>The accent colour.</summary>
    Info,

    /// <summary>Green.</summary>
    Success,

    /// <summary>Amber.</summary>
    Warning,

    /// <summary>Red.</summary>
    Danger,
}
