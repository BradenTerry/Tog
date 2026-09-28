using Microsoft.AspNetCore.Components;

namespace AgentsDashboard.Extensions;

/// <summary>
/// The dashboard's current state. Available through <c>@inject</c> and to the
/// extension's own services.
/// </summary>
public interface IDashboardView
{
    /// <summary>The latest snapshot.</summary>
    DashboardView Current { get; }

    /// <summary>
    /// Raised on every monitor tick, about once a second, on the monitor's thread.
    /// A component that handles it must marshal with <c>InvokeAsync</c>.
    /// </summary>
    event Action? Changed;
}

/// <summary>
/// The editor of the window a view is drawn in. Since API 1.3. Scoped to the
/// window, so it is only there for a component's <c>@inject</c>, not for the
/// extension's own services, which are shared by every window.
/// </summary>
public interface IEditorTabs
{
    /// <summary>
    /// Opens a file as a tab beside the agent's files: inside the worktree in view
    /// as that file, anywhere else (a report the extension wrote to its data
    /// folder) as an outside file. Either way the user can edit and save it;
    /// the extension itself never saves through this.
    /// </summary>
    void OpenFile(string absolutePath, int? line = null);
}

/// <summary>Links into the app's own views.</summary>
public interface INavigation
{
    /// <summary>The address of a file in an agent's editor, at a line.</summary>
    string FileHref(string agentId, string relativePath, int? line = null);
}

/// <summary>
/// Turns text an agent or a tool wrote into runs, with the files it mentions as
/// links. What <see cref="LinkedText"/> draws.
/// </summary>
public interface ITextLinker
{
    /// <summary>The runs of <paramref name="text"/>, with links for files that exist in the worktree.</summary>
    IReadOnlyList<LinkedRun> Link(string text, string worktreePath, string agentId);
}

/// <summary>
/// A run of text. <paramref name="Href"/> opens it in the editor and
/// <paramref name="EditorHref"/> in VS Code; both are null for plain text.
/// </summary>
public sealed record LinkedRun(string Text, string? Href = null, string? EditorHref = null)
{
    /// <summary>The file's absolute path, for <see cref="IEditorTabs.OpenFile"/>. Since API 1.4.</summary>
    public string? Path { get; init; }

    /// <summary>The line the text points at, if it names one. Since API 1.4.</summary>
    public int? Line { get; init; }
}

/// <summary>A folder the extension may keep files in.</summary>
public interface IExtensionStorage
{
    /// <summary>Created on first use, and kept across reloads and restarts.</summary>
    string DataDirectory { get; }
}

/// <summary>
/// Offers the user a new agent. Since API 1.5. Scoped to the window, like
/// <see cref="IEditorTabs"/>, so only a view's <c>@inject</c> has it.
/// </summary>
/// <remarks>
/// Nothing starts here: the window's New agent dialog opens filled in with the
/// offer, and the user checks it and presses Start, or closes it. An extension
/// cannot start an agent on its own, from a worker or anywhere else, because
/// an agent can read and change the whole repository and run commands in it.
/// Call it from a click in a view, such as a "Start an agent" button beside a
/// ticket.
/// </remarks>
public interface IAgentOffers
{
    /// <summary>Opens the New agent dialog with the offer filled in, replacing whatever it held.</summary>
    void Offer(AgentOffer offer);

    /// <summary>
    /// <see cref="Offer"/>, and learn how it ended. Since API 1.7.
    /// </summary>
    /// <returns>
    /// The started agent's session id once the user presses Start and it starts,
    /// or null when the dialog is closed, a later offer replaces this one, or the
    /// window goes away. It can take as long as the user leaves the dialog open.
    /// </returns>
    Task<string?> OfferAsync(AgentOffer offer);
}

/// <summary>What the New agent dialog is filled in with. Anything left null keeps the dialog's own default.</summary>
/// <param name="Repository">The repository's root folder. One not in Settings is still offered.</param>
/// <param name="Worktree">An existing worktree of that repository to start in, or null for a new worktree.</param>
/// <param name="WorktreeName">The new worktree's name, such as a ticket key; its branch is named after it. Ignored with <paramref name="Worktree"/>.</param>
/// <param name="Prompt">The agent's first message, which the user can edit before starting it.</param>
public sealed record AgentOffer(
    string? Repository = null,
    string? Worktree = null,
    string? WorktreeName = null,
    string? Prompt = null);

/// <summary>
/// Dialogs over the window. Since API 1.10. Scoped to the window, like
/// <see cref="IEditorTabs"/>, so only a component's <c>@inject</c> has it: a
/// view's or an overlay's.
/// </summary>
/// <remarks>
/// The app draws the frame: the backdrop, the title, which extension is asking,
/// a close button, focus on open and Escape to cancel. The component draws the
/// body and its own buttons, and ends the dialog through
/// <see cref="DialogBase.Dialog"/>. One dialog shows at a time per window; a
/// second waits for the first. The app's own prompts (an agent asking to add
/// an extension, a secret) are drawn over any of these.
/// </remarks>
public interface IDialogs
{
    /// <summary>
    /// Shows <typeparamref name="TComponent"/> as a dialog, written against
    /// <see cref="DialogBase"/>, and completes when it closes.
    /// </summary>
    /// <param name="title">What the dialog's header says.</param>
    /// <param name="parameters">The component's parameters, by name. Only ones it declares.</param>
    /// <returns>
    /// What the component passed to <see cref="DialogReference.Close"/>, or
    /// <see cref="DialogResult.Cancelled"/> when it was cancelled: Escape, the
    /// close button, the window closing or the extension reloading.
    /// </returns>
    Task<DialogResult> ShowAsync<TComponent>(string title, IReadOnlyDictionary<string, object?>? parameters = null)
        where TComponent : IComponent;
}

/// <summary>How a dialog ended. Since API 1.10.</summary>
/// <param name="Cancelled">True unless the component closed it with a value.</param>
/// <param name="Value">What the component passed to <see cref="DialogReference.Close"/>.</param>
public sealed record DialogResult(bool Cancelled, object? Value = null);
