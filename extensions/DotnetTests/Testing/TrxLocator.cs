namespace AgentsDashboard.Extensions.DotnetTests;

/// <summary>Finds the TRX reports already sitting in a worktree.</summary>
/// <remarks>
/// Where a TRX lands depends on how the run was started: <c>dotnet test</c> from
/// a repo root writes to <c>&lt;root&gt;/TestResults</c>, while running a test
/// application directly writes beside it, under
/// <c>bin/&lt;config&gt;/&lt;tfm&gt;/TestResults</c>. So the whole worktree is
/// searched, minus the directories that are never going to hold one and are
/// expensive to walk.
/// </remarks>
public static class TrxLocator
{
    private const int MaxDepth = 12;

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", ".vs", ".idea", ".vscode", "packages", ".nuget", ".venv", "venv",
    };

    /// <summary>Every <c>.trx</c> under a worktree, newest last.</summary>
    public static List<string> Find(string worktreePath)
    {
        var found = new List<string>();
        Walk(worktreePath, 0, found);
        return found;
    }

    private static void Walk(string dir, int depth, List<string> found)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.trx"))
            {
                found.Add(file);
            }

            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(child);
                if (!Skip.Contains(name))
                {
                    Walk(child, depth + 1, found);
                }
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // A directory we cannot read holds no reports we can read either.
        }
    }

    /// <summary>
    /// The project name a TRX belongs to, from the default
    /// <c>{asm}_{tfm}_{arch}</c> file name. Falls back to the whole file name for
    /// anything else, because a wrong guess reads worse than a long label.
    /// </summary>
    public static string ProjectNameFrom(string trxPath)
    {
        var name = Path.GetFileNameWithoutExtension(trxPath);
        var parts = name.Split('_');

        var end = parts.Length;
        while (end > 1 && (IsArch(parts[end - 1]) || IsTfm(parts[end - 1])))
        {
            end--;
        }

        return end == parts.Length ? name : string.Join('_', parts[..end]);
    }

    private static bool IsArch(string s) =>
        s is "x64" or "x86" or "arm64" or "arm" or "AnyCPU";

    private static bool IsTfm(string s) =>
        (s.StartsWith("net", StringComparison.OrdinalIgnoreCase) && s.Length > 3 && char.IsDigit(s[3]))
        || s.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase);
}
