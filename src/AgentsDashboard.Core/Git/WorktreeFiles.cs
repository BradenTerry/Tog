using System.Security.Cryptography;
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

/// <summary>A file's whole text, ready to edit.</summary>
/// <remarks>
/// The editor works in "\n" only, so <see cref="Text"/> is normalised on the way
/// in and <see cref="Eol"/> and <see cref="HasBom"/> carry what the file actually
/// used. Handing those back to <see cref="WorktreeFiles.Write"/> is what stops a
/// one line edit from rewriting every line ending in the file and turning a small
/// change into a whole-file diff.
/// </remarks>
public sealed record TextFile
{
    /// <summary>Worktree-relative path.</summary>
    public required string Path { get; init; }

    /// <summary>The whole file with "\n" line endings and no BOM.</summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// Lowercase hex SHA-256 of the bytes that were read, so a later write can
    /// tell whether the file changed underneath the editor.
    /// </summary>
    public string Stamp { get; init; } = "";

    public long Bytes { get; init; }
    public bool IsBinary { get; init; }

    /// <summary>True when only the first part of the file was read.</summary>
    public bool Truncated { get; init; }

    /// <summary>Set when the file could not be read, with the reason.</summary>
    public string? Error { get; init; }

    /// <summary>The line ending the file used: "\r\n" or "\n".</summary>
    public string Eol { get; init; } = "\n";

    /// <summary>True when the file started with a UTF-8 BOM.</summary>
    public bool HasBom { get; init; }

    /// <summary>Saving a binary, truncated or unreadable file would lose data.</summary>
    public bool Editable => Error is null && !IsBinary && !Truncated;
}

/// <summary>The outcome of a save.</summary>
/// <param name="Ok">False when nothing was written.</param>
/// <param name="Message">What to show the user.</param>
/// <param name="Stamp">The new stamp on success, for the next save.</param>
/// <param name="Conflict">True when the file changed since it was opened.</param>
public sealed record WriteResult(bool Ok, string Message, string? Stamp = null, bool Conflict = false);

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

        // At the file a link points to, for the same reason as ReadTextAt.
        full = LinkTarget(full);

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
    /// A file's whole text, for the editor.
    /// </summary>
    /// <remarks>
    /// The path is validated the same way <see cref="Read"/> validates it, and the
    /// same binary and size rules apply: a file that cannot be shown safely comes
    /// back marked rather than half-read, and <see cref="TextFile.Editable"/> is
    /// then false so the UI never offers to save over it.
    /// </remarks>
    public TextFile ReadText(string worktreePath, string relativePath)
    {
        var full = Resolve(worktreePath, relativePath);
        if (full is null)
        {
            return new TextFile { Path = relativePath, Error = "That path is not inside this worktree." };
        }

        return ReadTextAt(full, relativePath);
    }

    /// <summary>
    /// Reads a file by its absolute path, for one opened from outside any
    /// worktree, with the same size and binary rules. Its <see cref="TextFile.Path"/>
    /// is the absolute path.
    /// </summary>
    public TextFile ReadTextOutside(string absolutePath) =>
        Path.IsPathRooted(absolutePath)
            ? ReadTextAt(Path.GetFullPath(absolutePath), absolutePath)
            : new TextFile { Path = absolutePath, Error = "That is not an absolute path." };

    private static TextFile ReadTextAt(string full, string relativePath)
    {
        // Measured and read at the file a link points to: a FileInfo on the link
        // itself reports the link's own few bytes, and the read would stop there.
        full = LinkTarget(full);

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                return new TextFile { Path = relativePath, Error = "That file is no longer there." };
            }

            var take = (int)Math.Min(info.Length, MaxBytes);
            var bytes = new byte[take];

            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.ReadExactly(bytes, 0, take);
            }

            if (LooksBinary(bytes))
            {
                return new TextFile
                {
                    Path = relativePath,
                    Bytes = info.Length,
                    IsBinary = true,
                    Stamp = Stamp(bytes),
                };
            }

            var span = (ReadOnlySpan<byte>)bytes;
            var hasBom = span.StartsWith(Utf8Bom);
            var text = Encoding.UTF8.GetString(hasBom ? span[Utf8Bom.Length..] : span);

            // The first ending decides, because a file with mixed endings still has
            // to be written back as one thing and the majority is rarely the point.
            var first = text.IndexOf('\n');
            var crlf = first > 0 && text[first - 1] == '\r';

            return new TextFile
            {
                Path = relativePath,
                Bytes = info.Length,
                Truncated = info.Length > MaxBytes,
                Stamp = Stamp(bytes),
                Eol = crlf ? "\r\n" : "\n",
                HasBom = hasBom,
                Text = text.Replace("\r\n", "\n", StringComparison.Ordinal),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new TextFile { Path = relativePath, Error = e.Message };
        }
    }

    /// <summary>
    /// Saves a file, refusing to overwrite a change made since it was opened.
    /// </summary>
    /// <remarks>
    /// An agent is usually still working in the worktree while the file is open in
    /// the editor, so the stamp is checked against the bytes on disk first: without
    /// it a save would silently undo whatever the agent wrote in the meantime. The
    /// write itself goes to a temp file in the same directory and is then moved
    /// over the original, so an interrupted save leaves the old file intact rather
    /// than a half-written one.
    /// </remarks>
    public WriteResult Write(
        string worktreePath,
        string relativePath,
        string text,
        string? expectedStamp,
        string eol = "\n",
        bool bom = false,
        bool force = false)
    {
        var full = Resolve(worktreePath, relativePath);
        if (full is null)
        {
            return new WriteResult(false, "That path is not inside this worktree.");
        }

        return WriteAt(full, text, expectedStamp, eol, bom, force);
    }

    /// <summary>
    /// Saves a file by its absolute path, one the user opened from outside any
    /// worktree and edited, with the same check against a change made since.
    /// Only ever called from the user's own Save; nothing the app does on its own
    /// writes outside a worktree.
    /// </summary>
    public WriteResult WriteOutside(
        string absolutePath,
        string text,
        string? expectedStamp,
        string eol = "\n",
        bool bom = false,
        bool force = false) =>
        Path.IsPathRooted(absolutePath)
            ? WriteAt(Path.GetFullPath(absolutePath), text, expectedStamp, eol, bom, force)
            : new WriteResult(false, "That is not an absolute path.");

    private static WriteResult WriteAt(string full, string text, string? expectedStamp, string eol, bool bom, bool force)
    {
        // A link is written through to the file it points at. Moving the temp
        // file over the link itself would replace it with a copy, and a CLAUDE.md
        // kept in a dotfiles repository would quietly stop being kept there.
        full = LinkTarget(full);

        try
        {
            var exists = System.IO.File.Exists(full);

            if (exists && !force)
            {
                var current = Stamp(System.IO.File.ReadAllBytes(full));
                if (!string.Equals(current, expectedStamp, StringComparison.Ordinal))
                {
                    return new WriteResult(
                        false,
                        "The file changed on disk since you opened it.",
                        Conflict: true);
                }
            }

            var body = eol == "\r\n"
                ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)
                : text;

            var encoded = Encoding.UTF8.GetBytes(body);
            byte[] bytes = encoded;

            if (bom)
            {
                bytes = [.. Utf8Bom, .. encoded];
            }

            var directory = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(directory);

            // The temp file has to share the directory, or the move across volumes
            // stops being atomic and becomes a copy that can be interrupted.
            var temp = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("n") + ".tmp");

            try
            {
                System.IO.File.WriteAllBytes(temp, bytes);
                System.IO.File.Move(temp, full, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            return new WriteResult(true, "Saved.", Stamp(bytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new WriteResult(false, e.Message);
        }
    }

    private static string LinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is not worth failing the save we already failed.
        }
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Lowercase hex SHA-256 of some bytes.</summary>
    private static string Stamp(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

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

    /// <summary>The files arranged as a tree, for the Files tree.</summary>
    public static PathTreeNode<WorktreeFile> Tree(IEnumerable<WorktreeFile> files) =>
        PathTree.Build(files.Select(f => (f.Path, f)));
}
