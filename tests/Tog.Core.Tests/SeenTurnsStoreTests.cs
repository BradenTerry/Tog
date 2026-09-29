using Tog.Core.Repos;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

public class SeenTurnsStoreTests
{
    private static readonly DateTimeOffset First = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Second = First.AddMinutes(5);

    [Fact]
    public void An_agent_with_no_finished_turn_is_not_unread()
    {
        using var temp = new TempDir();

        Assert.False(new SeenTurnsStore(new AppPaths(temp.Path)).IsUnread("a", null));
    }

    [Fact]
    public void A_turn_never_seen_is_unread_until_marked_and_a_later_one_is_unread_again()
    {
        using var temp = new TempDir();
        var store = new SeenTurnsStore(new AppPaths(temp.Path));

        Assert.True(store.IsUnread("a", First));
        Assert.True(store.MarkSeen("a", First, ["a"]));
        Assert.False(store.IsUnread("a", First));
        Assert.False(store.MarkSeen("a", First, ["a"]));
        Assert.True(store.IsUnread("a", Second));
    }

    [Fact]
    public void What_was_seen_is_kept_across_a_restart_and_agents_gone_from_the_list_are_forgotten()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var store = new SeenTurnsStore(paths);
        store.MarkSeen("gone", First, ["gone", "kept"]);
        store.MarkSeen("kept", First, ["kept"]);

        var reopened = new SeenTurnsStore(paths);

        Assert.False(reopened.IsUnread("kept", First));
        Assert.True(reopened.IsUnread("gone", First));
    }

    [Fact]
    public void A_torn_file_is_nothing_seen()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.SeenTurnsFile, "{\"a\":");

        Assert.True(new SeenTurnsStore(paths).IsUnread("a", First));
    }
}
