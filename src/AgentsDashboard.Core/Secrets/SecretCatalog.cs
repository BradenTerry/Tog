using System.Text.Json;
using System.Text.Json.Serialization;
using AgentsDashboard.Core.Repos;

namespace AgentsDashboard.Core.Secrets;

/// <summary>What a binding lets an extension do with the secret behind it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SecretAccess>))]
public enum SecretAccess
{
    /// <summary>The app sends requests with it to the listed hosts; the extension never sees it.</summary>
    Brokered,

    /// <summary>The extension is handed the value. Covers brokered requests to any host too.</summary>
    Read,
}

/// <summary>A secret as the app knows it: its name. Never its value, which is in the OS store.</summary>
public sealed record SecretEntry
{
    public required string Name { get; init; }

    public DateTimeOffset Added { get; init; }
}

/// <summary>
/// Which stored secret answers one need an extension declared, and what it may
/// do with it: your approval, made by choosing the secret. Bound to the code it
/// was decided for: a binding whose <see cref="Hash"/> is not the running
/// build's counts for nothing, and the extension is asked about again.
/// </summary>
public sealed record SecretBinding
{
    public required string ExtensionId { get; init; }

    /// <summary>The name the extension uses, from its <c>extension.json</c>.</summary>
    public required string Need { get; init; }

    /// <summary>The stored secret it maps to, or null when you said no.</summary>
    public string? Secret { get; init; }

    /// <summary>The SHA-256 of the extension's entry assembly when you answered.</summary>
    public required string Hash { get; init; }

    public SecretAccess Access { get; init; }

    /// <summary>For <see cref="SecretAccess.Brokered"/>: the hosts the manifest declared when you answered.</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    public DateTimeOffset DecidedAt { get; init; }

    /// <summary>When the extension last used it, to the minute.</summary>
    public DateTimeOffset? LastUsed { get; init; }

    /// <summary>You said no. Kept, so a worker asking every minute does not ask you every minute.</summary>
    [JsonIgnore]
    public bool Refused => Secret is null;
}

/// <summary>Everything in <c>secrets.json</c>.</summary>
public sealed record SecretState(IReadOnlyList<SecretEntry> Secrets, IReadOnlyList<SecretBinding> Bindings)
{
    public static SecretState Empty { get; } = new([], []);
}

/// <summary>
/// The secrets' names and bindings, in <c>secrets.json</c>. The values are in
/// the OS store (<see cref="ISecretVault"/>).
/// </summary>
/// <remarks>
/// A file of its own rather than a part of <c>settings.json</c>: the Settings
/// page holds a copy of those settings while it is open and saves the whole of
/// it on every change, which would undo a binding made from a prompt meanwhile.
/// Held in memory and written whole and atomically on every change.
/// <para>
/// Before API 1.12 the file was a bare list of secrets, each with grants to
/// extensions that had asked for it by the secret's own name. Such a file is
/// read for its names only: those grants were for names no manifest declared,
/// which is exactly what is no longer allowed, so each extension is asked to
/// be bound once.
/// </para>
/// </remarks>
public sealed class SecretCatalog
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _file;
    private readonly Lock _gate = new();
    private SecretState _state;

    public SecretCatalog(AppPaths paths)
    {
        _file = paths.SecretsFile;
        _state = Read(_file);
    }

    public SecretState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public IReadOnlyList<SecretEntry> Secrets => State.Secrets;

    public IReadOnlyList<SecretBinding> Bindings => State.Bindings;

    public SecretEntry? Find(string name) => Secrets.FirstOrDefault(s => s.Name == name);

    /// <summary>The one binding for an extension's need, whatever build it was made for.</summary>
    public SecretBinding? Binding(string extensionId, string need) =>
        Bindings.FirstOrDefault(b => b.ExtensionId == extensionId && b.Need == need);

    /// <summary>Changes the state under the lock and saves it. Returns what it became.</summary>
    public SecretState Update(Func<SecretState, SecretState> change)
    {
        lock (_gate)
        {
            var next = change(_state);
            if (!ReferenceEquals(next, _state))
            {
                _state = next;
                Write(_file, next);
            }

            return next;
        }
    }

    private static SecretState Read(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return SecretState.Empty;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return new SecretState(doc.RootElement.Deserialize<List<SecretEntry>>(Options) ?? [], []);
            }

            var state = doc.RootElement.Deserialize<SecretState>(Options);
            return new SecretState(state?.Secrets ?? [], state?.Bindings ?? []);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return SecretState.Empty;
        }
    }

    private static void Write(string file, SecretState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        var ordered = new SecretState(
            [.. state.Secrets.OrderBy(s => s.Name, StringComparer.Ordinal)],
            [.. state.Bindings.OrderBy(b => b.ExtensionId, StringComparer.Ordinal).ThenBy(b => b.Need, StringComparer.Ordinal)]);
        File.WriteAllText(temp, JsonSerializer.Serialize(ordered, Options));
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>What a secret, or an extension's name for one, may be called, and how hosts are written.</summary>
public static class SecretNames
{
    public const int MaxLength = 64;

    /// <summary>
    /// Why <paramref name="name"/> cannot name a secret, or null when it can.
    /// Lower case letters, digits, <c>.</c>, <c>-</c> and <c>_</c>, so an
    /// extension's <c>github</c> and the one you typed as <c>GitHub</c> are
    /// never two different secrets.
    /// </summary>
    public static string? Problem(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Give it a name, such as github or linear.";
        }

        if (name.Length > MaxLength)
        {
            return $"A name is at most {MaxLength} characters.";
        }

        if (!char.IsAsciiLetterLower(name[0]) && !char.IsAsciiDigit(name[0]))
        {
            return "A name starts with a lower case letter or a digit.";
        }

        return name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-' or '_')
            ? null
            : "A name is lower case letters, digits, dots, dashes and underscores.";
    }

    /// <summary>
    /// A host as a manifest declares it and a brokered request is checked
    /// against it: <c>api.github.com</c>, or <c>intranet.corp:8443</c> when the
    /// port is not 443. Null when <paramref name="text"/> is not one host,
    /// such as a URL with a path, a wildcard or a user name.
    /// </summary>
    public static string? Host(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.IndexOfAny(['/', '\\', '@', '*', '?', '#', ' ']) >= 0)
        {
            return null;
        }

        return Uri.TryCreate("https://" + text, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? HostOf(uri)
            : null;
    }

    /// <summary>The host of an https address, with its port when that is not 443.</summary>
    public static string HostOf(Uri uri) =>
        uri.IsDefaultPort ? uri.IdnHost.ToLowerInvariant() : $"{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";
}
