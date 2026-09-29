using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NodeTests.Testing;

/// <summary>
/// Runs one test file with <c>node --test</c> and reads its JUnit report.
/// </summary>
/// <remarks>
/// This is the only place the extension starts a process, and it runs only
/// from a Run button. <c>node</c> is started directly with an argument list,
/// never through a shell, so a file name cannot become a command. It runs in
/// the worktree with the app's environment, as <c>npm test</c> in a terminal
/// there would: running the tests runs the repository's code, which is why it
/// waits for a click. Output is capped and the process tree is killed on
/// cancel or timeout, so a hung test cannot outlive the run or fill memory.
/// </remarks>
public static class NodeTestProcess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private const int MaxOutputChars = 4 * 1024 * 1024;
    private const int MaxErrorChars = 16 * 1024;

    public static async Task<TestFile> RunAsync(string worktree, TestFile file, CancellationToken ct)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = worktree,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        foreach (var arg in (string[])["--test", "--test-reporter=junit", "--test-reporter-destination=stdout", file.Path])
        {
            start.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return file with { Status = TestStatus.Failed, Error = "node was not found. Install Node 22 or newer and restart Tog." };
        }

        // A test that reads stdin would otherwise wait forever.
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        // Both streams are read at once: a full stderr pipe blocks the process
        // before it writes the report to stdout.
        var output = ReadCappedAsync(process.StandardOutput, MaxOutputChars);
        var errors = ReadCappedAsync(process.StandardError, MaxErrorChars);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            return file with { Status = TestStatus.Failed, Error = $"Stopped after {Timeout.TotalMinutes:0} minutes." };
        }

        return Read(file, await output, await errors);
    }

    /// <summary>Folds a JUnit report into the file's listed tests, keeping their order.</summary>
    public static TestFile Read(TestFile file, string report, string errors)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(report);
        }
        catch (XmlException)
        {
            return file with { Status = TestStatus.Failed, Error = Tail(errors) ?? "node --test did not produce a report." };
        }

        var results = doc.Descendants("testcase").Select(Case).ToList();

        // A file that fails to load is reported as one test named after the
        // file. Its message is a generic "test failed"; the reason is on stderr.
        if (results is [{ Name: var only, Status: TestStatus.Failed }] && only == file.Path)
        {
            return file with { Status = TestStatus.Failed, Error = Tail(errors) ?? "The file did not load." };
        }

        var byName = results.ToLookup(r => r.Name, StringComparer.Ordinal);
        var used = new HashSet<TestCase>(ReferenceEqualityComparer.Instance);
        var merged = new List<TestCase>();
        foreach (var listed in file.Tests)
        {
            var match = byName[listed.Name].FirstOrDefault(r => !used.Contains(r));
            if (match is not null)
            {
                used.Add(match);
                merged.Add(match);
            }
        }

        // Tests the file read could not name, such as ones made in a loop.
        merged.AddRange(results.Where(r => !used.Contains(r)));

        var status = merged.Any(t => t.Status == TestStatus.Failed) ? TestStatus.Failed
            : merged.Any(t => t.Status == TestStatus.Passed) ? TestStatus.Passed
            : TestStatus.Skipped;

        return new TestFile(file.Path, merged, status);
    }

    private static TestCase Case(XElement test)
    {
        var failure = test.Element("failure");
        var status = failure is not null ? TestStatus.Failed
            : test.Element("skipped") is not null ? TestStatus.Skipped
            : TestStatus.Passed;

        TimeSpan? duration = double.TryParse((string?)test.Attribute("time"), System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;

        return new TestCase(
            (string?)test.Attribute("name") ?? "(unnamed)",
            (string?)test.Parent?.Attribute("name"),
            status,
            duration,
            (string?)failure?.Attribute("message"));
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, int max)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            // Past the cap the stream is still drained, just not kept.
            text.Append(buffer, 0, Math.Min(read, Math.Max(0, max - text.Length)));
        }

        return text.ToString();
    }

    private static string? Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? null : string.Join('\n', lines.TakeLast(12));
    }
}
