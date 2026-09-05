namespace AgentsDashboard.Core.Presentation;

/// <summary>A run of a line, with the class to colour it by. Null means plain.</summary>
public readonly record struct Segment(string Text, string? Css);

/// <summary>
/// Highlights the lines of one file, in order.
/// </summary>
/// <remarks>
/// Stateful on purpose: a block comment or a multi-line string only makes sense
/// as a continuation of the line before it, so lines have to be fed through in
/// the order they appear. <see cref="Reset"/> starts again, which is what a
/// diff does at each hunk, since a hunk begins after a gap in the file.
/// </remarks>
public sealed class LineHighlighter(string path)
{
    private static readonly IReadOnlyList<Segment> Empty = [];

    private SyntaxState _state;

    /// <summary>True when this file has nothing to colour, so callers can skip the work.</summary>
    public bool Enabled { get; } = Syntax.LanguageOf(path) != SyntaxLanguage.None;

    /// <summary>Begin again, forgetting any unterminated comment or string.</summary>
    public void Reset() => _state = default;

    /// <summary>
    /// The next line, split into coloured runs. A line with nothing to colour
    /// returns nothing, and the caller renders it as plain text.
    /// </summary>
    public IReadOnlyList<Segment> Next(string line)
    {
        if (!Enabled || line.Length == 0)
        {
            return Empty;
        }

        var (tokens, next) = Syntax.Tokenize(line, path, _state);
        _state = next;
        return tokens.Count == 0 ? Empty : Split(line, tokens);
    }

    /// <summary>
    /// Turns tokens into runs covering the whole line, filling the gaps between
    /// them with plain text. Overlapping tokens are dropped rather than nested:
    /// the highlighter is approximate, and one run per character keeps rendering
    /// simple and the output well formed.
    /// </summary>
    private static List<Segment> Split(string line, IReadOnlyList<Token> tokens)
    {
        var ordered = tokens.OrderBy(t => t.Start).ToList();
        var segments = new List<Segment>(ordered.Count * 2 + 1);
        var at = 0;

        foreach (var token in ordered)
        {
            if (token.Start < at || token.Start >= line.Length)
            {
                continue;
            }

            var end = Math.Min(token.Start + token.Length, line.Length);
            if (end <= token.Start)
            {
                continue;
            }

            if (token.Start > at)
            {
                segments.Add(new Segment(line[at..token.Start], null));
            }

            segments.Add(new Segment(line[token.Start..end], Syntax.CssClass(token.Kind)));
            at = end;
        }

        if (at < line.Length)
        {
            segments.Add(new Segment(line[at..], null));
        }

        return segments;
    }
}
