using Microsoft.AspNetCore.Components;

namespace AgentsDashboard.Extensions;

/// <summary>
/// Cascaded to every component an extension contributes: its manifest and its
/// own services.
/// </summary>
public sealed class ExtensionContext(ExtensionInfo info, IServiceProvider services)
{
    /// <summary>The extension's manifest.</summary>
    public ExtensionInfo Info { get; } = info;

    /// <summary>A service from the extension's own container.</summary>
    public T Get<T>() where T : notnull =>
        (T)(services.GetService(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not registered by {Info.Id}."));
}

/// <summary>
/// A base for a view. The app sets <see cref="Agent"/> on every render, which
/// happens about once a second while the agent is on screen.
/// </summary>
/// <remarks>
/// Views stay built while another tab is in front, so they are re-rendered with
/// the page. A view that draws a lot should override <c>ShouldRender</c>.
/// </remarks>
public abstract class AgentViewBase : ComponentBase
{
    /// <summary>The agent this view is shown for.</summary>
    [Parameter, EditorRequired]
    public AgentContext Agent { get; set; } = default!;

    /// <summary>False while another tab is in front.</summary>
    [Parameter]
    public bool IsVisible { get; set; }

    /// <summary>The extension's manifest and services.</summary>
    [CascadingParameter]
    public ExtensionContext Context { get; set; } = default!;
}
