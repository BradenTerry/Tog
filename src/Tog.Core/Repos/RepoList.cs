namespace Tog.Core.Repos;

/// <summary>What adding a path to the repository list found.</summary>
/// <param name="Added">False only when there is nothing at the path; the list is then left alone.</param>
/// <param name="Note">Something worth telling whoever added it, even when it was added.</param>
public sealed record RepoCheck(bool Added, string? Note);

/// <summary>
/// Adding to and removing from <see cref="Settings.RepoRoots"/>, shared by
/// Settings and the <c>tog_add_repository</c> agent tool so the two accept the
/// same paths and say the same things about them.
/// </summary>
/// <remarks>
/// None of this touches <see cref="Settings.TrustedRoots"/>. A listed
/// repository is only offered under New agent; the first agent started in it
/// still asks whether Claude may read and change it, which is what keeps an
/// agent that lists one from granting itself or another agent anything.
/// </remarks>
public static class RepoList
{
    /// <summary>
    /// Whether <paramref name="root"/>, a repository or a <c>folder/*</c>, can be
    /// listed, and anything to say about it.
    /// </summary>
    public static RepoCheck Check(string root)
    {
        if (RepoDiscovery.WildcardFolder(root) is { } folder)
        {
            if (!Directory.Exists(folder))
            {
                return new RepoCheck(false, "There is no directory at that path.");
            }

            // Kept anyway: the point of a wildcard is the repositories cloned
            // into it later.
            return RepoDiscovery.ReposIn(folder).Count == 0
                ? new RepoCheck(true, "There are no repositories directly inside that folder yet.")
                : new RepoCheck(true, null);
        }

        if (!Directory.Exists(root))
        {
            return new RepoCheck(false, "There is no directory at that path.");
        }

        // Not fatal: discovery resolves any directory inside a repo up to its
        // root. Worth saying so rather than silently accepting nothing.
        return Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"))
            ? new RepoCheck(true, null)
            : new RepoCheck(true, "That is not a repository root, but it will be resolved to one if it is inside a repository.");
    }

    /// <summary>
    /// <paramref name="root"/> listed, last, and no longer hidden: adding a
    /// repository you once hid is asking to see it again.
    /// </summary>
    public static Settings Add(Settings settings, string root) => settings with
    {
        RepoRoots = [.. settings.RepoRoots.Where(r => r != root), root],
        HiddenRoots = settings.HiddenRoots.Where(r => r != root).ToArray(),
    };

    /// <summary>
    /// <paramref name="root"/> off the list. It is not hidden: while Claude runs in
    /// it, it still shows up under Running elsewhere.
    /// </summary>
    public static Settings Remove(Settings settings, string root) =>
        settings with { RepoRoots = settings.RepoRoots.Where(r => r != root).ToArray() };

    /// <summary>Whether <paramref name="root"/> is listed, written either way.</summary>
    public static bool Contains(Settings settings, string root) => settings.RepoRoots.Any(r => Same(r, root));

    /// <summary>Whether the user hid <paramref name="root"/>, written either way.</summary>
    public static bool IsHidden(Settings settings, string root) => settings.HiddenRoots.Any(h => Same(h, root));

    private static bool Same(string a, string b) =>
        RepoDiscovery.PathComparer.Equals(RepoDiscovery.Normalize(a), RepoDiscovery.Normalize(b));
}
