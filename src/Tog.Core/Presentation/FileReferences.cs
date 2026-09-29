using Tog.Core.Repos;
using System.Text.RegularExpressions;

namespace Tog.Core.Presentation;

/// <summary>One path mentioned in a piece of text.</summary>
/// <param name="Start">Index of the reference in the original text.</param>
/// <param name="Length">Length of the reference in the original text.</param>
/// <param name="Path">Worktree-relative path with forward slashes.</param>
/// <param name="Line">One-based line, when the reference named one.</param>
/// <param name="Column">One-based column, when the reference named one.</param>
public sealed record FileReference(int Start, int Length, string Path, int? Line, int? Column);

/// <summary>A run of text, either plain or standing for a reference.</summary>
public sealed record TextSegment(string Text, FileReference? Reference);

/// <summary>
/// Finding the files an agent mentions in prose, so they can be opened.
/// </summary>
/// <remarks>
/// The hard part is not matching paths, it is not matching everything else. Agent
/// replies and stack traces are full of colons and dotted words, so a candidate is
/// only kept when the caller says the file actually exists in the worktree: that
/// is what keeps "note: see below" and "version 1.2" out of the links, and it is
/// cheaper than trying to write a pattern that is right about English.
///
/// The patterns run with <see cref="RegexOptions.None"/> and a match timeout on
/// purpose. This runs over whole transcripts and stack traces, which can be very
/// long, and a backtracking pattern that is merely slow on a big input is a hang.
/// </remarks>
public static class FileReferences
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The path part of a candidate: no whitespace, no quotes, no brackets. A
    /// colon only as a Windows drive (<c>C:\</c>), since everywhere else it is what
    /// separates a path from its line.
    /// </summary>
    private const string PathChars = @"(?:\b[A-Za-z]:(?=[\\/]))?[^\s""'`<>|*?:()\[\],;]+";

    /// <summary>A .NET stack trace frame: "in /abs/path/File.cs:line 42".</summary>
    private static readonly Regex StackFrame = new(
        @"\bin\s+(?<path>" + PathChars + @"):line\s+(?<line>\d+)",
        RegexOptions.None,
        Timeout);

    /// <summary>MSBuild and compiler style: "path(12,5)" or "path(12)".</summary>
    private static readonly Regex Parenthesised = new(
        @"(?<path>" + PathChars + @")\((?<line>\d+)(?:,(?<col>\d+))?\)",
        RegexOptions.None,
        Timeout);

    /// <summary>Editor style: "path:12" or "path:12:5".</summary>
    private static readonly Regex Colon = new(
        @"(?<path>" + PathChars + @"):(?<line>\d+)(?::(?<col>\d+))?",
        RegexOptions.None,
        Timeout);

    /// <summary>A path on its own, recognised by a slash or an extension.</summary>
    private static readonly Regex Bare = new(
        @"(?<path>" + PathChars + @")",
        RegexOptions.None,
        Timeout);

    /// <summary>
    /// Every file reference in <paramref name="text"/> that <paramref name="exists"/>
    /// accepts, sorted by position and never overlapping.
    /// </summary>
    /// <param name="exists">Answers whether a worktree-relative path is a real file.</param>
    public static IReadOnlyList<FileReference> Find(
        string text,
        string worktreePath,
        Func<string, bool> exists)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var found = new List<FileReference>();

        // Longest-winning order: a frame beats the colon form inside it, and the
        // bare path is only ever the fallback for what none of the others claimed.
        Collect(StackFrame, text, worktreePath, exists, found, wantLine: true);
        Collect(Parenthesised, text, worktreePath, exists, found, wantLine: true);
        Collect(Colon, text, worktreePath, exists, found, wantLine: true);
        Collect(Bare, text, worktreePath, exists, found, wantLine: false);

        found.Sort((a, b) => a.Start != b.Start
            ? a.Start.CompareTo(b.Start)
            : b.Length.CompareTo(a.Length));

        var kept = new List<FileReference>(found.Count);
        var end = 0;

        foreach (var r in found)
        {
            if (r.Start >= end)
            {
                kept.Add(r);
                end = r.Start + r.Length;
            }
        }

        return kept;
    }

    private static void Collect(
        Regex pattern,
        string text,
        string worktreePath,
        Func<string, bool> exists,
        List<FileReference> found,
        bool wantLine)
    {
        Match match;

        try
        {
            match = pattern.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return;
        }

        while (match.Success)
        {
            var path = match.Groups["path"];
            var trimmed = Trim(path.Value);

            if (trimmed.Length > 0)
            {
                var relative = Relative(trimmed, worktreePath);

                if (relative is not null && Plausible(relative) && exists(relative))
                {
                    var start = path.Index;
                    var length = wantLine
                        ? match.Index + match.Length - start
                        : trimmed.Length;

                    found.Add(new FileReference(
                        start,
                        length,
                        relative,
                        wantLine ? Number(match, "line") : null,
                        wantLine ? Number(match, "col") : null));
                }
            }

            try
            {
                match = match.NextMatch();
            }
            catch (RegexMatchTimeoutException)
            {
                return;
            }
        }
    }

    private static int? Number(Match match, string group) =>
        match.Groups[group].Success && int.TryParse(match.Groups[group].Value, out var n) ? n : null;

    /// <summary>Punctuation that ends an English sentence is not part of a path.</summary>
    /// <remarks>
    /// Only the end is trimmed: a leading dot is the "./" of a relative path, and
    /// stripping it would turn a path into one that starts at the filesystem root.
    /// </remarks>
    private static string Trim(string value) =>
        value.TrimEnd('.', ',', ')', '(', ':', ';', '"', '\'', '`', '<', '>', '[', ']');

    /// <summary>
    /// A path must look like a path: a directory separator, or a dot in its last
    /// segment. Without that every bare word in a reply is a candidate, and a repo
    /// with a file called "Makefile" would turn the word into a link.
    /// </summary>
    private static bool Plausible(string relative)
    {
        if (relative.Contains('/'))
        {
            return true;
        }

        var dot = relative.LastIndexOf('.');
        return dot > 0 && dot < relative.Length - 1;
    }

    /// <summary>
    /// The worktree-relative form of a mentioned path, or null when it points
    /// outside the worktree and so is nothing this app can open.
    /// </summary>
    private static string? Relative(string value, string worktreePath)
    {
        var path = value.Replace('\\', '/');

        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (path.Length == 0)
        {
            return null;
        }

        var rooted = path.StartsWith('/')
            || (path.Length > 2 && path[1] == ':' && char.IsLetter(path[0]));

        if (!rooted)
        {
            return Array.Exists(path.Split('/'), s => s == "..") ? null : path;
        }

        var root = Path.GetFullPath(worktreePath).Replace('\\', '/').TrimEnd('/');
        if (!path.StartsWith(root + "/", RealPaths.Comparison))
        {
            return null;
        }

        return path[(root.Length + 1)..];
    }

    /// <summary>
    /// The text split into plain and linked runs, in order. Concatenating every
    /// segment's text reproduces the original exactly, so a view can render the
    /// list without having to track offsets of its own.
    /// </summary>
    public static IReadOnlyList<TextSegment> Segments(
        string text,
        IReadOnlyList<FileReference> references)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (references.Count == 0)
        {
            return [new TextSegment(text, null)];
        }

        var segments = new List<TextSegment>(references.Count * 2 + 1);
        var at = 0;

        foreach (var r in references)
        {
            if (r.Start > at)
            {
                segments.Add(new TextSegment(text[at..r.Start], null));
            }

            segments.Add(new TextSegment(text.Substring(r.Start, r.Length), r));
            at = r.Start + r.Length;
        }

        if (at < text.Length)
        {
            segments.Add(new TextSegment(text[at..], null));
        }

        return segments;
    }
}
