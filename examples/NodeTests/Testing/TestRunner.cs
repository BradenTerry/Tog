using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NodeTests.Testing;

/// <summary>
/// The tests of every worktree that has been looked at, and the run in
/// progress in each. Shared by every window.
/// </summary>
public sealed class TestRunner(ILogger<TestRunner> log) : IDisposable
{
    private readonly ConcurrentDictionary<string, WorktreeTests> _state = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new(StringComparer.Ordinal);

    // Cancelled when the extension unloads, which stops every run with it.
    private readonly CancellationTokenSource _unloading = new();

    /// <summary>Raised from a background thread whenever any worktree's tests change.</summary>
    public event Action? Changed;

    public WorktreeTests For(string worktree) => _state.GetValueOrDefault(worktree, WorktreeTests.Empty);

    /// <summary>Reads the worktree's test files again. Runs nothing.</summary>
    public async Task ListAsync(string worktree)
    {
        if (_runs.ContainsKey(worktree))
        {
            return;
        }

        try
        {
            var files = await Task.Run(() => Discovery.Find(worktree, _unloading.Token), _unloading.Token);
            Update(worktree, s => s with { Files = files, Listed = true, Error = null });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Update(worktree, s => s with { Listed = true, Error = e.Message });
        }
    }

    /// <summary>Runs one file, or every file when <paramref name="only"/> is null. One run per worktree at a time.</summary>
    public async Task RunAsync(string worktree, string? only = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_unloading.Token);
        if (!_runs.TryAdd(worktree, cts))
        {
            cts.Dispose();
            return;
        }

        try
        {
            // Listed again first, so a file an agent has just written is included.
            var files = await Task.Run(() => Discovery.Find(worktree, cts.Token), cts.Token);
            var chosen = files.Where(f => only is null || f.Path == only).Select(f => f.Path).ToHashSet();

            Update(worktree, s => s with
            {
                Listed = true,
                Running = true,
                Error = null,
                Files = [.. files.Select(f => chosen.Contains(f.Path) ? Pending(f) : Previous(s, f))],
            });

            // One file at a time, so each file's results show as soon as it ends.
            foreach (var file in files.Where(f => chosen.Contains(f.Path)))
            {
                var done = await NodeTestProcess.RunAsync(worktree, file, cts.Token);
                Update(worktree, s => s with { Files = [.. s.Files.Select(f => f.Path == done.Path ? done : f)] });
            }

            Update(worktree, s => s with { LastRun = DateTimeOffset.Now });
        }
        catch (OperationCanceledException)
        {
            // What had finished stays; what had not goes back to not run.
            Update(worktree, s => s with { Files = [.. s.Files.Select(Unstarted)] });
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Test run in {Worktree} failed", worktree);
            Update(worktree, s => s with { Error = e.Message, Files = [.. s.Files.Select(Unstarted)] });
        }
        finally
        {
            _runs.TryRemove(worktree, out _);
            cts.Dispose();
            Update(worktree, s => s with { Running = false });
        }
    }

    public void Stop(string worktree)
    {
        if (_runs.TryGetValue(worktree, out var cts))
        {
            cts.Cancel();
        }
    }

    public void Dispose()
    {
        _unloading.Cancel();
        _unloading.Dispose();
    }

    private static TestFile Pending(TestFile file) =>
        file with { Status = TestStatus.Running, Error = null, Tests = [.. file.Tests.Select(t => t with { Status = TestStatus.Running, Failure = null })] };

    // A file left out of this run keeps its last results rather than the bare listing.
    private static TestFile Previous(WorktreeTests state, TestFile listed) =>
        state.Files.FirstOrDefault(f => f.Path == listed.Path) ?? listed;

    private static TestFile Unstarted(TestFile file) =>
        file.Status != TestStatus.Running
            ? file
            : file with { Status = TestStatus.NotRun, Tests = [.. file.Tests.Select(t => t with { Status = TestStatus.NotRun })] };

    private void Update(string worktree, Func<WorktreeTests, WorktreeTests> change)
    {
        _state.AddOrUpdate(worktree, _ => change(WorktreeTests.Empty), (_, s) => change(s));
        Changed?.Invoke();
    }
}
