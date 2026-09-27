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
    /// Opens a file as a tab beside the agent's files. Inside the worktree in view
    /// it opens as that file, editable; anywhere else, such as a report the
    /// extension wrote to its data folder, read-only.
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
public sealed record LinkedRun(string Text, string? Href = null, string? EditorHref = null);

/// <summary>A folder the extension may keep files in.</summary>
public interface IExtensionStorage
{
    /// <summary>Created on first use, and kept across reloads and restarts.</summary>
    string DataDirectory { get; }
}
