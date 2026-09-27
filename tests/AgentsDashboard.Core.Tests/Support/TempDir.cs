namespace AgentsDashboard.Core.Tests.Support;

/// <summary>A directory that cleans itself up, for tests that need real files.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "agents-dashboard-tests",
            Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string Dir(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            // Git writes its objects read-only, and on Windows a read-only file
            // stops Directory.Delete.
            if (OperatingSystem.IsWindows())
            {
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    System.IO.File.SetAttributes(file, FileAttributes.Normal);
                }
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A test that leaves a handle open should not fail the run over it.
        }
    }
}


/// <summary>A clock the test drives.</summary>
public sealed class FakeClock(DateTimeOffset? start = null) : AgentsDashboard.Core.Platform.IClock
{
    public DateTimeOffset Now { get; private set; } = start ?? new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Symbolic links for tests, skipping the test where the OS will not make one.</summary>
public static class Links
{
    // Windows only lets an administrator, or a machine in Developer Mode, create
    // a link. CI runs as an administrator; a developer's machine may not.
    public static void File(string link, string target) => Make(() => System.IO.File.CreateSymbolicLink(link, target));

    public static void Directory(string link, string target) => Make(() => System.IO.Directory.CreateSymbolicLink(link, target));

    private static void Make(Action create)
    {
        try
        {
            create();
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine cannot create symbolic links: {e.Message}");
        }
    }
}
