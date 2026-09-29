using Microsoft.AspNetCore.Components;

namespace Togue.Extensions;

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

/// <summary>
/// A base for a component added with <see cref="IExtensionBuilder.AddOverlay{TComponent}"/>.
/// Since API 1.10. It has no agent or worktree: it is about the whole window, and
/// reads what it needs from <see cref="ITogueView"/>.
/// </summary>
public abstract class OverlayBase : ComponentBase
{
    /// <summary>The extension's manifest and services.</summary>
    [CascadingParameter]
    public ExtensionContext Context { get; set; } = default!;
}

/// <summary>
/// The dialog a component is drawn in, for ending it. Since API 1.10.
/// </summary>
public abstract class DialogReference
{
    /// <summary>Closes the dialog; <see cref="IDialogs.ShowAsync{TComponent}"/> completes with <paramref name="value"/>.</summary>
    public abstract void Close(object? value = null);

    /// <summary>Closes the dialog as cancelled, as Escape does.</summary>
    public abstract void Cancel();
}

/// <summary>
/// A base for a component shown with <see cref="IDialogs.ShowAsync{TComponent}"/>.
/// Since API 1.10. Draw the body and the buttons; the app draws the frame and
/// the title. The app's classes <c>modal-body</c> and <c>modal-foot</c> lay
/// them out like its own dialogs.
/// </summary>
public abstract class DialogBase : ComponentBase
{
    /// <summary>The dialog this component is in.</summary>
    [CascadingParameter]
    public DialogReference Dialog { get; set; } = default!;

    /// <summary>The extension's manifest and services.</summary>
    [CascadingParameter]
    public ExtensionContext Context { get; set; } = default!;
}

/// <summary>
/// A base for a view added with <see cref="IExtensionBuilder.AddWorktreeView{TComponent}"/>.
/// Since API 1.4. The app sets <see cref="Worktree"/> on every render.
/// </summary>
/// <remarks>
/// A worktree outlasts its agents and can be opened with none, so a view about
/// what is on disk (tests, a build) is shown for the worktree rather than
/// waiting for an agent to be picked.
/// </remarks>
public abstract class WorktreeViewBase : ComponentBase
{
    /// <summary>The worktree in view.</summary>
    [Parameter, EditorRequired]
    public WorktreeContext Worktree { get; set; } = default!;

    /// <summary>
    /// The agent on screen when it works in <see cref="Worktree"/>, else null:
    /// none is picked, or the user picked another worktree in the title bar.
    /// </summary>
    [Parameter]
    public AgentContext? Agent { get; set; }

    /// <summary>False while another tab is in front.</summary>
    [Parameter]
    public bool IsVisible { get; set; }

    /// <summary>The extension's manifest and services.</summary>
    [CascadingParameter]
    public ExtensionContext Context { get; set; } = default!;
}

/// <summary>
/// A base for a component opened as an editor tab with
/// <see cref="IEditorTabs.OpenView{TComponent}"/>. Since API 1.11. The app sets
/// <see cref="Worktree"/> and <see cref="Agent"/> on every render, about once a
/// second, along with the parameters the view was opened with.
/// </summary>
/// <remarks>
/// A tab stays built while another is in front, so a view that draws a lot
/// should override <c>ShouldRender</c>, and one that runs timers should pause
/// them while <see cref="IsVisible"/> is false.
/// </remarks>
public abstract class EditorViewBase : ComponentBase
{
    /// <summary>
    /// The worktree whose tabs this view is among, or null for the tabs opened
    /// with no worktree in view.
    /// </summary>
    [Parameter]
    public WorktreeContext? Worktree { get; set; }

    /// <summary>The agent on screen when it works in <see cref="Worktree"/>, else null.</summary>
    [Parameter]
    public AgentContext? Agent { get; set; }

    /// <summary>False while another tab is in front.</summary>
    [Parameter]
    public bool IsVisible { get; set; }

    /// <summary>The extension's manifest and services.</summary>
    [CascadingParameter]
    public ExtensionContext Context { get; set; } = default!;
}
