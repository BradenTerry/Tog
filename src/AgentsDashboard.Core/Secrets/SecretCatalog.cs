using System.Text.Json;
using System.Text.Json.Serialization;
using AgentsDashboard.Core.Repos;

namespace AgentsDashboard.Core.Secrets;

/// <summary>What an extension was allowed to do with a secret.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SecretAccess>))]
public enum SecretAccess
{
    /// <summary>The app sends requests with it to the listed hosts; the extension never sees it.</summary>
    Brokered,

    /// <summary>The extension is handed the value. Covers brokered requests to any host too.</summary>
    Read,
}

/// <summary>
/// One extension's standing with one secret. Bound to the code it was decided
/// for: a grant whose <see cref="Hash"/> is not the running build's counts for
/// nothing, and the extension is asked about again.
/// </summary>
public sealed record SecretGrant
{
    public required string ExtensionId { get; init; }

    /// <summary>The SHA-256 of the extension's entry assembly when you answered.</summary>
    public required string Hash { get; init; }

    public SecretAccess Access { get; init; }

    /// <summary>For <see cref="SecretAccess.Brokered"/>: the hosts requests may go to.</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    /// <summary>You said no. Kept, so a worker asking every minute does not ask you every minute.</summary>
    public bool Refused { get; init; }

    public DateTimeOffset DecidedAt { get; init; }

    /// <summary>When the extension last used it, to the minute.</summary>
    public DateTimeOffset? LastUsed { get; init; }
}

/// <summary>A secret as the app knows it: its name and who may use it. Never its value.</summary>
public sealed record SecretEntry
{
    public required string Name { get; init; }

    public DateTimeOffset Added { get; init; }

    public IReadOnlyList<SecretGrant> Grants { get; init; } = [];
}

/// <summary>
/// The secrets' names and grants, in <c>secrets.json</c>. The values are in the
/// OS store (<see cref="ISecretVault"/>).
/// </summary>
/// <remarks>
/// A file of its own rather than a part of <c>settings.json</c>: the Settings
/// page holds a copy of those settings while it is open and saves the whole of
/// it on every change, which would undo a grant made from a prompt meanwhile.
/// Held in memory and written whole and atomically on every change.
/// </remarks>
public sealed class SecretCatalog
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _file;
    private readonly Lock _gate = new();
    private IReadOnlyList<SecretEntry> _secrets;

    public SecretCatalog(AppPaths paths)
    {
        _file = paths.SecretsFile;
        _secrets = Read(_file);
    }

    public IReadOnlyList<SecretEntry> Secrets
    {
        get
        {
            lock (_gate)
            {
                return _secrets;
            }
        }
    }

    public SecretEntry? Find(string name) => Secrets.FirstOrDefault(s => s.Name == name);

    /// <summary>Changes the list under the lock and saves it. Returns what it became.</summary>
    public IReadOnlyList<SecretEntry> Update(Func<IReadOnlyList<SecretEntry>, IReadOnlyList<SecretEntry>> change)
    {
        lock (_gate)
        {
            var next = change(_secrets);
            if (!ReferenceEquals(next, _secrets))
            {
                _secrets = next;
                Write(_file, next);
            }

            return next;
        }
    }

    /// <summary>Replaces one secret's entry, adding it if it is new, or removes it when <paramref name="change"/> returns null.</summary>
    public void UpdateEntry(string name, Func<SecretEntry?, SecretEntry?> change) =>
        Update(all =>
        {
            var current = all.FirstOrDefault(s => s.Name == name);
            var next = change(current);
            if (ReferenceEquals(next, current))
            {
                return all;
            }

            var rest = all.Where(s => s.Name != name);
            return next is null ? [.. rest] : [.. rest, next];
        });

    private static IReadOnlyList<SecretEntry> Read(string file)
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<List<SecretEntry>>(File.ReadAllText(file), Options) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Write(string file, IReadOnlyList<SecretEntry> secrets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(secrets.OrderBy(s => s.Name, StringComparer.Ordinal), Options));
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>What a secret may be called.</summary>
public static class SecretNames
{
    public const int MaxLength = 64;

    /// <summary>
    /// Why <paramref name="name"/> cannot name a secret, or null when it can.
    /// Lower case letters, digits, <c>.</c>, <c>-</c> and <c>_</c>, so an
    /// extension's request for <c>github</c> and the one you typed as
    /// <c>GitHub</c> are never two different secrets.
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
}
