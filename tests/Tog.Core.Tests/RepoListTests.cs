using Tog.Core.Repos;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

public class RepoListTests
{
    [Fact]
    public void Refuses_a_path_with_nothing_there()
    {
        using var dir = new TempDir();

        var check = RepoList.Check(Path.Combine(dir.Path, "missing"));

        Assert.False(check.Added);
        Assert.Equal("There is no directory at that path.", check.Note);
    }

    [Fact]
    public void Accepts_a_repository_root_without_a_note()
    {
        using var repo = new TempRepo();

        Assert.Equal(new RepoCheck(true, null), RepoList.Check(repo.Path));
    }

    [Fact]
    public void Accepts_a_folder_that_is_not_a_root_and_says_so()
    {
        using var dir = new TempDir();

        var check = RepoList.Check(dir.Path);

        Assert.True(check.Added);
        Assert.Contains("not a repository root", check.Note);
    }

    [Fact]
    public void Accepts_an_empty_wildcard_folder_for_the_repositories_cloned_into_it_later()
    {
        using var dir = new TempDir();

        var check = RepoList.Check(Path.Join(dir.Path, "*"));

        Assert.True(check.Added);
        Assert.Contains("no repositories directly inside", check.Note);
    }

    [Fact]
    public void Refuses_a_wildcard_whose_folder_is_missing()
    {
        using var dir = new TempDir();

        Assert.False(RepoList.Check(Path.Join(dir.Path, "missing", "*")).Added);
    }

    [Fact]
    public void Adding_lists_the_root_once_and_last_and_unhides_it()
    {
        var settings = new Settings { RepoRoots = ["/a", "/b"], HiddenRoots = ["/a"] };

        var next = RepoList.Add(settings, "/a");

        Assert.Equal(["/b", "/a"], next.RepoRoots);
        Assert.Empty(next.HiddenRoots);
    }

    [Fact]
    public void Adding_never_trusts_the_repository()
    {
        // Trust is asked of the user when the first agent starts there. An agent
        // that lists a repository must not get past that.
        var next = RepoList.Add(new Settings { TrustedRoots = ["/other"] }, "/a");

        Assert.Equal(["/other"], next.TrustedRoots);
    }

    [Fact]
    public void Removing_takes_it_off_the_list_without_hiding_it()
    {
        var next = RepoList.Remove(new Settings { RepoRoots = ["/a", "/b"] }, "/a");

        Assert.Equal(["/b"], next.RepoRoots);
        Assert.Empty(next.HiddenRoots);
    }

    [Fact]
    public void Matches_a_root_written_with_a_trailing_separator()
    {
        var settings = new Settings { RepoRoots = ["/a/"], HiddenRoots = ["/h/"] };

        Assert.True(RepoList.Contains(settings, "/a"));
        Assert.True(RepoList.IsHidden(settings, "/h"));
        Assert.False(RepoList.Contains(settings, "/h"));
    }
}
