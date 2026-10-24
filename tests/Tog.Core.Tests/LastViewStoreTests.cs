using Tog.Core.Repos;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

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
    public void Round_trips_the_chat_view()
    {
        using var temp = new TempDir();
        var store = new LastViewStore(new AppPaths(temp.Path));
        var view = new LastView("session-1", null, null, Pinned: false, ChatView: true);

        store.Save(view);

        Assert.True(new LastViewStore(new AppPaths(temp.Path)).Load()!.ChatView);
    }

    [Fact]
    public void A_file_from_before_the_chat_view_opens_the_workbench()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.LastViewFile, "{\"SessionId\":\"s\",\"WorktreePath\":null,\"RepoRoot\":null,\"Pinned\":false}");

        var view = new LastViewStore(paths).Load();

        Assert.Equal("s", view!.SessionId);
        Assert.False(view.ChatView);
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
