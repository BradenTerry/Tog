namespace Tog.Core.Model;

/// <summary>One node of a path tree: a directory, or a file carrying a value.</summary>
public sealed class PathTreeNode<T>
{
    /// <summary>
    /// What the row shows. For a directory this may be several segments joined,
    /// where a chain of directories has only one thing in it.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Full path of this node: the file's path, or the directory's path with no
    /// trailing separator. Unique, so it works as a key.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>What the file is. Only set on file nodes.</summary>
    public T? Value { get; init; }

    public bool IsFile { get; init; }

    public IReadOnlyList<PathTreeNode<T>> Children { get; init; } = [];

    /// <summary>Files at or under this node.</summary>
    public int FileCount => IsFile ? 1 : Children.Sum(c => c.FileCount);
}

/// <summary>
/// Arranges a flat list of paths into a directory tree.
/// </summary>
/// <remarks>
/// A flat list is fine for a handful of files and useless for a hundred: the
/// paths all share a long prefix, so the part that tells them apart is the part
/// that gets clipped. A tree puts each name next to its siblings instead.
/// <para>
/// Directory chains with nothing to branch on are joined into one row
/// (<c>src/Project/Feature</c> rather than three rows each holding one child),
/// which is what keeps a deep .NET layout from spending most of its width on
/// indentation.
/// </para>
/// </remarks>
public static class PathTree
{
    /// <summary>The root node, whose children are the top level of the tree.</summary>
    public static PathTreeNode<T> Build<T>(IEnumerable<(string Path, T Value)> items)
    {
        var root = new Builder<T>("", "");

        foreach (var (path, value) in items)
        {
            var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var node = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                node = node.Directory(segments[i]);
            }

            node.AddFile(segments[^1], path, value);
        }

        return root.Build(collapse: false);
    }

    private sealed class Builder<T>(string segment, string path)
    {
        private readonly Dictionary<string, Builder<T>> _dirs = new(StringComparer.Ordinal);
        private readonly List<(string Name, string Path, T Value)> _files = [];

        public string Segment { get; } = segment;

        public string FullPath { get; } = path;

        public Builder<T> Directory(string name)
        {
            if (_dirs.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var child = new Builder<T>(name, FullPath.Length == 0 ? name : FullPath + "/" + name);
            _dirs[name] = child;
            return child;
        }

        public void AddFile(string name, string path, T value) => _files.Add((name, path, value));

        /// <param name="collapse">
        /// Whether this node may be merged into its parent's name. False for the
        /// root, which has no name to merge into.
        /// </param>
        public PathTreeNode<T> Build(bool collapse = true)
        {
            var node = this;
            var displayName = Segment;

            // A directory whose only content is one directory has nothing to branch
            // on, so it is drawn as part of the same row.
            while (collapse && node._files.Count == 0 && node._dirs.Count == 1)
            {
                var only = node._dirs.Values.Single();
                displayName = displayName.Length == 0 ? only.Segment : displayName + "/" + only.Segment;
                node = only;
            }

            var children = new List<PathTreeNode<T>>(node._dirs.Count + node._files.Count);
            children.AddRange(node._dirs.Values
                .OrderBy(d => d.Segment, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.Build()));
            children.AddRange(node._files
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new PathTreeNode<T>
                {
                    Name = f.Name,
                    Path = f.Path,
                    Value = f.Value,
                    IsFile = true,
                }));

            return new PathTreeNode<T>
            {
                Name = displayName,
                Path = node.FullPath,
                Children = children,
            };
        }
    }
}
