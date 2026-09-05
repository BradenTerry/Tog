using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Monitoring;
using AgentsDashboard.Core.Platform;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Core.Review;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class ReviewMarkdownWriterTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 5, 14, 22, 0, TimeSpan.Zero);

    private static ReviewComment Comment(string file, int start, int end, string body, string? snippet = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("n"),
            FilePath = file,
            StartLine = start,
            EndLine = end,
            Body = body,
            Snippet = snippet,
        };

    [Fact]
    public void Groups_by_file_and_orders_by_line()
    {
        var draft = new ReviewDraft
        {
            WorktreePath = "/repo",
            Summary = "Two things to fix.",
            Comments =
            [
                Comment("src/B.cs", 10, 10, "second file"),
                Comment("src/A.cs", 88, 88, "later line"),
                Comment("src/A.cs", 42, 45, "earlier line"),
            ],
        };

        var markdown = ReviewMarkdownWriter.Render(draft, At);

        Assert.Contains("# Review 2026-09-05 14:22", markdown);
        Assert.Contains("Two things to fix.", markdown);

        var a = markdown.IndexOf("## src/A.cs", StringComparison.Ordinal);
        var b = markdown.IndexOf("## src/B.cs", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "Files should be grouped and ordered by path");

        var early = markdown.IndexOf("**L42-45**", StringComparison.Ordinal);
        var late = markdown.IndexOf("**L88**", StringComparison.Ordinal);
        Assert.True(early >= 0 && late > early, "Comments should be ordered by line");
    }

    [Fact]
    public void Keeps_a_multi_line_comment_inside_its_bullet()
    {
        var draft = new ReviewDraft
        {
            WorktreePath = "/repo",
            Comments = [Comment("a.cs", 1, 1, "first line\nsecond line")],
        };

        var markdown = ReviewMarkdownWriter.Render(draft, At);

        // A bare newline would end the list item and orphan the rest from its line
        // number, so continuation lines are indented under the bullet.
        Assert.Contains("- **L1**: first line\n  second line", markdown);
    }

    [Fact]
    public void Quotes_the_lines_the_comment_is_about()
    {
        var draft = new ReviewDraft
        {
            WorktreePath = "/repo",
            Comments = [Comment("a.cs", 3, 4, "look here", "+added line\n context line")],
        };

        var markdown = ReviewMarkdownWriter.Render(draft, At);

        Assert.Contains("  > +added line", markdown);
        Assert.Contains("  >  context line", markdown);
    }

    [Fact]
    public void Says_which_side_a_removed_line_comment_is_on()
    {
        var draft = new ReviewDraft
        {
            WorktreePath = "/repo",
            Comments = [Comment("a.cs", 7, 7, "why did this go?") with { Side = DiffSide.Left }],
        };

        Assert.Contains("**L7** (removed line):", ReviewMarkdownWriter.Render(draft, At));
    }

    [Theory]
    [InlineData(1, "1 review comment")]
    [InlineData(3, "3 review comments")]
    public void Counts_comments_in_the_prompt(int count, string expected) =>
        Assert.Contains(expected, ReviewMarkdownWriter.Prompt("path/to/review.md", count));
}

public class FeedbackDispatcherTests
{
    private sealed class FakeClipboard : IClipboard
    {
        public string? Copied { get; private set; }

        public bool TryCopy(string text)
        {
            Copied = text;
            return true;
        }
    }

    private sealed class FailingClipboard : IClipboard
    {
        public bool TryCopy(string text) => false;
    }

    private static ReviewDraft Draft(string worktree) => new()
    {
        WorktreePath = worktree,
        Summary = "Have a look at these.",
        Comments =
        [
            new ReviewComment
            {
                Id = "c1",
                FilePath = "src/A.cs",
                StartLine = 42,
                EndLine = 45,
                Body = "This swallows the cancellation.",
            },
        ],
    };

    [Fact]
    public async Task Writes_the_review_into_the_worktree()
    {
        using var dir = new TempDir();
        var clipboard = new FakeClipboard();
        var dispatcher = new FeedbackDispatcher(clipboard, new FakeClock());

        var submission = await dispatcher.SubmitAsync(Draft(dir.Path), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(submission.MarkdownPath));
        Assert.StartsWith(
            Path.Combine(dir.Path, ".agents-dashboard", "reviews"),
            submission.MarkdownPath,
            StringComparison.Ordinal);

        var markdown = await File.ReadAllTextAsync(submission.MarkdownPath, TestContext.Current.CancellationToken);
        Assert.Contains("## src/A.cs", markdown);
        Assert.Contains("**L42-45**", markdown);
    }

    [Fact]
    public async Task Puts_a_paste_ready_prompt_on_the_clipboard()
    {
        using var dir = new TempDir();
        var clipboard = new FakeClipboard();
        var dispatcher = new FeedbackDispatcher(clipboard, new FakeClock());

        var submission = await dispatcher.SubmitAsync(Draft(dir.Path), TestContext.Current.CancellationToken);

        Assert.True(submission.ClipboardCopied);
        Assert.Equal(submission.Prompt, clipboard.Copied);

        // The prompt points at the file by a worktree-relative path, which is what
        // an agent working in that worktree can open.
        Assert.Contains(".agents-dashboard/reviews/review-", submission.Prompt);
        Assert.DoesNotContain(dir.Path, submission.Prompt);
    }

    [Fact]
    public async Task Writes_the_file_even_when_nothing_else_works()
    {
        using var dir = new TempDir();
        var dispatcher = new FeedbackDispatcher(new FailingClipboard(), new FakeClock());

        var submission = await dispatcher.SubmitAsync(Draft(dir.Path), TestContext.Current.CancellationToken);

        // The file is the artifact that has to survive everything else failing.
        Assert.True(File.Exists(submission.MarkdownPath));
        Assert.False(submission.ClipboardCopied);
    }

}

public class ReviewDraftStoreTests
{
    [Fact]
    public void Keeps_a_draft_across_loads()
    {
        using var dir = new TempDir();
        var store = new ReviewDraftStore(new AppPaths(dir.Path));

        store.Save(new ReviewDraft
        {
            WorktreePath = "/repo/wt",
            Summary = "in progress",
            Comments = [new ReviewComment { Id = "c1", FilePath = "a.cs", StartLine = 1, EndLine = 1, Body = "x" }],
        });

        var loaded = store.Load("/repo/wt");

        Assert.Equal("in progress", loaded.Summary);
        Assert.Single(loaded.Comments);
    }

    [Fact]
    public void Keeps_two_worktrees_drafts_apart()
    {
        using var dir = new TempDir();
        var store = new ReviewDraftStore(new AppPaths(dir.Path));

        store.Save(new ReviewDraft { WorktreePath = "/repo/a", Summary = "for a" });
        store.Save(new ReviewDraft { WorktreePath = "/repo/b", Summary = "for b" });

        Assert.Equal("for a", store.Load("/repo/a").Summary);
        Assert.Equal("for b", store.Load("/repo/b").Summary);
    }

    [Fact]
    public void Saving_an_empty_draft_removes_it()
    {
        using var dir = new TempDir();
        var store = new ReviewDraftStore(new AppPaths(dir.Path));

        store.Save(new ReviewDraft { WorktreePath = "/repo/wt", Summary = "something" });
        store.Save(new ReviewDraft { WorktreePath = "/repo/wt" });

        Assert.True(store.Load("/repo/wt").IsEmpty);
    }

    [Fact]
    public void An_unknown_worktree_loads_an_empty_draft() =>
        Assert.True(new ReviewDraftStore(new AppPaths(Path.GetTempPath()))
            .Load("/nowhere/at/all")
            .IsEmpty);
}

public class WaitingWatchTests
{
    private static WaitingAgent Agent(string id) => new(
        new AgentSession { SessionId = id, Pid = 1, Cwd = "/repo", Status = AgentStatus.Waiting },
        "/repo",
        "repo",
        "/repo",
        "repo",
        "main");

    [Fact]
    public void Says_nothing_about_agents_that_were_already_blocked_when_it_started()
    {
        var watch = new WaitingWatch();

        // Opening the dashboard onto three blocked agents should show you three
        // blocked agents, not fire three notifications about a state you are
        // already looking at.
        Assert.Empty(watch.Take([Agent("a"), Agent("b"), Agent("c")]));
    }

    [Fact]
    public void Announces_an_agent_that_blocks_after_it_started()
    {
        var watch = new WaitingWatch();
        watch.Take([Agent("a")]);

        var fresh = watch.Take([Agent("a"), Agent("b")]);

        Assert.Equal("b", Assert.Single(fresh).Session.SessionId);
    }

    [Fact]
    public void Does_not_repeat_while_an_agent_stays_blocked()
    {
        var watch = new WaitingWatch();
        watch.Take([]);
        watch.Take([Agent("a")]);

        Assert.Empty(watch.Take([Agent("a")]));
        Assert.Empty(watch.Take([Agent("a")]));
    }

    [Fact]
    public void Announces_again_when_the_same_agent_blocks_a_second_time()
    {
        var watch = new WaitingWatch();
        watch.Take([]);
        watch.Take([Agent("a")]);
        watch.Take([]);

        // A second question is a different question.
        Assert.Single(watch.Take([Agent("a")]));
    }
}
