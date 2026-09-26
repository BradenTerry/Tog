namespace AgentsDashboard.Core.Code;

/// <summary>A span of source the editor can jump to.</summary>
/// <remarks>
/// Positions are one-based in both line and column, which is Monaco's convention.
/// Roslyn counts from zero, and the whole conversion happens in one place inside
/// <see cref="CodeQueries"/>: an off-by-one here is invisible everywhere except
/// "go to definition lands on the line above".
/// </remarks>
/// <param name="Path">Worktree-relative path with forward slashes.</param>
/// <param name="Preview">The trimmed text of the line, for a results panel.</param>
/// <param name="Container">Containing type and member, such as "ClaudeCli.SendAsync".</param>
public sealed record CodeLocation(
    string Path,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Preview,
    string? Container);

/// <summary>What the editor shows when the pointer rests on a symbol.</summary>
/// <param name="Signature">The symbol as source would write it, minimally qualified.</param>
/// <param name="Summary">The XML doc summary, tags stripped and whitespace collapsed.</param>
public sealed record HoverInfo(string Signature, string? Summary);

/// <summary>One end of a call relationship: a symbol and where the calls are.</summary>
/// <param name="Location">Where the symbol itself is declared.</param>
/// <param name="CallSites">The individual calls, in the caller's file.</param>
public sealed record CallHierarchyItem(
    string Name,
    string? Container,
    CodeLocation Location,
    IReadOnlyList<CodeLocation> CallSites);

/// <summary>Who calls the method under the caret, and what it calls.</summary>
public sealed record CallHierarchy(
    CodeLocation Target,
    IReadOnlyList<CallHierarchyItem> Callers,
    IReadOnlyList<CallHierarchyItem> Callees);

/// <summary>Where a worktree's solution is in its one-time load.</summary>
public enum LoadState
{
    NotLoaded,
    Loading,
    Ready,
    Failed,
}

/// <summary>The chip the editor shows for a worktree's solution.</summary>
/// <param name="Message">Progress while loading, the reason when failed, else null.</param>
public sealed record LoadStatus(LoadState State, string? Message, int Projects, int Documents);
