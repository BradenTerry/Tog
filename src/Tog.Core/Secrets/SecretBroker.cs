using System.Text;
using Tog.Core.Platform;

namespace Tog.Core.Secrets;

/// <summary>Which build of which extension is asking, and what its manifest declared. The app vouches for all of it.</summary>
/// <param name="Hash">The SHA-256 of the entry assembly that is running, as loaded.</param>
/// <param name="Needs">The secrets its <c>extension.json</c> declares; the only names it can ask for.</param>
public sealed record SecretCaller(string ExtensionId, string ExtensionName, string Hash, IReadOnlyList<SecretNeed> Needs)
{
    public SecretNeed? Need(string name) => Needs.FirstOrDefault(n => n.Name == name);
}

/// <summary>An extension waiting for you to choose which secret answers one of its needs.</summary>
public sealed record SecretRequest(SecretCaller Caller, SecretNeed Need)
{
    public string Name => Need.Name;
}

/// <summary>How the app puts a secret on a brokered request.</summary>
/// <param name="Header">The header it goes in.</param>
/// <param name="Scheme">Written before it, as in <c>Bearer</c>, or null for the value alone.</param>
/// <param name="Base64">Encode the value first, as Basic authentication wants for <c>user:token</c>.</param>
public sealed record SecretPlacement(string Header, string? Scheme, bool Base64);

/// <summary>
/// Hands secrets to extensions through the bindings you made, asks you to make
/// one when a declared need has none, and sends requests with a secret on an
/// extension's behalf.
/// </summary>
/// <remarks>
/// <para>
/// An extension can only ask for a secret its manifest declares, by its own
/// name for it, and a brokered request can only go to a host declared for it.
/// Anything else throws: no prompt, since a prompt for a name nobody declared
/// is a question you should never have to answer. A declared need is answered
/// by a binding, which you make in Settings or in the prompt a first call puts
/// up, by choosing which of your secrets it gets. Binding is the approval, and
/// it covers everything the need declared, so there is one decision per need
/// and build, not one per host.
/// </para>
/// <para>
/// A call waits until it is answered, and every call for the same need of the
/// same build shares one prompt, so a worker's retries and a view's renders do
/// not pile up questions. A no is kept too, so an extension that asks on a
/// timer is answered from the catalog after the first time.
/// </para>
/// <para>
/// None of this is isolation. An extension runs in the app's process with its
/// types, and one that means harm can reach this object, or the OS store, by
/// reflection. Binding keeps a well-behaved extension to the secrets you meant
/// it to have; the boundary is still consenting to its code.
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

    /// <summary>What one prompt is about: one need of one build.</summary>
    private sealed record Key(string ExtensionId, string Hash, string Need)
    {
        public static Key Of(SecretRequest r) => new(r.Caller.ExtensionId, r.Caller.Hash, r.Need.Name);
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

    /// <summary>How stale a binding's last-used time may be before it is written again.</summary>
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

    public IReadOnlyList<SecretBinding> Bindings => _catalog.Bindings;

    /// <summary>The binding for an extension's need, whatever build it was made for.</summary>
    public SecretBinding? Binding(string extensionId, string need) => _catalog.Binding(extensionId, need);

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
    /// The value of the secret bound to the caller's need, once you have bound
    /// it for this build; null when you said no. The need must declare
    /// <c>read</c>. Waits until you answer.
    /// </summary>
    public async Task<string?> ReadAsync(SecretCaller caller, string name, CancellationToken ct)
    {
        RefuseAgents();
        var need = Declared(caller, name);
        if (!need.Read)
        {
            throw new InvalidOperationException(
                $"{caller.ExtensionName} declares the secret {name} without \"read\": true, so it can only be sent with SendAsync.");
        }

        return await Decide(caller, need, SecretAccess.Read, null, ct).ConfigureAwait(false)
            ? Take(caller, need.Name)
            : null;
    }

    /// <summary>
    /// Sends <paramref name="request"/> with the secret bound to the caller's
    /// need, once you have bound it for this build. Null when you said no. The
    /// host must be one the need declares. The extension never sees the value.
    /// </summary>
    public async Task<HttpResponseMessage?> SendAsync(
        SecretCaller caller, string name, HttpRequestMessage request, SecretPlacement placement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        RefuseAgents();
        var need = Declared(caller, name);

        if (request.RequestUri is not { IsAbsoluteUri: true, Scheme: "https" } uri || uri.Host.Length == 0)
        {
            throw new ArgumentException("A brokered request needs an absolute https address.", nameof(request));
        }

        var host = SecretNames.HostOf(uri);
        if (!need.Read && !need.Hosts.Contains(host))
        {
            throw new InvalidOperationException(
                $"{host} is not a host {caller.ExtensionName} declares for the secret {name}. Declared: {string.Join(", ", need.Hosts)}.");
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

        if (!await Decide(caller, need, SecretAccess.Brokered, host, ct).ConfigureAwait(false)
            || Take(caller, need.Name) is not { } value)
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

    /// <summary>The caller's declared need of that name, or the reason it cannot ask.</summary>
    private static SecretNeed Declared(SecretCaller caller, string name)
    {
        if (SecretNames.Problem(name) is { } problem)
        {
            throw new ArgumentException(problem, nameof(name));
        }

        return caller.Need(name) ?? throw new InvalidOperationException(
            $"{caller.ExtensionName} asked for the secret {name}, which its extension.json does not declare. "
            + "Add it to \"secrets\" there, with the hosts it is sent to.");
    }

    /// <summary>
    /// True once the caller may go ahead, false when you said no. Answers from
    /// the catalog when it can and otherwise waits on the one prompt for this
    /// need, then looks again: the binding you made may not cover this call, or
    /// you may have deleted the secret meanwhile.
    /// </summary>
    private async Task<bool> Decide(SecretCaller caller, SecretNeed need, SecretAccess access, string? host, CancellationToken ct)
    {
        var request = new SecretRequest(caller, need);
        while (true)
        {
            Task<bool> wait;
            lock (_gate)
            {
                switch (Standing(caller, need.Name, access, host))
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
            // and for you, and answering it still records the binding. False
            // is a no with nothing to record it against; true means look again.
            if (!await wait.WaitAsync(ct).ConfigureAwait(false))
            {
                return false;
            }
        }
    }

    /// <summary>True when bound and covering this call, false when refused, null when you have not said.</summary>
    private bool? Standing(SecretCaller caller, string need, SecretAccess access, string? host)
    {
        if (_catalog.Binding(caller.ExtensionId, need) is not { } binding || binding.Hash != caller.Hash)
        {
            return null;
        }

        if (binding.Refused)
        {
            return false;
        }

        if (!Exists(binding.Secret!))
        {
            return null;
        }

        return binding.Access == SecretAccess.Read
               || (access == SecretAccess.Brokered && binding.Hosts.Contains(host))
            ? true
            : null;
    }

    /// <summary>
    /// Your yes from the prompt: <paramref name="secret"/> answers the request's
    /// need. When no secret of that name exists yet, <paramref name="value"/>
    /// is stored as it. Returns why it failed, or null; a failed answer leaves
    /// the request waiting so the prompt can say why.
    /// </summary>
    public string? Allow(SecretRequest request, string secret, string? value = null) =>
        Bind(request.Caller.ExtensionId, request.Caller.Hash, request.Need, secret, value);

    /// <summary>
    /// Binds an extension's need to one of your secrets for one build of it,
    /// covering what the need declares now: its hosts, or reading the value.
    /// Replaces any binding the need had. Returns why it failed, or null.
    /// </summary>
    public string? Bind(string extensionId, string hash, SecretNeed need, string secret, string? value = null)
    {
        if (SecretNames.Problem(secret) is { } problem)
        {
            return problem;
        }

        if (!Exists(secret))
        {
            if (string.IsNullOrEmpty(value))
            {
                return $"There is no secret called {secret} yet. Enter its value to add it.";
            }

            if (Store(secret, value) is { } failed)
            {
                return failed;
            }
        }

        var now = _clock.Now;
        _catalog.Update(state =>
        {
            var old = state.Bindings.FirstOrDefault(b => b.ExtensionId == extensionId && b.Need == need.Name);
            var binding = new SecretBinding
            {
                ExtensionId = extensionId,
                Need = need.Name,
                Secret = secret,
                Hash = hash,
                Access = need.Read ? SecretAccess.Read : SecretAccess.Brokered,
                Hosts = need.Read ? [] : need.Hosts,
                DecidedAt = now,
                LastUsed = old is { } o && o.Secret == secret && o.Hash == hash ? o.LastUsed : null,
            };
            return state with { Bindings = [.. state.Bindings.Where(b => b != old), binding] };
        });

        Wake(extensionId, need.Name);
        return null;
    }

    /// <summary>
    /// Your no. Kept for this build, so the extension is answered null from now
    /// on without asking, until Settings forgets it.
    /// </summary>
    public void Refuse(SecretRequest request)
    {
        var (id, need) = (request.Caller.ExtensionId, request.Need.Name);
        var now = _clock.Now;
        _catalog.Update(state => state with
        {
            Bindings =
            [
                .. state.Bindings.Where(b => b.ExtensionId != id || b.Need != need),
                new SecretBinding { ExtensionId = id, Need = need, Secret = null, Hash = request.Caller.Hash, DecidedAt = now },
            ],
        });
        Wake(id, need);
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
            prompt = _pending.Remove(Key.Of(request), out var pending) ? pending.Prompt : null;
        }

        prompt?.TrySetResult(false);
        Raise();
    }

    /// <summary>
    /// Forgets the binding for a need, yes or no. The next call for it asks
    /// again; a call already waiting keeps waiting.
    /// </summary>
    public void Unbind(string extensionId, string need)
    {
        _catalog.Update(state => state with
        {
            Bindings = [.. state.Bindings.Where(b => b.ExtensionId != extensionId || b.Need != need)],
        });
        Raise();
    }

    /// <summary>
    /// Wakes every call waiting on this need, of any build, to look again. One
    /// the new binding does not cover puts its prompt straight back.
    /// </summary>
    private void Wake(string extensionId, string need)
    {
        List<TaskCompletionSource<bool>> woken = [];
        lock (_gate)
        {
            foreach (var key in _pending.Keys.Where(k => k.ExtensionId == extensionId && k.Need == need).ToList())
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

    /// <summary>Reads the value bound to a caller's need once it has been let through, and notes the use.</summary>
    private string? Take(SecretCaller caller, string need)
    {
        if (_catalog.Binding(caller.ExtensionId, need) is not { Secret: { } secret } binding)
        {
            return null;
        }

        string? value;
        try
        {
            value = _vault.Read(secret);
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
        if (binding.LastUsed is not { } last || now - last >= UseResolution)
        {
            _catalog.Update(state => state with
            {
                Bindings = [.. state.Bindings.Select(b => b.ExtensionId == caller.ExtensionId && b.Need == need ? b with { LastUsed = now } : b)],
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
        _catalog.Update(state => state.Secrets.Any(e => e.Name == name)
            ? state
            : state with { Secrets = [.. state.Secrets, new SecretEntry { Name = name, Added = now }] });

        // A request waiting on a secret that did not exist can now be asked properly.
        Raise();
        return null;
    }

    /// <summary>
    /// Deletes a secret: its value from the OS store, and every binding to it,
    /// so the needs it answered are asked about again. Returns why it failed, or null.
    /// </summary>
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

        _catalog.Update(state => new SecretState(
            [.. state.Secrets.Where(s => s.Name != name)],
            [.. state.Bindings.Where(b => b.Secret != name)]));
        Raise();
        return null;
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose() => _http.Dispose();
}
