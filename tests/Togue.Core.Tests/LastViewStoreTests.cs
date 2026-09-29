using Togue.Core.Repos;
using Togue.Core.Tests.Support;

namespace Togue.Core.Tests;

public class LastViewStoreTests
{
    [Fact]
    public void Nothing_saved_loads_as_nothing()
    {
        using var temp = new TempDir();

        Assert.Null(new LastViewStore(new AppPaths(temp.Path)).Load());
    }

    [Fact]
    public void Round_trips_the_agent_and_worktree()
    {
        using var temp = new TempDir();
        var store = new LastViewStore(new AppPaths(temp.Path));
        var view = new LastView("session-1", "/repo/.claude/worktrees/a", "/repo", Pinned: true);

        store.Save(view);

        Assert.Equal(view, new LastViewStore(new AppPaths(temp.Path)).Load());
    }

    [Fact]
    public void A_torn_file_loads_as_nothing()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.LastViewFile, "{\"SessionId\":");

        Assert.Null(new LastViewStore(paths).Load());
    }
}
