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
    /// Where it goes until the user moves it. Placement is the user's, not the
    /// extension's. Only <see cref="ViewLocation.AgentTab"/> exists in API 1.0.
    /// </param>
    /// <param name="order">Lower comes first, after the app's own tabs.</param>
    /// <param name="appliesTo">Hides the view for agents it has nothing for. Answer from memory: it runs on every render.</param>
    void AddView<TComponent>(
        string id,
        string title,
        ViewLocation defaultLocation = ViewLocation.AgentTab,
        int order = 100,
        Func<AgentContext, bool>? appliesTo = null)
        where TComponent : IComponent;

    /// <summary>Adds a small count or label shown on one of this extension's views.</summary>
    void AddIndicator<TProvider>(string viewId) where TProvider : class, IAgentIndicator;

    /// <summary>
    /// Adds background work that runs while the extension is enabled. An
    /// exception is logged and the worker restarted after a pause.
    /// </summary>
    void AddWorker<TWorker>(string id) where TWorker : class, IExtensionWorker;
}

/// <summary>
/// Where a view goes. The app draws the frame; the extension draws what is in it.
/// </summary>
/// <remarks>
/// A location is a place in the app's layout, not a kind of component, so a view
/// written for one works in any other, and the user can move it. New locations
/// (side and bottom panels) are added in minor versions.
/// </remarks>
public enum ViewLocation
{
    /// <summary>A tab in the agent view, after Chat, Changes and Files.</summary>
    AgentTab,
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
/// Supplies an indicator for an agent. Called on every render of the agent view,
/// so it must answer from memory: no IO, no git, no waiting.
/// </summary>
public interface IAgentIndicator
{
    /// <summary>The indicator to show, or null for none.</summary>
    Indicator? For(AgentContext agent);
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
