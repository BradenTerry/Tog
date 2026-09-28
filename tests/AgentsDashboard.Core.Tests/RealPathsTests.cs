using AgentsDashboard.Core.Repos;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class RealPathsTests
{
    [Fact]
    public void A_folder_reached_through_a_link_resolves_to_where_it_is()
    {
        using var dir = new TempDir();
        var real = dir.Dir("real/repo");
        Links.Directory(Path.Combine(dir.Path, "alias"), Path.Combine(dir.Path, "real"));

        var resolved = RealPaths.Resolve(Path.Combine(dir.Path, "alias", "repo"));

        Assert.Equal(RealPaths.Resolve(real), resolved);
        Assert.DoesNotContain("alias", resolved);
    }

    [Fact]
    public void A_folder_through_a_link_is_under_the_real_worktree()
    {
        using var dir = new TempDir();
        var real = dir.Dir("real/repo/src");
        Links.Directory(Path.Combine(dir.Path, "alias"), Path.Combine(dir.Path, "real"));

        Assert.True(RealPaths.IsUnder(Path.Combine(dir.Path, "alias", "repo", "src"), RealPaths.Resolve(Path.Combine(dir.Path, "real", "repo"))));
        Assert.True(RealPaths.IsUnder(Path.Combine(dir.Path, "alias", "repo"), Path.Combine(dir.Path, "real", "repo")));
        Assert.False(RealPaths.IsUnder(real, Path.Combine(dir.Path, "real", "rep")));
    }

    [Fact]
    public void A_link_to_a_link_is_followed_to_the_end()
    {
        using var dir = new TempDir();
        dir.Dir("real/repo");
        Links.Directory(Path.Combine(dir.Path, "one"), Path.Combine(dir.Path, "real"));
        Links.Directory(Path.Combine(dir.Path, "two"), Path.Combine(dir.Path, "one"));

        Assert.Equal(
            RealPaths.Resolve(Path.Combine(dir.Path, "real", "repo")),
            RealPaths.Resolve(Path.Combine(dir.Path, "two", "repo")));
    }

    [Fact]
    public void A_path_that_does_not_exist_comes_back_as_it_was()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "nowhere", "at-all");

        Assert.EndsWith(Path.Combine("nowhere", "at-all"), RealPaths.Resolve(missing));
    }

    [Fact]
    public void A_sibling_with_a_longer_name_is_not_inside() =>
        Assert.False(RealPaths.IsUnder("/no/such/repo-two", "/no/such/repo"));
}
