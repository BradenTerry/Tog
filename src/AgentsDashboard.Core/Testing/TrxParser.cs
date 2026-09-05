using System.Globalization;
using System.Xml.Linq;
using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Testing;

/// <summary>What one pass over a TRX file found.</summary>
/// <param name="Results">Results completed since the previous pass.</param>
/// <param name="NextOffset">Where the next pass should resume.</param>
/// <param name="DeclaredTotal">Tests the run says it will execute, when it has said.</param>
/// <param name="Completed">True once the closing TestRun tag has been written.</param>
/// <param name="DefinitionCount">Test definitions seen in this slice, which a
/// caller reading the file in pieces accumulates.</param>
/// <param name="StartedAt">When the run says it began, when it has said.</param>
public sealed record TrxScan(
    IReadOnlyList<TestResultItem> Results,
    int NextOffset,
    int? DeclaredTotal,
    bool Completed,
    int DefinitionCount = 0,
    DateTimeOffset? StartedAt = null);

/// <summary>
/// Reads a TRX report, including one that is still being written.
/// </summary>
/// <remarks>
/// Microsoft.Testing.Platform 2.3.0 and later stream results into the TRX as the
/// run progresses, which turns the report file itself into a live feed. That is
/// what this parser exists for: it is deliberately incremental and tolerant, and
/// never loads the file as a document, because for most of a run it is not one.
/// A classic VSTest run writes its TRX once, at the end; the same code reads it,
/// it just arrives all at once.
/// </remarks>
public static class TrxParser
{
    /// <summary>
    /// Scans forward from <paramref name="offset"/>, returning only elements that
    /// were complete at the time of reading.
    /// </summary>
    public static TrxScan Scan(string text, int offset = 0)
    {
        var (spans, next) = XmlElementScanner.Scan(text, "UnitTestResult", offset);

        var results = new List<TestResultItem>(spans.Count);
        foreach (var span in spans)
        {
            var item = ParseResult(text[span.Start..span.End]);
            if (item is not null)
            {
                results.Add(item);
            }
        }

        var definitions = XmlElementScanner.Count(text, "UnitTest");

        return new TrxScan(
            results,
            next,
            ReadCounterTotal(text) ?? (definitions > 0 ? definitions : null),
            text.Contains("</TestRun>", StringComparison.Ordinal),
            definitions,
            ReadStartTime(text));
    }

    /// <summary>
    /// The run's own total from its summary counters, which are only written once
    /// the run ends. Null before then, and the UI shows a count rather than a
    /// percentage instead of inventing a denominator.
    /// </summary>
    public static int? ReadCounterTotal(string text)
    {
        var counters = text.IndexOf("<Counters ", StringComparison.Ordinal);
        if (counters < 0)
        {
            return null;
        }

        var total = ReadAttribute(text, counters, "total");
        return total is not null
               && int.TryParse(total, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    /// <summary>
    /// When the run began, from the report's own Times element. Preferred over the
    /// file's creation time, which is when the writer got round to opening it.
    /// </summary>
    public static DateTimeOffset? ReadStartTime(string text)
    {
        var times = text.IndexOf("<Times ", StringComparison.Ordinal);
        if (times < 0)
        {
            return null;
        }

        var start = ReadAttribute(text, times, "start");
        return start is not null
               && DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at.ToLocalTime()
            : null;
    }

    /// <summary>Reads one attribute out of the start tag beginning at <paramref name="tagStart"/>.</summary>
    private static string? ReadAttribute(string text, int tagStart, string name)
    {
        var close = text.IndexOf('>', tagStart);
        if (close < 0)
        {
            return null;
        }

        var tag = text[tagStart..close];
        var needle = name + "=\"";
        var at = tag.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var valueStart = at + needle.Length;
        var valueEnd = tag.IndexOf('"', valueStart);
        return valueEnd < 0 ? null : tag[valueStart..valueEnd];
    }

    private static TestResultItem? ParseResult(string xml)
    {
        XElement element;
        try
        {
            element = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            // Only complete elements reach here, so this means genuinely malformed
            // XML rather than a truncated read. Skipping one result is better than
            // failing the whole run's display.
            return null;
        }

        var name = (string?)element.Attribute("testName");
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var testId = (string?)element.Attribute("testId")
                     ?? (string?)element.Attribute("executionId")
                     ?? name;

        var errorInfo = Descendant(element, "ErrorInfo");

        return new TestResultItem
        {
            TestId = testId,
            Name = name,
            ClassName = SplitClassName(name),
            Outcome = MapOutcome((string?)element.Attribute("outcome")),
            Duration = ParseDuration((string?)element.Attribute("duration")),
            EndTime = ParseTime((string?)element.Attribute("endTime")),
            Message = Trim(Descendant(errorInfo, "Message")?.Value),
            StackTrace = Trim(Descendant(errorInfo, "StackTrace")?.Value),
        };
    }

    /// <summary>
    /// Finds a child by local name. TRX declares a default namespace, but a
    /// fragment parsed on its own has lost the declaration, so matching on the
    /// local name works for both.
    /// </summary>
    private static XElement? Descendant(XElement? parent, string localName) =>
        parent?.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// The declaring type of a fully qualified test name. Data-driven tests carry
    /// their arguments in parentheses, which must not be mistaken for a member
    /// separator, so only the part before the first bracket is considered.
    /// </summary>
    public static string? SplitClassName(string testName)
    {
        var bracket = testName.IndexOf('(');
        var head = bracket < 0 ? testName : testName[..bracket];
        var dot = head.LastIndexOf('.');
        return dot > 0 ? head[..dot] : null;
    }

    public static TestOutcome MapOutcome(string? raw) => raw switch
    {
        "Passed" => TestOutcome.Passed,
        "Failed" or "Error" or "Timeout" or "Aborted" => TestOutcome.Failed,
        "NotExecuted" => TestOutcome.NotExecuted,
        "Skipped" or "Inconclusive" or "Pending" => TestOutcome.Skipped,
        _ => TestOutcome.Other,
    };

    private static TimeSpan ParseDuration(string? raw) =>
        raw is not null && TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var d)
            ? d
            : TimeSpan.Zero;

    private static DateTimeOffset? ParseTime(string? raw) =>
        raw is not null
        && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;
}
