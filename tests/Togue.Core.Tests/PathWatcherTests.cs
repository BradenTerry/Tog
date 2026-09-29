using System.Collections.Concurrent;
using System.Diagnostics;
using Togue.Core.Platform;

namespace Togue.Core.Tests;

public class PathWatcherTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_file_is_reported_under_the_path_as_given()
    {
        // The temp folder on macOS is /var/..., which FSEvents reports as
        // /private/var/..., so this also covers spelling paths back.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var seen = new ConcurrentQueue<string>();
            using var watcher = PathWatcher.Watch(dir.FullName, recursive: true, seen.Enqueue);
            var file = Path.Combine(dir.FullName, "a.txt");
            await File.WriteAllTextAsync(file, "x", Token);

            await Until(() => seen.Contains(file));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_shallow_watch_ignores_files_in_subfolders()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var deep = Directory.CreateDirectory(Path.Combine(dir.FullName, "sub")).FullName;
            var seen = new ConcurrentQueue<string>();
            using var watcher = PathWatcher.Watch(dir.FullName, recursive: false, seen.Enqueue);

            await File.WriteAllTextAsync(Path.Combine(deep, "deep.txt"), "x", Token);
            var top = Path.Combine(dir.FullName, "top.txt");
            await File.WriteAllTextAsync(top, "x", Token);

            await Until(() => seen.Contains(top));
            Assert.DoesNotContain(Path.Combine(deep, "deep.txt"), seen);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Nothing_is_reported_after_dispose()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var seen = new ConcurrentQueue<string>();
            PathWatcher.Watch(dir.FullName, recursive: true, seen.Enqueue).Dispose();

            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "a.txt"), "x", Token);
            await Task.Delay(500, Token);

            Assert.Empty(seen);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_missing_folder_throws_an_io_exception() =>
        Assert.ThrowsAny<IOException>(() =>
            PathWatcher.Watch(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n")), recursive: true, _ => { }));

    [Fact]
    public void Starting_a_watch_is_quick()
    {
        // The reason this type exists: .NET's watcher on macOS waits on sync()
        // as it starts, seconds on a busy machine.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < 5; i++)
            {
                PathWatcher.Watch(dir.FullName, recursive: true, _ => { }).Dispose();
            }

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Five watches took {clock.Elapsed}.");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting for the event.");
            await Task.Delay(20, Token);
        }
    }
}
