using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tog.Core.Agents;

/// <summary>A way to sign an agent in, as the agent offers it in <c>initialize</c>.</summary>
/// <param name="Id">The agent's id for the method, such as "claude-ai-login".</param>
/// <param name="Name">What to call it, such as "Claude Subscription".</param>
/// <param name="Command">The program that signs in.</param>
/// <param name="Arguments">Its arguments.</param>
public sealed record LoginMethod(string Id, string Name, string? Description, string Command, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// The terminal sign-ins in an <c>initialize</c> result's <c>authMethods</c>.
    /// Only those that name their own command (the <c>terminal-auth</c> meta
    /// form) are taken: the plain form means "run the agent again with these
    /// arguments", and an agent launched through a wrapper would not be that.
    /// </summary>
    public static IReadOnlyList<LoginMethod> Read(JsonElement initializeResult)
    {
        if (initializeResult.ValueKind != JsonValueKind.Object
            || !initializeResult.TryGetProperty("authMethods", out var methods)
            || methods.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var found = new List<LoginMethod>();
        foreach (var method in methods.EnumerateArray())
        {
            if (AcpClient.Text(method, "id") is not { } id
                || !method.TryGetProperty("_meta", out var meta)
                || meta.ValueKind != JsonValueKind.Object
                || !meta.TryGetProperty("terminal-auth", out var terminal)
                || AcpClient.Text(terminal, "command") is not { Length: > 0 } command)
            {
                continue;
            }

            var args = terminal.TryGetProperty("args", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToList()
                : [];
            found.Add(new LoginMethod(id, AcpClient.Text(method, "name") ?? id, AcpClient.Text(method, "description")?.Trim(), command, args));
        }

        return found;
    }
}

public enum LoginState { Running, Succeeded, Failed, Cancelled }

/// <summary>
/// One run of an agent's sign-in command, which opens the browser and waits for
/// the sign-in to come back.
/// </summary>
/// <remarks>
/// The command expects a terminal, so what it prints is kept for the UI: the
/// page it opened, in case the browser did not, and its last words when it
/// fails. A sign-in that cannot reach back to the machine falls back to asking
/// for a code from the page, which is why its input stays open.
/// </remarks>
public sealed partial class AgentLogin : IAsyncDisposable
{
    private const int KeptLines = 40;

    private readonly Process _process;
    private readonly Lock _gate = new();
    private readonly List<string> _output = [];

    private AgentLogin(string backendId, LoginMethod method, Process process)
    {
        BackendId = backendId;
        Method = method;
        _process = process;
    }

    public string BackendId { get; }

    public LoginMethod Method { get; }

    public LoginState State { get; private set; } = LoginState.Running;

    /// <summary>The first web address it printed: the sign-in page.</summary>
    public string? Url { get; private set; }

    /// <summary>Raised from a background thread whenever it prints or ends.</summary>
    public event Action? Changed;

    /// <summary>Its last lines of output, with terminal colours removed.</summary>
    public IReadOnlyList<string> Output
    {
        get
        {
            lock (_gate)
            {
                return [.. _output];
            }
        }
    }

    /// <summary>Ends with the run, whatever the outcome.</summary>
    public Task Completion => _done.Task;

    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Starts the sign-in. Throws <see cref="InvalidOperationException"/> when it cannot be run.</summary>
    public static AgentLogin Start(string backendId, LoginMethod method, string workingDirectory)
    {
        var psi = new ProcessStartInfo(method.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in method.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var login = new AgentLogin(backendId, method, process);
        process.OutputDataReceived += (_, e) => login.Take(e.Data);
        process.ErrorDataReceived += (_, e) => login.Take(e.Data);
        process.Exited += (_, _) => login.Finish();

        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException($"The sign-in could not be started ({method.Command}): {e.Message}", e);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return login;
    }

    /// <summary>Sends a code copied from the sign-in page, for when it asks for one.</summary>
    public async Task SendCodeAsync(string code)
    {
        if (State != LoginState.Running || string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        try
        {
            await _process.StandardInput.WriteLineAsync(code.Trim()).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // It ended as the code went in; its exit says how it went.
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (State != LoginState.Running)
            {
                return;
            }

            State = LoginState.Cancelled;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        _done.TrySetResult();
        Changed?.Invoke();
    }

    /// <summary>Whether a line it printed is worth showing, and the sign-in page if it names one.</summary>
    public static (string Line, string? Url) Clean(string raw)
    {
        var line = Ansi().Replace(raw, "").TrimEnd();
        var url = UrlPattern().Match(line) is { Success: true } m ? m.Value.TrimEnd('.', ',', ')') : null;
        return (line, url);
    }

    private void Take(string? raw)
    {
        if (raw is null)
        {
            return;
        }

        var (line, url) = Clean(raw);
        if (line.Trim().Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _output.Add(line);
            if (_output.Count > KeptLines)
            {
                _output.RemoveAt(0);
            }

            Url ??= url;
        }

        Changed?.Invoke();
    }

    private void Finish()
    {
        // The output events can still be draining when Exited fires; waiting
        // for them keeps the last line, usually the reason, from going missing.
        try
        {
            _process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
        }

        lock (_gate)
        {
            if (State == LoginState.Running)
            {
                State = _process.ExitCode == 0 ? LoginState.Succeeded : LoginState.Failed;
            }
        }

        _done.TrySetResult();
        Changed?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        Cancel();
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    [GeneratedRegex(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"https?://[^\s""'<>]+")]
    private static partial Regex UrlPattern();
}
