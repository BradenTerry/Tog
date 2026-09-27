using AgentsDashboard.Core.Platform;
using AgentsDashboard.Core.Presentation;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class OpenRequestsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void A_request_names_an_absolute_path_and_maybe_a_line()
    {
        var file = Path.Combine(Path.GetTempPath(), "shot.png");
        var json = $$"""{"path": {{System.Text.Json.JsonSerializer.Serialize(file)}}, "line": 12}""";

        Assert.Equal(new OpenRequest(file, 12), OpenRequests.Parse(json));
    }

    [Theory]
    [InlineData("""{"path": "relative/shot.png"}""")]
    [InlineData("""{"path": ""}""")]
    [InlineData("""{"file": "/tmp/shot.png"}""")]
    [InlineData("""["/tmp/shot.png"]""")]
    [InlineData("""{"path": "/tmp/sh""")]
    public void Anything_else_is_not_a_request(string json) => Assert.Null(OpenRequests.Parse(json));

    [Fact]
    public void A_line_that_is_not_a_positive_number_is_ignored()
    {
        var file = Path.Combine(Path.GetTempPath(), "notes.txt");
        var json = $$"""{"path": {{System.Text.Json.JsonSerializer.Serialize(file)}}, "line": 0}""";

        Assert.Null(OpenRequests.Parse(json)!.Line);
    }

    [Fact]
    public async Task A_dropped_request_is_delivered_once_and_deleted()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        using var requests = new OpenRequests(paths, new FakeClock(DateTimeOffset.UtcNow));
        var got = new List<OpenRequest>();
        var arrived = new TaskCompletionSource();
        using var _ = requests.Subscribe(r =>
        {
            lock (got)
            {
                got.Add(r);
            }

            arrived.TrySetResult();
        });
        requests.Start();

        var target = Path.Combine(dir.Path, "shot.png");
        Drop(paths, target);

        await arrived.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal([new OpenRequest(target)], got);
        Assert.Empty(Directory.EnumerateFiles(paths.OpenRequestsDir));
    }

    [Fact]
    public async Task A_request_before_any_window_waits_for_the_first()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var target = Path.Combine(dir.Path, "shot.png");
        Directory.CreateDirectory(paths.OpenRequestsDir);
        Drop(paths, target);

        using var requests = new OpenRequests(paths, new FakeClock(DateTimeOffset.UtcNow));
        requests.Start();
        await WaitUntil(() => !Directory.EnumerateFiles(paths.OpenRequestsDir).Any());

        var got = new List<OpenRequest>();
        using var _ = requests.Subscribe(got.Add);

        Assert.Equal([new OpenRequest(target)], got);
    }

    [Fact]
    public async Task A_request_left_from_long_ago_is_dropped()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        Directory.CreateDirectory(paths.OpenRequestsDir);
        var file = Drop(paths, Path.Combine(dir.Path, "shot.png"));
        File.SetLastWriteTimeUtc(file, clock.Now.UtcDateTime - OpenRequests.Stale - TimeSpan.FromMinutes(1));

        using var requests = new OpenRequests(paths, clock);
        requests.Start();
        await WaitUntil(() => !Directory.EnumerateFiles(paths.OpenRequestsDir).Any());

        var got = new List<OpenRequest>();
        using var _ = requests.Subscribe(got.Add);

        Assert.Empty(got);
    }

    [Fact]
    public void Opening_from_inside_the_app_says_whether_a_window_took_it()
    {
        using var dir = new TempDir();
        using var requests = new OpenRequests(new AppPaths(dir.Path), new FakeClock(DateTimeOffset.UtcNow));
        var target = new OpenRequest(Path.Combine(dir.Path, "shot.png"), 3);

        Assert.False(requests.Open(target));

        var got = new List<OpenRequest>();
        using var _ = requests.Subscribe(got.Add);
        Assert.Equal([target], got);

        Assert.True(requests.Open(target));
        Assert.Equal([target, target], got);
    }

    [Theory]
    [InlineData("shot.PNG", "image/png")]
    [InlineData("a/b/photo.jpeg", "image/jpeg")]
    [InlineData("icon.svg", "image/svg+xml")]
    [InlineData("Program.cs", null)]
    [InlineData("Makefile", null)]
    public void Images_are_known_by_extension(string path, string? type) =>
        Assert.Equal(type, ImageTypes.ContentType(path));

    /// <summary>Writes a request the way a caller should: under another name, then renamed into place.</summary>
    private static string Drop(AppPaths paths, string target)
    {
        var name = Guid.NewGuid().ToString("n");
        var temp = Path.Combine(paths.OpenRequestsDir, name + ".tmp");
        var final = Path.Combine(paths.OpenRequestsDir, name + ".json");
        File.WriteAllText(temp, $$"""{"path": {{System.Text.Json.JsonSerializer.Serialize(target)}}}""");
        File.Move(temp, final);
        return final;
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow + Wait;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out waiting.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        // The file goes before the request is handed on; give that a moment.
        await Task.Delay(100, TestContext.Current.CancellationToken);
    }
}
