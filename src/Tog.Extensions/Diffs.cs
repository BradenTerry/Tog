namespace Tog.Extensions;

/// <summary>Which side of a diff a line is on. Since API 1.11.</summary>
public enum DiffSide
{
    /// <summary>The left side: the text before, such as HEAD's.</summary>
    Original,

    /// <summary>The right side: the text after, such as the file on disk.</summary>
    Modified,
}

/// <summary>
/// Lines of one side of a diff, from <paramref name="Start"/> to
/// <paramref name="End"/>, both counted from 1 and both included. Since API 1.11.
/// </summary>
public sealed record DiffLines(DiffSide Side, int Start, int End)
{
    /// <summary>One line.</summary>
    public static DiffLines Line(DiffSide side, int line) => new(side, line, line);
}

/// <summary>
/// Lines a <see cref="DiffView"/> marks, such as ones with a comment on them:
/// tinted, with a mark in the gutter. Since API 1.11.
/// </summary>
/// <param name="Lines">The lines.</param>
/// <param name="Tooltip">Shown on hovering the gutter mark.</param>
public sealed record DiffMark(DiffLines Lines, string? Tooltip = null);

/// <summary>
/// The app's half of <see cref="DiffView"/>: the component that draws it with
/// the editor's own diff. Since API 1.11. Implemented by the app; an extension
/// uses <see cref="DiffView"/> and never this.
/// </summary>
public interface IDiffViewHost
{
    /// <summary>The app's component, handed the <see cref="DiffView"/> as its <c>Source</c> parameter.</summary>
    Type Component { get; }
}
