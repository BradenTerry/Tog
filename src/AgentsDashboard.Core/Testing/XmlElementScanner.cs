namespace AgentsDashboard.Core.Testing;

/// <summary>One complete element found in a partially written document.</summary>
/// <param name="Start">Index of the element's opening angle bracket.</param>
/// <param name="End">Index just past the element's final angle bracket.</param>
public readonly record struct ElementSpan(int Start, int End);

/// <summary>
/// Finds whole elements by name in XML that is still being written.
/// </summary>
/// <remarks>
/// <para>
/// A streaming TRX has no closing <c>&lt;/TestRun&gt;</c> until the run ends, so
/// it cannot be handed to an XML parser as a document. It can, however, be
/// mined for the elements that <em>are</em> complete, which is what this does:
/// scan forward, track nesting, and hand back only elements whose closing tag has
/// already been written. A half-written element at the tail is simply not
/// returned, and is picked up on the next pass once the writer finishes it.
/// </para>
/// <para>
/// Nesting matters: a data-driven test writes its per-case results as
/// <c>UnitTestResult</c> elements inside an <c>InnerResults</c> block of another
/// <c>UnitTestResult</c>. Matching the first closing tag would truncate the outer
/// element mid-way and produce unparseable XML.
/// </para>
/// </remarks>
public static class XmlElementScanner
{
    /// <summary>
    /// Every complete <paramref name="name"/> element at or after
    /// <paramref name="offset"/>, plus the offset to resume from next time.
    /// </summary>
    public static (List<ElementSpan> Spans, int NextOffset) Scan(string text, string name, int offset)
    {
        var spans = new List<ElementSpan>();
        var pos = Math.Clamp(offset, 0, text.Length);
        var resume = pos;

        while (pos < text.Length)
        {
            var open = FindStartTag(text, name, pos);
            if (open < 0)
            {
                // Nothing further starts here. Resume from the last unconsumed
                // '<', so a start tag arriving in pieces is not skipped past.
                var lastAngle = text.LastIndexOf('<');
                return (spans, Math.Max(resume, lastAngle < 0 ? text.Length : lastAngle));
            }

            var end = FindElementEnd(text, name, open);
            if (end < 0)
            {
                // The element has begun but not finished. Come back to it.
                return (spans, open);
            }

            spans.Add(new ElementSpan(open, end));
            pos = end;
            resume = end;
        }

        return (spans, resume);
    }

    /// <summary>Counts complete occurrences of a self-contained element name.</summary>
    public static int Count(string text, string name, int offset = 0)
    {
        var (spans, _) = Scan(text, name, offset);
        return spans.Count;
    }

    /// <summary>
    /// Index of the next <c>&lt;name</c> start tag, requiring the name to be
    /// followed by a delimiter so <c>&lt;UnitTest</c> does not match
    /// <c>&lt;UnitTestResult</c>.
    /// </summary>
    private static int FindStartTag(string text, string name, int from)
    {
        var needle = "<" + name;
        var i = text.IndexOf(needle, from, StringComparison.Ordinal);
        while (i >= 0)
        {
            var after = i + needle.Length;
            if (after >= text.Length)
            {
                return -1;
            }

            if (IsTagNameEnd(text[after]))
            {
                return i;
            }

            i = text.IndexOf(needle, i + 1, StringComparison.Ordinal);
        }

        return -1;
    }

    private static bool IsTagNameEnd(char c) => c is ' ' or '\t' or '\r' or '\n' or '>' or '/';

    /// <summary>
    /// Index just past the element that starts at <paramref name="start"/>, or -1
    /// when it is not complete yet.
    /// </summary>
    private static int FindElementEnd(string text, string name, int start)
    {
        var depth = 0;
        var pos = start;

        while (pos < text.Length)
        {
            var lt = text.IndexOf('<', pos);
            if (lt < 0)
            {
                return -1;
            }

            // Comments and CDATA can hold anything, including text that looks like
            // a tag, so step over them wholesale.
            if (Matches(text, lt, "<!--"))
            {
                var close = text.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (close < 0)
                {
                    return -1;
                }

                pos = close + 3;
                continue;
            }

            if (Matches(text, lt, "<![CDATA["))
            {
                var close = text.IndexOf("]]>", lt + 9, StringComparison.Ordinal);
                if (close < 0)
                {
                    return -1;
                }

                pos = close + 3;
                continue;
            }

            var tagEnd = FindTagEnd(text, lt, out var selfClosing);
            if (tagEnd < 0)
            {
                return -1;
            }

            var isClosing = lt + 1 < text.Length && text[lt + 1] == '/';
            var tagName = ReadTagName(text, lt, isClosing);

            if (string.Equals(tagName, name, StringComparison.Ordinal))
            {
                if (isClosing)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return tagEnd;
                    }
                }
                else if (!selfClosing)
                {
                    depth++;
                }
                else if (depth == 0)
                {
                    // <name ... /> with no children.
                    return tagEnd;
                }
            }

            pos = tagEnd;
        }

        return -1;
    }

    private static bool Matches(string text, int at, string token) =>
        at + token.Length <= text.Length
        && string.CompareOrdinal(text, at, token, 0, token.Length) == 0;

    /// <summary>
    /// Index just past the <c>&gt;</c> closing this tag, honouring quoted
    /// attribute values so a <c>&gt;</c> inside one does not end it early.
    /// </summary>
    private static int FindTagEnd(string text, int lt, out bool selfClosing)
    {
        selfClosing = false;
        var quote = '\0';

        for (var i = lt + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                selfClosing = i > lt + 1 && text[i - 1] == '/';
                return i + 1;
            }
        }

        return -1;
    }

    private static string ReadTagName(string text, int lt, bool isClosing)
    {
        var i = lt + (isClosing ? 2 : 1);
        var start = i;
        while (i < text.Length && !IsTagNameEnd(text[i]))
        {
            i++;
        }

        return text[start..i];
    }
}
