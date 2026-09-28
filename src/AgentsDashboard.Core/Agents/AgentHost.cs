using System.Text;
using System.Text.Json;
using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Monitoring;
using AgentsDashboard.Core.Platform;

namespace AgentsDashboard.Core.Agents;

/// <summary>Where a hosted agent is.</summary>
public enum HostedState
{
    /// <summary>Not running: its conversation is saved and a message picks it up again.</summary>
    Stopped,

    /// <summary>Running and waiting for a message.</summary>
    Idle,

    /// <summary>In the middle of a turn.</summary>
    Working,

    /// <summary>In the middle of a turn, blocked on a permission only you can give or questions only you can answer.</summary>
    Waiting,

    /// <summary>Its last turn ended in an error, or its process died under it.</summary>
    Failed,
}

/// <summary>One answer a permission prompt offers.</summary>
public sealed record PermissionChoice(string OptionId, string Name, string Kind)
{
    public bool Allows => Kind.StartsWith("allow", StringComparison.Ordinal);
}

/// <summary>A permission the agent is asking for, waiting on you.</summary>
/// <param name="Title">What it wants to do, as the agent titles it: a command, a file.</param>
/// <param name="Detail">Why, when the agent says.</param>
public sealed record PermissionAsk(string Key, string Title, string? Detail, IReadOnlyList<PermissionChoice> Choices);

/// <summary>A hosted agent as the UI sees it at one moment.</summary>
/// <param name="LiveText">What the agent has said so far in the part of the turn since its last tool call. Cleared when the turn ends, by which time the transcript has it.</param>
/// <param name="CurrentTool">The tool call in progress, by its title.</param>
/// <param name="Title">The name the agent gave the conversation, or one you picked it up under. Never the prompt.</param>
/// <param name="Prompt">The first thing it was asked, to stand in for a title until it has one.</param>
/// <param name="Context">How full its context window was when its last turn ended, if it has said.</param>
/// <param name="FolderGone">Its folder no longer exists, typically a worktree that was removed. Claude resumes a conversation only in the folder it started in, so it cannot carry on.</param>
/// <param name="Commands">The slash commands it takes, as it last listed them, or those another session listed while it has not.</param>
/// <param name="Questions">A form of questions it is waiting on you to answer, the oldest when there are several.</param>
public sealed record HostedAgent(
    string SessionId,
    string Cwd,
    string? Title,
    HostedState State,
    DateTimeOffset StateSince,
    DateTimeOffset AddedAt,
    string LiveText,
    string? CurrentTool,
    PermissionAsk? Permission,
    string? Error,
    IReadOnlyList<AcpConfigOption> Options,
    string? Prompt = null,
    ContextUsage? Context = null,
    bool FolderGone = false,
    IReadOnlyList<AcpCommand>? Commands = null,
    QuestionForm? Questions = null)
{
    /// <summary>What it is waiting on you for, in a few words, when it is.</summary>
    /// <remarks>An MCP server's form is named as one, never by its own message, which is the server's to write.</remarks>
    public string? WaitingFor => Permission?.Title ?? Questions switch
    {
        null => null,
        { Source: FormSource.McpServer } => QuestionForm.McpServerAsking,
        { Questions.Count: > 1 } form => $"{form.Questions.Count} questions for you",
        { Message.Length: > 0 } form => form.Message,
        var form => form.Questions.FirstOrDefault()?.Text ?? form.Questions.FirstOrDefault()?.Header ?? "A question",
    };
}

/// <summary>How much of an agent's context window its conversation takes up.</summary>
/// <param name="Used">Tokens in context: the last request's input, cached or not.</param>
/// <param name="Size">The model's context window, as the agent reports it.</param>
public sealed record ContextUsage(long Used, long Size)
{
    /// <summary>Used as a whole percentage of the window, capped at 100.</summary>
    public int Percent => Size <= 0 ? 0 : (int)Math.Min(100, Math.Round(Used * 100.0 / Size));
}

/// <summary>What to start an agent with. Blank settings leave the agent's own default.</summary>
public sealed record AgentStart(
    string Cwd,
    string? Prompt = null,
    string? Model = null,
    string? Effort = null,
    string? Mode = null,
    string? Title = null);

/// <summary>An agent's folder is gone, so its conversation cannot be resumed there.</summary>
public sealed class FolderGoneException(string folder)
    : InvalidOperationException($"{folder} no longer exists, so this agent cannot carry on. Its conversation can only be resumed in the folder it started in.")
{
    public string Folder { get; } = folder;
}

/// <summary>How an action went, with a sentence for the UI.</summary>
public sealed record HostResult(bool Ok, string Message, string? SessionId = null)
{
    public static HostResult Failed(string message) => new(false, message);
}

/// <summary>
/// Runs agents over the Agent Client Protocol, inside the dashboard's process.
/// </summary>
/// <remarks>
/// <para>
/// One agent process hosts every session: ACP is built for many sessions on one
/// connection, and one Node process per agent would cost memory for nothing. It
/// is started on first use and again after it dies. When it dies, every session
/// it held stops; their conversations are saved by the agent, so a message
/// resumes them.
/// </para>
/// <para>
/// The dashboard is the host, so closing it ends the agents with it. That is the
/// same trade editors make for local agents. The sessions it runs are kept in
/// <see cref="HostedAgentStore"/>, which is what brings them back to the agent list
/// as stopped agents on the next start.
/// </para>
/// <para>
/// Streamed text arrives a few tokens at a time. Every chunk updating the page
/// would redraw it hundreds of times a turn, so <see cref="Changed"/> is raised
/// at most ten times a second.
/// </para>
/// </remarks>
public sealed class AgentHost : IAgentSessionSource, IAsyncDisposable
{
    private readonly AgentBackend _backend;
    private readonly IAgentLauncher _launcher;
    private readonly HostedAgentStore _store;
    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly PlanUsageStore? _planStore;
    private readonly Dictionary<string, PlanLimit> _plan = new(StringComparer.Ordinal);
    private readonly Timer _flush;
    private readonly Lock _flushGate = new();
    private readonly IAgentMcpServers? _mcpServers;

    private IAgentProcess? _process;
    private AcpClient? _client;

    /// <summary>When the running agent process started. Background work begun before it died with the last one.</summary>
    private DateTimeOffset? _processStartedAt;
    private bool _dirty;
    private bool _recordChanged;
    private bool _planChanged;
    private IReadOnlyList<AcpConfigOption> _knownOptions = [];

    /// <summary>
    /// The commands the last session listed. The agent lists them only once a
    /// session is running, and a stopped agent is resumed by the message you are
    /// typing, so without these it would have none to offer. Skills and commands
    /// mostly live in your own settings, so another session's list is close.
    /// Each agent's list is saved with it, so this starts from the saved ones
    /// rather than empty after a restart, when no session has run yet.
    /// </summary>
    private IReadOnlyList<AcpCommand> _knownCommands = [];

    public AgentHost(
        AgentBackend backend,
        IAgentLauncher launcher,
        HostedAgentStore store,
        IClock? clock = null,
        PlanUsageStore? planStore = null,
        IAgentMcpServers? mcpServers = null)
    {
        _mcpServers = mcpServers;
        _backend = backend;
        _launcher = launcher;
        _store = store;
        _clock = clock ?? new SystemClock();
        _planStore = planStore;

        foreach (var limit in planStore?.Load() ?? [])
        {
            _plan[limit.Window] = limit;
        }

        foreach (var saved in store.Load())
        {
            _entries[saved.SessionId] = new Entry(saved.SessionId, saved.Cwd, saved.AddedAt, _clock.Now)
            {
                Title = saved.Title,
                Prompt = saved.Prompt,
                Context = saved.Context,
                Commands = saved.Commands,
            };

            if (saved.Commands is { Count: > 0 } commands)
            {
                _knownCommands = commands;
            }
        }

        _flush = new Timer(_ => Flush(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    /// <summary>Raised when any agent changes, at most ten times a second.</summary>
    public event Action? Changed;

    public AgentBackend Backend => _backend;

    /// <summary>
    /// The settings the agent offered the last session it started, such as its
    /// models and modes. Empty until one has started. The start form lists these
    /// rather than a list of its own, so it offers exactly what the agent has.
    /// </summary>
    public IReadOnlyList<AcpConfigOption> KnownOptions
    {
        get
        {
            lock (_gate)
            {
                return _knownOptions;
            }
        }
    }

    /// <summary>
    /// The subscription plan's usage limits as Claude last reported them, leaving
    /// out any whose window has since reset. Empty when signed in with an API key,
    /// which has no plan limits to report.
    /// </summary>
    /// <remarks>
    /// Claude only reports these when they change, on a turn of an agent hosted
    /// here, so they are as of the last such turn. Agents run in a terminal do not
    /// feed them.
    /// </remarks>
    public IReadOnlyList<PlanLimit> PlanLimits
    {
        get
        {
            var now = _clock.Now;
            lock (_gate)
            {
                return _plan.Values.Where(l => l.CurrentAt(now)).OrderBy(l => l.Rank).ToList();
            }
        }
    }

    public IReadOnlyList<HostedAgent> Agents
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Select(e => e.Snapshot(_knownCommands)).ToList();
            }
        }
    }

    public HostedAgent? Find(string sessionId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(sessionId, out var entry) ? entry.Snapshot(_knownCommands) : null;
        }
    }

    /// <summary>Starts a new conversation in a folder, and sends it the prompt if there is one.</summary>
    public async Task<HostResult> StartAsync(AgentStart start, CancellationToken ct = default)
    {
        try
        {
            var client = await ConnectAsync(ct).ConfigureAwait(false);
            var (sessionId, options) = await client.NewSessionAsync(start.Cwd, ct, _mcpServers?.For(start.Cwd)).ConfigureAwait(false);

            var entry = new Entry(sessionId, start.Cwd, _clock.Now, _clock.Now)
            {
                Title = start.Title,
                Prompt = Clip(start.Prompt),
                Attached = true,
                Options = options,
            };
            entry.SetState(HostedState.Idle, _clock.Now);

            lock (_gate)
            {
                _entries[sessionId] = entry;
                _knownOptions = options;
            }

            Save();
            await ApplyAsync(client, entry, start, ct).ConfigureAwait(false);
            Touch();

            if (!string.IsNullOrWhiteSpace(start.Prompt))
            {
                return await SendAsync(sessionId, start.Prompt, ct).ConfigureAwait(false);
            }

            return new HostResult(true, "Started.", sessionId);
        }
        catch (Exception e) when (IsAgentFailure(e))
        {
            return HostResult.Failed(Explain(e));
        }
    }

    /// <summary>
    /// Picks up a saved conversation, one of the agent's own or one started
    /// elsewhere, such as in a terminal. It joins the agent list like any other.
    /// </summary>
    public async Task<HostResult> ResumeAsync(string sessionId, AgentStart start, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(sessionId))
            {
                _entries[sessionId] = new Entry(sessionId, start.Cwd, _clock.Now, _clock.Now) { Title = start.Title };
            }
        }

        Save();
        try
        {
            var client = await AttachAsync(sessionId, ct).ConfigureAwait(false);
            Entry entry;
            lock (_gate)
            {
                entry = _entries[sessionId];
            }

            await ApplyAsync(client, entry, start, ct).ConfigureAwait(false);
            Touch();

            return string.IsNullOrWhiteSpace(start.Prompt)
                ? new HostResult(true, "Resumed.", sessionId)
                : await SendAsync(sessionId, start.Prompt, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsAgentFailure(e))
        {
            // A missing folder is not the agent failing: nothing ran, and the
            // chat already says why it cannot continue.
            if (e is not FolderGoneException)
            {
                Fail(sessionId, Explain(e));
            }

            return HostResult.Failed(Explain(e));
        }
    }

    /// <summary>
    /// Picks a stopped agent's session up again without sending it anything, so
    /// its commands and settings are live before the first message. Resuming
    /// starts no turn. A running agent is left as it is.
    /// </summary>
    public async Task<HostResult> WakeAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            await AttachAsync(sessionId, ct).ConfigureAwait(false);
            return new HostResult(true, "Resumed.", sessionId);
        }
        catch (Exception e) when (IsAgentFailure(e))
        {
            // A missing folder is not the agent failing: nothing ran, and the
            // chat already says why it cannot continue.
            if (e is not FolderGoneException)
            {
                Fail(sessionId, Explain(e));
            }

            return HostResult.Failed(Explain(e));
        }
    }

    /// <summary>
    /// Sends a message. A stopped agent is resumed first. A working one takes it
    /// as its next turn: the agent queues it rather than interrupting itself.
    /// </summary>
    public async Task<HostResult> SendAsync(string sessionId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return HostResult.Failed("Nothing to send.");
        }

        AcpClient client;
        try
        {
            client = await AttachAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsAgentFailure(e))
        {
            // A missing folder is not the agent failing: nothing ran, and the
            // chat already says why it cannot continue.
            if (e is not FolderGoneException)
            {
                Fail(sessionId, Explain(e));
            }

            return HostResult.Failed(Explain(e));
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var entry))
            {
                return HostResult.Failed("That agent is not here any more.");
            }

            entry.Turns++;
            entry.Error = null;
            if (entry.State is not (HostedState.Working or HostedState.Waiting))
            {
                entry.Live.Clear();
                entry.CurrentTool = null;
                entry.SetState(HostedState.Working, _clock.Now);
            }

            entry.Prompt ??= Clip(text);
        }

        Touch();
        _ = RunTurnAsync(client, sessionId, text);
        return new HostResult(true, "Sent.", sessionId);
    }

    /// <summary>Stops the turn in progress. The agent stays, idle, with everything so far kept.</summary>
    public async Task<HostResult> CancelAsync(string sessionId, CancellationToken ct = default)
    {
        AcpClient? client;
        lock (_gate)
        {
            client = _client;
            if (_entries.TryGetValue(sessionId, out var entry))
            {
                entry.Abandon();
                Resume(entry);
            }
        }

        if (client is null)
        {
            return new HostResult(true, "Nothing was running.");
        }

        try
        {
            await client.CancelAsync(sessionId, ct).ConfigureAwait(false);
            return new HostResult(true, "Stopping the turn.");
        }
        catch (Exception e) when (IsAgentFailure(e))
        {
            return HostResult.Failed(Explain(e));
        }
    }

    /// <summary>Ends the agent's session. It moves to stopped and a message resumes it.</summary>
    public async Task<HostResult> StopAsync(string sessionId, CancellationToken ct = default)
    {
        AcpClient? client;
        bool attached;
        lock (_gate)
        {
            client = _client;
            attached = _entries.TryGetValue(sessionId, out var entry) && entry.Attached;
            entry?.Abandon();
        }

        if (attached && client is not null)
        {
            try
            {
                await client.CancelAsync(sessionId, ct).ConfigureAwait(false);
                await client.CloseSessionAsync(sessionId, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (IsAgentFailure(e))
            {
                // Closing is tidying up. The session is treated as stopped either way.
            }
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var entry))
            {
                entry.Attached = false;
                entry.Turns = 0;
                entry.Live.Clear();
                entry.CurrentTool = null;
                entry.Abandon();
                entry.SetState(HostedState.Stopped, _clock.Now);
            }
        }

        Touch();
        return new HostResult(true, "Stopped. Its conversation is kept; send it a message to pick it up again.");
    }

    /// <summary>Takes an agent off the agent list. Its conversation stays saved and can be resumed.</summary>
    public async Task<HostResult> RemoveAsync(string sessionId, CancellationToken ct = default)
    {
        await StopAsync(sessionId, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _entries.Remove(sessionId);
        }

        Save();
        Touch();
        return new HostResult(true, "Removed. The conversation is still there under New agent, Resume.");
    }

    /// <summary>Answers the agent's permission prompt. A null option declines without choosing.</summary>
    public void Answer(string sessionId, string key, string? optionId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var entry) && entry.Permission is { } pending && pending.Ask.Key == key)
            {
                pending.Answer.TrySetResult(optionId);
            }
        }
    }

    /// <summary>
    /// Answers the agent's open form of questions. Null content skips it: the
    /// agent is told you chose not to answer, and carries on.
    /// </summary>
    public void AnswerQuestions(string sessionId, string key, IReadOnlyDictionary<string, object>? content)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var entry)
                && entry.Questions.FirstOrDefault(q => q.Form.Key == key) is { } pending)
            {
                pending.Answer.TrySetResult(content is null ? FormReply.Decline : FormReply.Accept(content));
            }
        }
    }

    /// <summary>The agents running in this process, for the monitor: stopped ones are not running.</summary>
    public IReadOnlyList<AgentSession> Read()
    {
        int pid;
        DateTimeOffset? since;
        List<HostedAgent> running;
        lock (_gate)
        {
            pid = _process?.ProcessId ?? 0;
            since = _processStartedAt;
            running = _entries.Values.Where(e => e.Attached).Select(e => e.Snapshot(_knownCommands)).ToList();
        }

        return running.Select(a => new AgentSession
        {
            SessionId = a.SessionId,
            Pid = pid,
            Cwd = a.Cwd,
            Status = a.State switch
            {
                HostedState.Working => AgentStatus.Active,
                HostedState.Waiting => AgentStatus.Waiting,
                _ => AgentStatus.Idle,
            },
            WaitingFor = a.WaitingFor,
            Name = a.Title,
            StartedAt = a.AddedAt,
            LastActivity = a.StateSince,
            StatusSince = a.StateSince,
            IsBackground = true,
            JobId = a.SessionId,
            ProcessStartedAt = since,
        }).ToList();
    }

    private async Task RunTurnAsync(AcpClient client, string sessionId, string text)
    {
        string? error = null;
        try
        {
            await client.PromptAsync(sessionId, text).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The agent process went away. The connection closing is what records
            // that, for every session at once and with the agent's last words.
            return;
        }
        catch (Exception e) when (IsAgentFailure(e))
        {
            error = Explain(e);
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var entry))
            {
                return;
            }

            entry.Turns = Math.Max(0, entry.Turns - 1);
            if (error is not null)
            {
                entry.Error = error;
            }

            if (entry.Turns == 0 && entry.Attached)
            {
                entry.Live.Clear();
                entry.CurrentTool = null;

                // The turn is over, so nothing still open will be read: the agent
                // stopped waiting for it when the turn ended.
                entry.Abandon();
                entry.SetState(entry.Error is null ? HostedState.Idle : HostedState.Failed, _clock.Now);
            }
        }

        Touch();
    }

    /// <summary>Makes sure the session is live on the current connection, resuming it when it is not.</summary>
    private async Task<AcpClient> AttachAsync(string sessionId, CancellationToken ct)
    {
        var client = await ConnectAsync(ct).ConfigureAwait(false);

        string cwd;
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var entry))
            {
                throw new InvalidOperationException("That agent is not here any more.");
            }

            if (entry.Attached)
            {
                return client;
            }

            cwd = entry.Cwd;
        }

        if (!Directory.Exists(cwd))
        {
            throw new FolderGoneException(cwd);
        }

        var options = await client.ResumeSessionAsync(sessionId, cwd, ct, _mcpServers?.For(cwd)).ConfigureAwait(false);
        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var entry))
            {
                entry.Attached = true;
                entry.Options = options.Count > 0 ? options : entry.Options;
                entry.Error = null;
                entry.SetState(HostedState.Idle, _clock.Now);
                if (options.Count > 0)
                {
                    _knownOptions = options;
                }
            }
        }

        Touch();
        return client;
    }

    /// <summary>Sets the model, effort and mode asked for, where the agent offers them. The rest are left alone.</summary>
    private async Task ApplyAsync(AcpClient client, Entry entry, AgentStart start, CancellationToken ct)
    {
        // Model first: changing it can reset the others, as a model without a
        // mode falls back to the nearest one it has.
        foreach (var (id, wanted) in new[] { ("model", start.Model), ("effort", start.Effort), ("mode", start.Mode) })
        {
            if (string.IsNullOrWhiteSpace(wanted))
            {
                continue;
            }

            IReadOnlyList<AcpConfigOption> options;
            lock (_gate)
            {
                options = entry.Options;
            }

            if (Match(options, id, wanted) is not { } value)
            {
                continue;
            }

            try
            {
                var updated = await client.SetConfigOptionAsync(entry.SessionId, id, value, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (updated.Count > 0)
                    {
                        entry.Options = updated;
                    }
                }
            }
            catch (JsonRpcException)
            {
                // The agent turned the value down. Its default stands.
            }
        }
    }

    /// <summary>
    /// The value to set for a wanted setting: the choice itself when the agent
    /// offers it, or the one whose value names it, so "opus" finds "opus[1m]" and
    /// "manual" finds the mode the agent calls Manual.
    /// </summary>
    public static string? Match(IReadOnlyList<AcpConfigOption> options, string id, string wanted)
    {
        var option = options.FirstOrDefault(o => o.Id == id);
        if (option is null)
        {
            return null;
        }

        return option.Choices.FirstOrDefault(c => string.Equals(c.Value, wanted, StringComparison.OrdinalIgnoreCase))?.Value
            ?? option.Choices.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase))?.Value
            ?? option.Choices.FirstOrDefault(c => c.Value.Contains(wanted, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private async Task<AcpClient> ConnectAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_client is { Connection.IsClosed: false } live)
            {
                return live;
            }
        }

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_client is { Connection.IsClosed: false } live)
                {
                    return live;
                }
            }

            var launchedAt = _clock.Now;
            var process = _launcher.Launch(_backend);
            var rpc = new JsonRpcConnection(process.Input, process.Output);
            var client = new AcpClient(rpc);

            rpc.Notified += (method, parameters) => OnNotified(method, parameters);
            rpc.RequestHandler = OnRequestAsync;
            rpc.Closed += reason => OnClosed(rpc, process, reason);
            rpc.Start();

            try
            {
                await client.InitializeAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await rpc.DisposeAsync().ConfigureAwait(false);
                await process.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            lock (_gate)
            {
                _process = process;
                _client = client;
                _processStartedAt = launchedAt;
            }

            return client;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private void OnClosed(JsonRpcConnection rpc, IAgentProcess process, Exception? reason)
    {
        var why = process.RecentErrors.Split('\n').LastOrDefault(l => l.Trim().Length > 0);
        lock (_gate)
        {
            if (_client?.Connection != rpc)
            {
                return;
            }

            _client = null;
            _process = null;
            foreach (var entry in _entries.Values.Where(e => e.Attached))
            {
                entry.Attached = false;
                entry.Abandon();
                entry.Live.Clear();
                entry.CurrentTool = null;
                var wasWorking = entry.Turns > 0;
                entry.Turns = 0;
                if (wasWorking)
                {
                    entry.Error = $"{_backend.Name} stopped in the middle of a turn." + (why is null ? "" : $" Its last words: {why}");
                }

                entry.SetState(wasWorking ? HostedState.Failed : HostedState.Stopped, _clock.Now);
            }
        }

        _ = process.DisposeAsync().AsTask();
        Touch();
    }

    private void OnNotified(string method, JsonElement parameters)
    {
        if (method != "session/update"
            || AcpClient.Text(parameters, "sessionId") is not { } sessionId
            || !parameters.TryGetProperty("update", out var update))
        {
            return;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var entry))
            {
                return;
            }

            switch (AcpClient.Text(update, "sessionUpdate"))
            {
                case "agent_message_chunk":
                    if (update.TryGetProperty("content", out var content) && AcpClient.Text(content, "text") is { } text)
                    {
                        entry.Live.Append(text);
                    }

                    break;

                case "tool_call":
                    // Whatever was said before a tool call is a finished message,
                    // written to the transcript before the tool runs, so the live
                    // text starts again from here.
                    entry.Live.Clear();
                    entry.CurrentTool = AcpClient.Text(update, "title") ?? entry.CurrentTool;
                    break;

                case "tool_call_update":
                    if (AcpClient.Text(update, "title") is { } title)
                    {
                        entry.CurrentTool = title;
                    }

                    if (AcpClient.Text(update, "status") is "completed" or "failed")
                    {
                        entry.CurrentTool = null;
                    }

                    break;

                case "current_mode_update":
                    if (AcpClient.Text(update, "currentModeId") is { } mode)
                    {
                        entry.Options = entry.Options
                            .Select(o => o.Id == "mode" ? o with { Current = mode } : o)
                            .ToList();
                    }

                    break;

                case "config_option_update":
                    var options = AcpClient.ReadOptions(update);
                    if (options.Count > 0)
                    {
                        entry.Options = options;
                    }

                    break;

                case "usage_update":
                    // Sent when a turn ends and after a compaction, not while the
                    // turn runs, so the figure is as of the last turn.
                    if (AcpClient.Number(update, "used") is { } used
                        && AcpClient.Number(update, "size") is { } size and > 0)
                    {
                        entry.Context = new ContextUsage(used, size);
                        _recordChanged = true;
                    }

                    // The bridge passes the plan's rate limits along on the same
                    // update, but only when the SDK reports that they changed.
                    if (update.TryGetProperty("_meta", out var meta)
                        && meta.ValueKind == JsonValueKind.Object
                        && meta.TryGetProperty("_claude/rateLimit", out var rateLimit))
                    {
                        foreach (var limit in PlanLimit.Read(rateLimit, _clock.Now))
                        {
                            _plan[limit.Window] = limit.After(_plan.GetValueOrDefault(limit.Window));
                            _planChanged = true;
                        }
                    }

                    break;

                case "available_commands_update":
                    var listed = AcpClient.ReadCommands(update);
                    if (entry.Commands is null || !entry.Commands.SequenceEqual(listed))
                    {
                        _recordChanged = true;
                    }

                    entry.Commands = listed;
                    _knownCommands = listed;
                    break;

                case "session_info_update":
                    if (AcpClient.Text(update, "title") is { Length: > 0 } named
                        && named != entry.Title
                        && !IsPromptEcho(named, entry.Prompt))
                    {
                        // The agent names a conversation once it knows what it is
                        // about, which is a better label than the first prompt, so
                        // it is kept for the next start as well.
                        entry.Title = named;
                        _recordChanged = true;
                    }

                    break;

                default:
                    return;
            }

            _dirty = true;
        }
    }

    private Task<object?> OnRequestAsync(string method, JsonElement parameters, CancellationToken ct) => method switch
    {
        "session/request_permission" => OnPermissionAsync(parameters, ct),
        "elicitation/create" => OnQuestionsAsync(parameters, ct),
        _ => throw new JsonRpcException(JsonRpcConnection.MethodNotFound, $"{method} is not offered by the dashboard."),
    };

    private async Task<object?> OnPermissionAsync(JsonElement parameters, CancellationToken ct)
    {
        var sessionId = AcpClient.Text(parameters, "sessionId") ?? "";
        var toolCall = parameters.TryGetProperty("toolCall", out var call) ? call : default;
        var choices = parameters.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
            ? options.EnumerateArray()
                .Select(o => new PermissionChoice(
                    AcpClient.Text(o, "optionId") ?? "",
                    AcpClient.Text(o, "name") ?? AcpClient.Text(o, "optionId") ?? "",
                    AcpClient.Text(o, "kind") ?? ""))
                .Where(o => o.OptionId.Length > 0)
                .ToList()
            : [];

        var ask = new PermissionAsk(
            Guid.NewGuid().ToString("n"),
            AcpClient.Text(toolCall, "title") ?? "A tool call",
            Detail(toolCall),
            choices);
        var pending = new PendingPermission(ask);

        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var entry))
            {
                return Declined();
            }

            entry.Permission = pending;
            entry.SetState(HostedState.Waiting, _clock.Now);
        }

        Touch();

        string? chosen;
        using (ct.Register(() => pending.Answer.TrySetResult(null)))
        {
            chosen = await pending.Answer.Task.ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var entry) && entry.Permission == pending)
            {
                entry.Permission = null;
                Resume(entry);
            }
        }

        Touch();

        return chosen is null
            ? Declined()
            : new { outcome = new { outcome = "selected", optionId = chosen } };

        static object Declined() => new { outcome = new { outcome = "cancelled" } };
    }

    /// <summary>
    /// A form of questions from the agent: AskUserQuestion, an MCP server's
    /// elicitation, or the bridge asking whether to retry a refused request on
    /// another model. Only form mode is advertised; anything else, a form with no
    /// session, and a form with a required field the dashboard cannot draw are
    /// declined at once, which the bridge treats as the user saying no.
    /// </summary>
    private async Task<object?> OnQuestionsAsync(JsonElement parameters, CancellationToken ct)
    {
        var sessionId = AcpClient.Text(parameters, "sessionId");
        var form = QuestionForm.Parse(parameters, Guid.NewGuid().ToString("n"));
        if (sessionId is null || form is null)
        {
            return FormReply.Decline.ToWire();
        }

        var pending = new PendingQuestions(form);
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var entry))
            {
                return FormReply.Decline.ToWire();
            }

            entry.Questions.Add(pending);
            entry.SetState(HostedState.Waiting, _clock.Now);
        }

        Touch();

        FormReply reply;
        using (ct.Register(() => pending.Answer.TrySetResult(FormReply.Cancel)))
        {
            reply = await pending.Answer.Task.ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var entry) && entry.Questions.Remove(pending))
            {
                Resume(entry);
            }
        }

        Touch();
        return reply.ToWire();
    }

    /// <summary>
    /// Out of waiting once nothing is left waiting on you: back to working if the
    /// turn is still going, or idle if it is not, as with a form asked outside a turn.
    /// </summary>
    private void Resume(Entry entry)
    {
        if (entry.Permission is not null || entry.Questions.Count > 0 || !entry.Attached)
        {
            return;
        }

        if (entry.Turns > 0)
        {
            entry.SetState(HostedState.Working, _clock.Now);
        }
        else if (entry.State == HostedState.Waiting)
        {
            entry.SetState(HostedState.Idle, _clock.Now);
        }
    }

    /// <summary>The line under a permission's title: the agent's own description of the call, or its command.</summary>
    private static string? Detail(JsonElement toolCall)
    {
        if (toolCall.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (toolCall.TryGetProperty("rawInput", out var input) && AcpClient.Text(input, "description") is { } description)
        {
            return description;
        }

        if (toolCall.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var inner) && AcpClient.Text(inner, "text") is { } text)
                {
                    return text;
                }
            }
        }

        return null;
    }

    private void Fail(string sessionId, string error)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var entry))
            {
                entry.Error = error;
                entry.SetState(HostedState.Failed, _clock.Now);
            }
        }

        Touch();
    }

    private void Save()
    {
        List<HostedAgentRecord> records;
        lock (_gate)
        {
            records = _entries.Values
                .OrderBy(e => e.AddedAt)
                .Select(e => new HostedAgentRecord(e.SessionId, e.Cwd, e.Title, e.AddedAt, e.Prompt, e.Context, e.Commands))
                .ToList();
        }

        try
        {
            _store.Save(records);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The list is what brings agents back after a restart. Losing one
            // save only costs that, and failing the action over it would cost more.
        }
    }

    private void Touch()
    {
        lock (_gate)
        {
            _dirty = true;
        }

        Flush();
    }

    private void Flush()
    {
        if (FlushOnce())
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Takes what changed and writes it, one caller at a time. Touch flushes on
    /// whichever thread noticed the change, so without the gate a second caller
    /// could find nothing left to take and return while the first was still
    /// writing, and the flush in DisposeAsync would then not mean it was written.
    /// </summary>
    private bool FlushOnce()
    {
        lock (_flushGate)
        {
            bool save;
            List<PlanLimit>? plan = null;
            lock (_gate)
            {
                if (!_dirty)
                {
                    return false;
                }

                _dirty = false;
                save = _recordChanged;
                _recordChanged = false;
                if (_planChanged)
                {
                    plan = [.. _plan.Values];
                    _planChanged = false;
                }
            }

            if (save)
            {
                Save();
            }

            if (plan is not null)
            {
                try
                {
                    _planStore?.Save(plan);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Only costs the figures showing before the next turn after a restart.
                }
            }

            return true;
        }
    }

    private static bool IsAgentFailure(Exception e) =>
        e is JsonRpcException or IOException or InvalidOperationException
            or OperationCanceledException or System.ComponentModel.Win32Exception;

    private string Explain(Exception e) => e switch
    {
        JsonRpcException rpc => $"{_backend.Name} refused: {rpc.Message}",
        IOException => $"{_backend.Name} stopped unexpectedly. {(_process?.RecentErrors.Split('\n').LastOrDefault() ?? "")}".Trim(),
        _ => e.Message,
    };

    /// <summary>
    /// The prompt as kept in place of a title: long enough that <see cref="IsPromptEcho"/>
    /// can still recognise the bridge's 256-character cut of it.
    /// </summary>
    private static string? Clip(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var flat = Flat(prompt);
        return flat.Length > 1000 ? flat[..1000] : flat;
    }

    /// <summary>
    /// Whether a "title" from the agent is only the first prompt handed back.
    /// </summary>
    /// <remarks>
    /// The bridge asks the CLI to generate a title when the first turn ends. When
    /// it cannot, it falls back to the SDK's session summary, which for a session
    /// it drives is the raw first prompt, flattened and cut at 256 characters with
    /// an ellipsis. Taking that as a title would dress the prompt up as a name the
    /// agent chose, so it is refused and the prompt stays labelled as a prompt.
    /// </remarks>
    public static bool IsPromptEcho(string title, string? prompt)
    {
        if (prompt is null)
        {
            return false;
        }

        var flatTitle = Flat(title);
        var flatPrompt = Flat(prompt);
        return flatTitle == flatPrompt
               || (flatTitle.EndsWith('\u2026') && flatTitle.Length > 1 && flatPrompt.StartsWith(flatTitle[..^1], StringComparison.Ordinal));
    }

    private static string Flat(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public async ValueTask DisposeAsync()
    {
        await _flush.DisposeAsync().ConfigureAwait(false);

        // Whatever changed since the last tick, such as a title or the plan's
        // limits arriving just before the app closed, would otherwise be lost.
        Flush();

        AcpClient? client;
        IAgentProcess? process;
        lock (_gate)
        {
            client = _client;
            process = _process;
            _client = null;
            _process = null;
        }

        if (client is not null)
        {
            await client.Connection.DisposeAsync().ConfigureAwait(false);
        }

        if (process is not null)
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }

        _connectGate.Dispose();
    }

    private sealed class PendingPermission(PermissionAsk ask)
    {
        public PermissionAsk Ask { get; } = ask;

        public TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PendingQuestions(QuestionForm form)
    {
        public QuestionForm Form { get; } = form;

        public TaskCompletionSource<FormReply> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// How a form is answered: accept with the answers, decline (you skipped it,
    /// and the agent carries on knowing that), or cancel (it was abandoned, and
    /// the tool call that asked is aborted).
    /// </summary>
    private sealed record FormReply(string Action, IReadOnlyDictionary<string, object>? Content = null)
    {
        public static readonly FormReply Decline = new("decline");
        public static readonly FormReply Cancel = new("cancel");

        public static FormReply Accept(IReadOnlyDictionary<string, object> content) => new("accept", content);

        public object ToWire() => Content is null ? new { action = Action } : new { action = Action, content = Content };
    }

    /// <summary>A hosted agent's mutable state, only touched under the host's lock.</summary>
    private sealed class Entry(string sessionId, string cwd, DateTimeOffset addedAt, DateTimeOffset now)
    {
        public string SessionId { get; } = sessionId;
        public string Cwd { get; } = cwd;
        public DateTimeOffset AddedAt { get; } = addedAt;
        public string? Title { get; set; }
        public string? Prompt { get; set; }
        public ContextUsage? Context { get; set; }
        public bool Attached { get; set; }
        public int Turns { get; set; }
        public HostedState State { get; private set; } = HostedState.Stopped;
        public DateTimeOffset StateSince { get; private set; } = now;
        public StringBuilder Live { get; } = new();
        public string? CurrentTool { get; set; }
        public PendingPermission? Permission { get; set; }

        /// <summary>Forms of questions open, oldest first. Parallel subagents can each ask.</summary>
        public List<PendingQuestions> Questions { get; } = [];
        public string? Error { get; set; }
        public IReadOnlyList<AcpConfigOption> Options { get; set; } = [];
        public IReadOnlyList<AcpCommand>? Commands { get; set; }

        public void SetState(HostedState state, DateTimeOffset at)
        {
            if (state != State)
            {
                State = state;
                StateSince = at;
            }
        }

        /// <summary>Settles everything open as abandoned: the permission declined, the questions cancelled.</summary>
        public void Abandon()
        {
            Permission?.Answer.TrySetResult(null);
            Permission = null;
            foreach (var pending in Questions)
            {
                pending.Answer.TrySetResult(FormReply.Cancel);
            }

            Questions.Clear();
        }

        public HostedAgent Snapshot(IReadOnlyList<AcpCommand> knownCommands) => new(
            SessionId, Cwd, Title, State, StateSince, AddedAt,
            Live.ToString(), CurrentTool, Permission?.Ask, Error, Options, Prompt, Context,
            FolderGone: !Directory.Exists(Cwd),
            Commands: Commands ?? knownCommands,
            Questions: Questions.FirstOrDefault()?.Form);
    }
}
