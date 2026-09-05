using System.Text;
using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Git;

/// <summary>One file in the worktree, as the browser lists it.</summary>
/// <param name="Path">Repo-relative path.</param>
/// <param name="Tracked">False for a file git knows about only as untracked.</param>
public sealed record WorktreeFile(string Path, bool Tracked);

/// <summary>A file's contents, ready to show.</summary>
public sealed record FileContent
{
    public required string Path { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = [];
    public long Bytes { get; init; }
    public bool IsBinary { get; init; }

    /// <summary>True when only the first part of the file was read.</summary>
    public bool Truncated { get; init; }

    /// <summary>Set when the file could not be read, with the reason.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Browsing the files of a worktree.
/// </summary>
/// <remarks>
/// The listing comes from git rather than from walking the directory, so it
/// respects <c>.gitignore</c> for free: a worktree's <c>bin</c>, <c>obj</c> and
/// <c>node_modules</c> are not files you want to browse, and no hand-written
/// skip list would keep up with a project's own ignore rules.
/// </remarks>
public sealed class WorktreeFiles(IGitCli git)
{
    /// <summary>Files larger than this are shown up to the limit and marked truncated.</summary>
    private const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>Every tracked and untracked-but-not-ignored file, sorted by path.</summary>
    public async Task<IReadOnlyList<WorktreeFile>> ListAsync(
        string worktreePath,
        CancellationToken ct = default)
    {
        var tracked = await Read(worktreePath, ["ls-files", "-z"], ct).ConfigureAwait(false);
        var untracked = await Read(
            worktreePath,
            ["ls-files", "-z", "--others", "--exclude-standard"],
            ct).ConfigureAwait(false);

        var files = new List<WorktreeFile>(tracked.Count + untracked.Count);
        files.AddRange(tracked.Select(p => new WorktreeFile(p, Tracked: true)));
        files.AddRange(untracked.Select(p => new WorktreeFile(p, Tracked: false)));
        files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return files;
    }

    private async Task<List<string>> Read(
        string worktreePath,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        // -z separates with NUL, which is the only separator a path cannot contain.
        var full = new List<string> { "-c", "core.quotePath=false" };
        full.AddRange(args);

        var result = await git.RunAsync(worktreePath, full, ct).ConfigureAwait(false);
        return result.Ok
            ? result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];
    }

    /// <summary>
    /// A file's text, split into lines.
    /// </summary>
    /// <remarks>
    /// The path comes from the UI, so it is checked rather than trusted: it must
    /// be relative, free of <c>..</c>, and resolve to somewhere inside the
    /// worktree. Without that, a crafted path would let the browser read anything
    /// the user can read.
    /// </remarks>
    public FileContent Read(string worktreePath, string relativePath)
    {
        var full = Resolve(worktreePath, relativePath);
        if (full is null)
        {
            return new FileContent { Path = relativePath, Error = "That path is not inside this worktree." };
        }

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                return new FileContent { Path = relativePath, Error = "That file is no longer there." };
            }

            var take = (int)Math.Min(info.Length, MaxBytes);
            var bytes = new byte[take];

            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.ReadExactly(bytes, 0, take);
            }

            if (LooksBinary(bytes))
            {
                return new FileContent { Path = relativePath, Bytes = info.Length, IsBinary = true };
            }

            var text = Encoding.UTF8.GetString(bytes);
            var lines = text.Split('\n');

            // A trailing newline produces one empty element that is not a line.
            var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

            return new FileContent
            {
                Path = relativePath,
                Bytes = info.Length,
                Truncated = info.Length > MaxBytes,
                Lines = lines.Take(count).Select(l => l.TrimEnd('\r')).ToArray(),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new FileContent { Path = relativePath, Error = e.Message };
        }
    }

    /// <summary>
    /// The absolute path of a worktree-relative path, or null when it does not
    /// stay inside the worktree.
    /// </summary>
    public static string? Resolve(string worktreePath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Split('/').Any(s => s is ".."))
        {
            return null;
        }

        var root = Path.GetFullPath(worktreePath);
        var full = Path.GetFullPath(Path.Combine(root, normalized));

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return full.StartsWith(prefix, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>A NUL byte early in the file is git's own binary heuristic.</summary>
    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var window = bytes.Length < 8000 ? bytes : bytes[..8000];
        return window.IndexOf((byte)0) >= 0;
    }

    /// <summary>The files arranged as a tree, for the browser's sidebar.</summary>
    public static PathTreeNode<WorktreeFile> Tree(IEnumerable<WorktreeFile> files) =>
        PathTree.Build(files.Select(f => (f.Path, f)));
}
