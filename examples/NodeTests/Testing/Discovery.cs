using System.Text.RegularExpressions;

namespace NodeTests.Testing;

/// <summary>
/// Finds test files and the tests in them by reading the files, without
/// running anything. Node has no way to list tests short of running them, and a
/// tab that executed the repository's code just because it was opened would be
/// a surprise. The list is a first guess: a test named in a loop or by a
/// variable appears once its file has run, and the run's results replace it.
/// </summary>
public static partial class Discovery
{
    private static readonly string[] Extensions = [".js", ".mjs", ".cjs", ".ts", ".mts", ".cts"];

    // Folders that are never the project's own tests, or are too big to walk.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "dist", "build", "coverage", "bin", "obj",
    };

    private const int MaxFiles = 500;
    private const long MaxFileBytes = 1024 * 1024;

    public static IReadOnlyList<TestFile> Find(string worktree, CancellationToken ct)
    {
        var files = new List<TestFile>();
        foreach (var path in Walk(worktree, ct).Take(MaxFiles))
        {
            var relative = Path.GetRelativePath(worktree, path).Replace('\\', '/');
            files.Add(new TestFile(relative, Names(path).Select(n => new TestCase(n)).ToList()));
        }

        return [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    /// <summary>Node's own naming convention for a test file, <c>*.test.js</c> and its TypeScript forms.</summary>
    public static bool IsTestFile(string name)
    {
        var extension = Path.GetExtension(name);
        return Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            && Path.GetFileNameWithoutExtension(name).EndsWith(".test", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Walk(string root, CancellationToken ct)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    // Dot folders hold tooling (.git, .claude and its worktrees),
                    // and a symlinked folder could lead anywhere, or in a circle.
                    if (!name.StartsWith('.') && !Skipped.Contains(name)
                        && !new DirectoryInfo(entry).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(entry);
                    }
                }
                else if (IsTestFile(name))
                {
                    yield return entry;
                }
            }
        }
    }

    private static List<string> Names(string path)
    {
        string text;
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                return [];
            }

            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return [.. TestCall().Matches(text).Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal)];
    }

    // test('name', ...) and it('name', ...), with .skip, .only or .todo, in any
    // of the three quote styles. describe is left out: it groups, it is not a test.
    [GeneratedRegex("""\b(?:it|test)(?:\.(?:skip|only|todo))?\s*\(\s*(?<q>['"`])(?<name>(?:(?!\k<q>).)+)\k<q>""")]
    private static partial Regex TestCall();
}
