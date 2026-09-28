using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Presentation;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class RepoDiscoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentSession Session(string cwd) => new()
    {
        SessionId = Guid.NewGuid().ToString("n"),
        Pid = 1,
        Cwd = cwd,
    };

    private static RepoDiscovery Discovery() => new(new WorktreeLister(new GitCli()));

    [Fact]
    public async Task Finds_a_repository_from_an_agent_running_in_it()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("first");

        // Nothing is configured: an agent started anywhere reveals its repository,
        // which is what makes the dashboard useful the first time it opens.
        var roots = await Discovery().DiscoverAsync([Session(repo.Path)], new Settings(), Ct);

        Assert.Single(roots);
    }

    [Fact]
    public async Task Groups_a_linked_worktree_under_the_repository_it_belongs_to()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("first");
        repo.Git("worktree", "add", "-q", "-b", "side", ".worktrees/side");

        var roots = await Discovery().DiscoverAsync(
            [Session(repo.Path), Session(Path.Combine(repo.Path, ".worktrees", "side"))],
            new Settings(),
            Ct);

        // Two agents in two worktrees of one repository is one repository.
        Assert.Single(roots);
    }

    [Fact]
    public async Task Keeps_a_repository_the_user_added_by_hand()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("first");

        var roots = await Discovery().DiscoverAsync(
            [],
            new Settings { RepoRoots = [repo.Path] },
            Ct);

        Assert.Single(roots);
    }

    [Fact]
    public void A_path_ending_in_a_star_is_every_repository_directly_inside_the_folder()
    {
        using var dir = new TempDir();
        var one = dir.Dir("one");
        dir.Dir("one/.git");
        var two = dir.Dir("two");
        dir.File("two/.git", "gitdir: elsewhere\n");
        dir.Dir("notes");
        dir.Dir("nested/deeper/.git");

        // A plain path passes through untouched, in its place in the list.
        Assert.Equal(
            ["/by/hand", one, two],
            RepoDiscovery.Expand(["/by/hand", Path.Combine(dir.Path, "*")]));
    }

    [Theory]
    [InlineData("/code/*", "/code")]
    [InlineData("  /code/*  ", "/code")]
    [InlineData(@"C:\code\*", @"C:\code")]
    [InlineData("/*", "/")]
    [InlineData("/code", null)]
    [InlineData("/code/a*", null)]
    public void Only_a_last_segment_of_star_is_a_wildcard(string path, string? folder) =>
        Assert.Equal(folder, RepoDiscovery.WildcardFolder(path));

    [Fact]
    public void A_wildcard_on_a_missing_folder_is_nothing() =>
        Assert.Empty(RepoDiscovery.Expand(["/no/such/folder/anywhere/*"]));

    [Fact]
    public async Task Leaves_out_a_repository_the_user_hid()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("first");
        var canonical = repo.Git("rev-parse", "--show-toplevel");

        var roots = await Discovery().DiscoverAsync(
            [Session(repo.Path)],
            new Settings { HiddenRoots = [canonical] },
            Ct);

        // Hiding has to outrank discovery, or a repo with a live agent would keep
        // coming back however many times it was dismissed.
        Assert.Empty(roots);
    }

    [Fact]
    public async Task Ignores_a_working_directory_that_is_not_in_a_repository()
    {
        using var dir = new TempDir();

        Assert.Empty(await Discovery().DiscoverAsync([Session(dir.Path)], new Settings(), Ct));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Round_trips_settings()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(new AppPaths(dir.Path));

        store.Save(new Settings
        {
            RepoRoots = ["/a", "/b"],
            NotifyOnWaiting = false,
        });

        var loaded = store.Load();

        Assert.Equal(["/a", "/b"], loaded.RepoRoots);
        Assert.False(loaded.NotifyOnWaiting);
    }

    [Fact]
    public void Falls_back_to_defaults_when_the_file_is_unreadable()
    {
        using var dir = new TempDir();
        dir.File("settings.json", "{ not json");

        var loaded = new SettingsStore(new AppPaths(dir.Path)).Load();

        // A settings file we cannot parse must not stop the app starting.
        Assert.True(loaded.NotifyOnWaiting);
        Assert.Empty(loaded.RepoRoots);
    }

    [Fact]
    public void A_first_run_gets_defaults()
    {
        using var dir = new TempDir();

        Assert.True(new SettingsStore(new AppPaths(Path.Combine(dir.Path, "fresh"))).Load().NotifyOnWaiting);
    }
}

public class FmtTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(60, "1m")]
    [InlineData(252, "4m 12s")]
    [InlineData(3600, "1h")]
    [InlineData(5400, "1h 30m")]
    public void Formats_a_duration_at_the_precision_a_person_reads(int seconds, string expected) =>
        Assert.Equal(expected, Fmt.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void A_negative_duration_reads_as_zero_rather_than_as_nonsense() =>
        Assert.Equal("0s", Fmt.Duration(TimeSpan.FromSeconds(-5)));

    [Fact]
    public void Summarizes_a_worktree_with_no_changes_as_nothing() =>
        Assert.Null(Fmt.Changes(new GitStatusInfo()));

    [Fact]
    public void Summarizes_what_changed()
    {
        var status = new GitStatusInfo { Staged = 2, Changed = 3, Untracked = 1 };

        Assert.Equal("2 staged, 3 changed, 1 new", Fmt.Changes(status));
    }

    [Fact]
    public void Summarizes_the_position_against_upstream()
    {
        Assert.Equal("3 ahead, 1 behind", Fmt.Position(new GitStatusInfo { Ahead = 3, Behind = 1 }));
        Assert.Equal("2 ahead", Fmt.Position(new GitStatusInfo { Ahead = 2 }));
        Assert.Null(Fmt.Position(new GitStatusInfo()));
    }
}
