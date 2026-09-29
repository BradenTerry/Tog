namespace Togue.Core.Model;

/// <summary>A diff's files arranged as a directory tree.</summary>
public static class DiffTree
{
    public static PathTreeNode<DiffFile> Build(IEnumerable<DiffFile> files) =>
        PathTree.Build(files.Select(f => (f.Path, f)));
}

/// <summary>Roll-ups that only make sense for a tree of diffs.</summary>
public static class DiffTreeExtensions
{
    public static int Additions(this PathTreeNode<DiffFile> node) =>
        node.IsFile ? node.Value!.Additions : node.Children.Sum(c => c.Additions());

    public static int Deletions(this PathTreeNode<DiffFile> node) =>
        node.IsFile ? node.Value!.Deletions : node.Children.Sum(c => c.Deletions());
}
