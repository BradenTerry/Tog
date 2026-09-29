using Tog.Core.Claude;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

public class PastSessionReaderTests
{
    private const string Cwd = "/Users/b/Projects/Soar";

    private static string Transcript(string sessionId) =>
        Path.Combine("projects", ClaudePaths.ProjectSlug(Cwd), sessionId + ".jsonl");

    [Fact]
    public void Lists_a_session_with_its_generated_title_and_last_prompt()
    {
        using var dir = new TempDir();
        dir.File(Path.Combine(".claude", Transcript("s1")), """
            {"type":"user","message":{"role":"user","content":"hi"}}
            {"type":"ai-title","aiTitle":"Admin page pagination","sessionId":"s1"}
            {"type":"last-prompt","lastPrompt":"is the admin page paginated"}
            """);

        var session = Assert.Single(new PastSessionReader(new ClaudePaths(home: dir.Path)).For(Cwd));

        Assert.Equal("s1", session.SessionId);
        Assert.Equal("Admin page pagination", session.Title);
        Assert.Equal("is the admin page paginated", session.LastPrompt);
    }

    [Fact]
    public void A_name_you_gave_it_wins_over_the_generated_one()
    {
        using var dir = new TempDir();
        dir.File(Path.Combine(".claude", Transcript("s1")), """
            {"type":"custom-title","customTitle":"My name","sessionId":"s1"}
            {"type":"ai-title","aiTitle":"Generated","sessionId":"s1"}
            """);

        var session = Assert.Single(new PastSessionReader(new ClaudePaths(home: dir.Path)).For(Cwd));

        Assert.Equal("My name", session.Title);
    }

    [Fact]
    public void The_latest_title_is_the_one_shown()
    {
        using var dir = new TempDir();
        dir.File(Path.Combine(".claude", Transcript("s1")), """
            {"type":"ai-title","aiTitle":"First idea","sessionId":"s1"}
            {"type":"ai-title","aiTitle":"What it became","sessionId":"s1"}
            """);

        Assert.Equal("What it became", Assert.Single(new PastSessionReader(new ClaudePaths(home: dir.Path)).For(Cwd)).Title);
    }

    [Fact]
    public void A_session_that_never_got_going_is_left_out()
    {
        using var dir = new TempDir();
        dir.File(Path.Combine(".claude", Transcript("empty")), """
            {"type":"system","content":"cleared"}
            """);

        Assert.Empty(new PastSessionReader(new ClaudePaths(home: dir.Path)).For(Cwd));
    }

    [Fact]
    public void Newest_first()
    {
        using var dir = new TempDir();
        dir.File(Path.Combine(".claude", Transcript("old")), """{"type":"ai-title","aiTitle":"Old"}""");
        dir.File(Path.Combine(".claude", Transcript("new")), """{"type":"ai-title","aiTitle":"New"}""");
        var projects = Path.Combine(dir.Path, ".claude", "projects", ClaudePaths.ProjectSlug(Cwd));
        File.SetLastWriteTimeUtc(Path.Combine(projects, "old.jsonl"), DateTime.UtcNow.AddHours(-2));

        var titles = new PastSessionReader(new ClaudePaths(home: dir.Path)).For(Cwd).Select(s => s.Title);

        Assert.Equal(["New", "Old"], titles);
    }

    [Fact]
    public void A_folder_with_no_history_has_no_sessions()
    {
        using var dir = new TempDir();

        Assert.Empty(new PastSessionReader(new ClaudePaths(home: dir.Path)).For(Cwd));
    }
}
