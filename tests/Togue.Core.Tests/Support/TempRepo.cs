using System.Diagnostics;

namespace Togue.Core.Tests.Support;

/// <summary>
/// A real git repository in a temporary directory.
/// </summary>
/// <remarks>
/// The git layer is a parser over git's own output, so the only test worth
/// writing for it runs the real git. A faked process would only prove that the
/// parser agrees with the fake.
/// </remarks>
public sealed class TempRepo : IDisposable
{
    private readonly TempDir _dir = new();

    public TempRepo()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Git("config", "commit.gpgsign", "false");

        // Git for Windows defaults to autocrlf=true, which would check files out
        // with CRLF and make every assertion on file text depend on the machine.
        Git("config", "core.autocrlf", "false");
    }

    public string Path => _dir.Path;

    public void Write(string relative, string content) => _dir.File(relative, content);

    public void Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("git could not be started");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}{stdout}");
        }

        return stdout.TrimEnd('\n', '\r');
    }

    public void Dispose() => _dir.Dispose();
}
