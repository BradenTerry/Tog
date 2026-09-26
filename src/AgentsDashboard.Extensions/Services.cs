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

/// <summary>Links into the app's own views.</summary>
public interface INavigation
{
    /// <summary>The address of a file in an agent's Files tab, at a line.</summary>
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
/// A run of text. <paramref name="Href"/> opens it in the Files tab and
/// <paramref name="EditorHref"/> in VS Code; both are null for plain text.
/// </summary>
public sealed record LinkedRun(string Text, string? Href = null, string? EditorHref = null);

/// <summary>A folder the extension may keep files in.</summary>
public interface IExtensionStorage
{
    /// <summary>Created on first use, and kept across reloads and restarts.</summary>
    string DataDirectory { get; }
}
