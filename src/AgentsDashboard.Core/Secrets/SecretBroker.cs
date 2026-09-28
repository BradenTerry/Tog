using System.Text;
using AgentsDashboard.Core.Platform;

namespace AgentsDashboard.Core.Secrets;

/// <summary>Which build of which extension is asking. The app vouches for all three.</summary>
/// <param name="Hash">The SHA-256 of the entry assembly that is running, as loaded.</param>
public sealed record SecretCaller(string ExtensionId, string ExtensionName, string Hash);

/// <summary>An extension waiting for you to answer about a secret.</summary>
/// <param name="Host">For a brokered request, where it is going.</param>
/// <param name="Purpose">What the extension says it wants it for. Its own words, not checked.</param>
public sealed record SecretRequest(SecretCaller Caller, string Name, SecretAccess Access, string? Host, string? Purpose);

/// <summary>How the app puts a secret on a brokered request.</summary>
/// <param name="Header">The header it goes in.</param>
/// <param name="Scheme">Written before it, as in <c>Bearer</c>, or null for the value alone.</param>
/// <param name="Base64">Encode the value first, as Basic authentication wants for <c>user:token</c>.</param>
public sealed record SecretPlacement(string Header, string? Scheme, bool Base64);

/// <summary>
/// Hands secrets to extensions you approved, asks you about the rest, and
/// sends requests with a secret on an extension's behalf.
/// </summary>
/// <remarks>
/// <para>
/// A call waits until it is answered. Every call about the same thing (the
/// same build of an extension, the same secret, the same access and host)
/// shares one prompt, so a worker's retries and a view's renders do not pile
/// up questions. Your answer is kept with the extension's hash, and a no is
/// kept too, so an extension that asks on a timer is answered from the
/// catalog after the first time rather than asking you again.
/// </para>
/// <para>
/// None of this is isolation. An extension runs in the app's process with its
/// types, and one that means harm can reach this object, or the OS store, by
/// reflection. The approval keeps a well-behaved extension to the secrets you
/// meant it to have; the boundary is still consenting to its code.
/// </para>
/// </remarks>
public sealed class SecretBroker : IDisposable
{
    private readonly SecretCatalog _catalog;
    private readonly ISecretVault _vault;
    private readonly IClock _clock;
    private readonly HttpClient _http;
    private readonly Lock _gate = new();
    private readonly Dictionary<Key, (SecretRequest Request, TaskCompletionSource<bool> Prompt)> _pending = [];

    /// <summary>What one prompt is about. Not the purpose, which is the extension's words and may vary per call.</summary>
    private sealed record Key(SecretCaller Caller, string Name, SecretAccess Access, string? Host)
    {
        public static Key Of(SecretRequest r) => new(r.Caller, r.Name, r.Access, r.Host);
    }

    /// <summary>Set while an agent tool runs, and in everything it starts. See <see cref="ForAgent"/>.</summary>
    private static readonly AsyncLocal<bool> AnsweringAgent = new();

    /// <summary>
    /// Marks the work of answering an agent's tool call, until disposed. A
    /// secret asked for inside it, or in a task it starts, is refused with an
    /// exception rather than a prompt: an agent must never be handed a secret
    /// through a tool, nor be able to put a prompt in front of you with words
    /// it chose. It flows with the execution context, so code that goes out
    /// of its way to drop that (a queue a worker drains) is not caught; the
    /// extension is trusted code either way.
    /// </summary>
    public static IDisposable ForAgent()
    {
        AnsweringAgent.Value = true;
        return new AgentScope();
    }

    private sealed class AgentScope : IDisposable
    {
        public void Dispose() => AnsweringAgent.Value = false;
    }

    internal static void RefuseAgents()
    {
        if (AnsweringAgent.Value)
        {
            throw new InvalidOperationException("Secrets are not available to agents, or while answering an agent's tool call.");
        }
    }

    /// <summary>How stale a grant's last-used time may be before it is written again.</summary>
    private static readonly TimeSpan UseResolution = TimeSpan.FromMinutes(1);

    public SecretBroker(SecretCatalog catalog, ISecretVault vault, IClock clock, HttpMessageHandler? handler = null)
    {
        _catalog = catalog;
        _vault = vault;
        _clock = clock;

        // No redirects: a redirect to another host would carry a header the
        // extension named along with it, and the host check is the whole point
        // of brokering. The extension gets the 3xx and can follow it itself.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(100),
        };
    }

    /// <summary>Raised when a request arrives or is answered, and when the catalog changes, on whatever thread did it.</summary>
    public event Action? Changed;

    public ISecretVault Vault => _vault;

    public IReadOnlyList<SecretEntry> Secrets => _catalog.Secrets;

    /// <summary>Requests waiting for an answer.</summary>
    public IReadOnlyList<SecretRequest> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending.Values.Select(p => p.Request)];
            }
        }
    }

    /// <summary>
    /// Whether a secret of that name has been added. Answered from the catalog,
    /// not the OS store, which can be slow and on macOS may put up a prompt of
    /// its own; a value deleted behind the app's back reads as null when used.
    /// </summary>
    public bool Exists(string name) => _catalog.Find(name) is not null;

    /// <summary>
    /// The value, once you have let this build of the extension read it; null
    /// when you said no or there is no such secret and you declined to add it.
    /// Waits until you answer.
    /// </summary>
    public async Task<string?> ReadAsync(SecretCaller caller, string name, string? purpose, CancellationToken ct)
    {
        if (SecretNames.Problem(name) is { } problem)
        {
            throw new ArgumentException(problem, nameof(name));
        }

        RefuseAgents();
        return await Decide(new SecretRequest(caller, name, SecretAccess.Read, null, purpose), ct).ConfigureAwait(false)
            ? Take(caller, name)
            : null;
    }

    /// <summary>
    /// Sends <paramref name="request"/> with the secret on it, once you have let
    /// this build of the extension use it with the request's host. Null when you
    /// said no. The extension never sees the value.
    /// </summary>
    public async Task<HttpResponseMessage?> SendAsync(
        SecretCaller caller, string name, HttpRequestMessage request, SecretPlacement placement, string? purpose, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        RefuseAgents();
        if (SecretNames.Problem(name) is { } problem)
        {
            throw new ArgumentException(problem, nameof(name));
        }

        if (request.RequestUri is not { IsAbsoluteUri: true, Scheme: "https" } uri || uri.Host.Length == 0)
        {
            throw new ArgumentException("A brokered request needs an absolute https address.", nameof(request));
        }

        // The extension keeps its request object, so it could change the
        // address after you saw it in the prompt, or read the header back once
        // it was added. The broker sends a copy of its own, taken now, and the
        // secret only ever touches the copy. Host is left off: it would name
        // another server than the one approved.
        var own = new HttpRequestMessage(request.Method, uri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = request.Content,
        };
        foreach (var (key, values) in request.Headers)
        {
            if (!string.Equals(key, placement.Header, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                own.Headers.TryAddWithoutValidation(key, values);
            }
        }

        var host = uri.IsDefaultPort ? uri.IdnHost.ToLowerInvariant() : $"{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";
        if (!await Decide(new SecretRequest(caller, name, SecretAccess.Brokered, host, purpose), ct).ConfigureAwait(false)
            || Take(caller, name) is not { } value)
        {
            return null;
        }

        var text = placement.Base64 ? Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) : value;
        var header = placement.Scheme is { Length: > 0 } scheme ? $"{scheme} {text}" : text;
        if (!own.Headers.TryAddWithoutValidation(placement.Header, header))
        {
            throw new ArgumentException($"{placement.Header} cannot carry a secret.", nameof(placement));
        }

        var response = await _http.SendAsync(own, ct).ConfigureAwait(false);

        // The response points back at the request it answered, header and all.
        own.Headers.Remove(placement.Header);
        return response;
    }

    /// <summary>
    /// True once the caller may go ahead, false when you said no. Answers from
    /// the catalog when it can and otherwise waits on the one prompt for this
    /// request, then looks again: you may have answered a wider question in
    /// the meantime, or deleted the secret.
    /// </summary>
    private async Task<bool> Decide(SecretRequest request, CancellationToken ct)
    {
        while (true)
        {
            Task<bool> wait;
            lock (_gate)
            {
                switch (Standing(Key.Of(request)))
                {
                    case true:
                        return true;
                    case false:
                        return false;
                }

                var key = Key.Of(request);
                if (!_pending.TryGetValue(key, out var pending))
                {
                    pending = (request, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                    _pending[key] = pending;
                    _ = Task.Run(Raise, CancellationToken.None);
                }

                wait = pending.Prompt.Task;
            }

            // A cancelled caller stops waiting; the prompt stays for the others
            // and for you, and answering it still records the grant. False is
            // a no with nothing to record it against; true means look again.
            if (!await wait.WaitAsync(ct).ConfigureAwait(false))
            {
                return false;
            }
        }
    }

    /// <summary>True when allowed, false when refused, null when you have not said.</summary>
    private bool? Standing(Key request)
    {
        if (_catalog.Find(request.Name) is not { } entry)
        {
            return null;
        }

        var grant = entry.Grants.FirstOrDefault(g => g.ExtensionId == request.Caller.ExtensionId);
        if (grant is null || grant.Hash != request.Caller.Hash)
        {
            return null;
        }

        if (grant.Refused)
        {
            return false;
        }

        return grant.Access == SecretAccess.Read
               || (request.Access == SecretAccess.Brokered && grant.Hosts.Contains(request.Host))
            ? true
            : null;
    }

    /// <summary>
    /// Your yes. For a secret that does not exist yet, <paramref name="value"/>
    /// is what to store. Returns why it failed, or null; a failed answer leaves
    /// the request waiting so the prompt can say why.
    /// </summary>
    public string? Allow(SecretRequest request, string? value = null)
    {
        var now = _clock.Now;
        if (!Exists(request.Name))
        {
            if (string.IsNullOrEmpty(value))
            {
                return $"There is no secret called {request.Name} yet. Enter its value to add it.";
            }

            if (Store(request.Name, value) is { } failed)
            {
                return failed;
            }
        }

        _catalog.UpdateEntry(request.Name, entry =>
        {
            var old = entry!.Grants.FirstOrDefault(g => g.ExtensionId == request.Caller.ExtensionId);

            // A grant for this same build only widens: by host, or from brokered
            // to read. Anything else starts over.
            var same = old is { Refused: false } && old.Hash == request.Caller.Hash;
            var hosts = same && old!.Access == SecretAccess.Brokered ? old.Hosts : [];
            var access = same && old!.Access == SecretAccess.Read ? SecretAccess.Read : request.Access;
            var grant = new SecretGrant
            {
                ExtensionId = request.Caller.ExtensionId,
                Hash = request.Caller.Hash,
                Access = access,
                Hosts = access == SecretAccess.Read ? [] : request.Host is { } host && !hosts.Contains(host) ? [.. hosts, host] : hosts,
                DecidedAt = now,
                LastUsed = old?.Hash == request.Caller.Hash ? old.LastUsed : null,
            };
            return entry with { Grants = [.. entry.Grants.Where(g => g != old), grant] };
        });

        Answer(request);
        return null;
    }

    /// <summary>
    /// Your no. Kept for this build, so the extension is answered null from now
    /// on without asking. A secret that does not exist is not added for it, and
    /// the no is not kept either: there is nothing to keep it against.
    /// </summary>
    public void Refuse(SecretRequest request)
    {
        if (_catalog.Find(request.Name) is null)
        {
            Dismiss(request);
            return;
        }

        {
            _catalog.UpdateEntry(request.Name, entry => entry! with
            {
                Grants =
                [
                    .. entry.Grants.Where(g => g.ExtensionId != request.Caller.ExtensionId),
                    new SecretGrant
                    {
                        ExtensionId = request.Caller.ExtensionId,
                        Hash = request.Caller.Hash,
                        Refused = true,
                        DecidedAt = _clock.Now,
                    },
                ],
            });
            Answer(request);
        }
    }

    /// <summary>
    /// Not now: the waiting callers hear null and nothing is kept, so the
    /// extension asks again next time. What Escape does, since a reflex
    /// should not silence an extension until you find Forget in Settings.
    /// </summary>
    public void Dismiss(SecretRequest request)
    {
        TaskCompletionSource<bool>? prompt;
        lock (_gate)
        {
            if (_pending.Remove(Key.Of(request), out var pending))
            {
                prompt = pending.Prompt;
            }
            else
            {
                prompt = null;
            }
        }

        prompt?.TrySetResult(false);
        Raise();
    }

    private void Answer(SecretRequest request)
    {
        // Every waiting request this answer may settle is woken to look again;
        // one it did not settle puts its prompt straight back.
        List<TaskCompletionSource<bool>> woken;
        lock (_gate)
        {
            var settled = _pending.Keys
                .Where(k => k.Name == request.Name && k.Caller.ExtensionId == request.Caller.ExtensionId && Standing(k) is not null)
                .Append(Key.Of(request))
                .Distinct()
                .ToList();
            woken = [];
            foreach (var key in settled)
            {
                if (_pending.Remove(key, out var pending))
                {
                    woken.Add(pending.Prompt);
                }
            }
        }

        foreach (var prompt in woken)
        {
            prompt.TrySetResult(true);
        }

        Raise();
    }

    /// <summary>Reads the value for a caller that has been let through, and notes the use.</summary>
    private string? Take(SecretCaller caller, string name)
    {
        string? value;
        try
        {
            value = _vault.Read(name);
        }
        catch (SecretVaultException)
        {
            return null;
        }

        if (value is null)
        {
            return null;
        }

        var now = _clock.Now;
        if (_catalog.Find(name)?.Grants.FirstOrDefault(g => g.ExtensionId == caller.ExtensionId) is { } grant
            && (grant.LastUsed is not { } last || now - last >= UseResolution))
        {
            _catalog.UpdateEntry(name, entry => entry is null ? null : entry with
            {
                Grants = [.. entry.Grants.Select(g => g.ExtensionId == caller.ExtensionId ? g with { LastUsed = now } : g)],
            });
            Raise();
        }

        return value;
    }

    /// <summary>Adds a secret, or replaces its value. Returns why it failed, or null.</summary>
    public string? Store(string name, string value)
    {
        if (SecretNames.Problem(name) is { } problem)
        {
            return problem;
        }

        if (string.IsNullOrEmpty(value))
        {
            return "Enter the value.";
        }

        if (!_vault.Available)
        {
            return $"There is no store for secrets on this machine ({_vault.Description}).";
        }

        try
        {
            _vault.Write(name, value);
        }
        catch (SecretVaultException e)
        {
            return e.Message;
        }

        var now = _clock.Now;
        _catalog.UpdateEntry(name, entry => entry ?? new SecretEntry { Name = name, Added = now });

        // A request waiting on a secret that did not exist can now be asked properly.
        Raise();
        return null;
    }

    /// <summary>Deletes a secret: its value from the OS store, and every grant. Returns why it failed, or null.</summary>
    public string? Delete(string name)
    {
        try
        {
            _vault.Delete(name);
        }
        catch (SecretVaultException e)
        {
            return e.Message;
        }

        _catalog.UpdateEntry(name, _ => null);
        Raise();
        return null;
    }

    /// <summary>
    /// Forgets what you decided about one extension and a secret, yes or no.
    /// It will be asked again the next time it wants it.
    /// </summary>
    public void Revoke(string name, string extensionId)
    {
        _catalog.UpdateEntry(name, entry => entry is null ? null : entry with
        {
            Grants = [.. entry.Grants.Where(g => g.ExtensionId != extensionId)],
        });
        Raise();
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose() => _http.Dispose();
}
