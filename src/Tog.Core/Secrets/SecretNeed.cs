namespace Tog.Core.Secrets;

/// <summary>
/// A secret an extension says in its <c>extension.json</c> that it needs.
/// Read before its code runs, so Settings and the consent card can show it,
/// and the only names the extension can ask for: a call for anything else is
/// refused without a prompt. Since API 1.12.
/// </summary>
/// <remarks>
/// Declaring is asking, not getting. Nothing is handed over until you bind the
/// need to one of your stored secrets, and that binding is kept for one build.
/// The manifest is not part of the code hash, so a manifest edited after you
/// bound it cannot widen what the binding covers: the hosts and read access
/// you approved are kept on the binding, and anything past them asks again.
/// </remarks>
public sealed record SecretNeed
{
    /// <summary>The extension's own name for it, such as <c>github</c>. Yours may differ; the binding maps them.</summary>
    public required string Name { get; init; }

    /// <summary>A sentence for you: what it is for.</summary>
    public string? Purpose { get; init; }

    /// <summary>
    /// Where the app may send it on the extension's behalf, such as
    /// <c>api.github.com</c>. A brokered request to any other host is refused.
    /// </summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    /// <summary>
    /// The extension wants the value itself, for a client library that will not
    /// take a request from the app. It can then send it anywhere.
    /// </summary>
    public bool Read { get; init; }

    public const int MaxPurpose = 300;

    /// <summary>
    /// A manifest's needs with their hosts written the one way, or why they
    /// cannot be used. An extension whose needs fail here does not load.
    /// </summary>
    public static (IReadOnlyList<SecretNeed> Needs, string? Error) Check(IReadOnlyList<SecretNeed>? needs)
    {
        if (needs is null or { Count: 0 })
        {
            return ([], null);
        }

        var result = new List<SecretNeed>();
        foreach (var need in needs)
        {
            if (need is null)
            {
                return ([], "secrets has an empty entry.");
            }

            if (SecretNames.Problem(need.Name) is { } problem)
            {
                return ([], $"The secret \"{need.Name}\": {problem}");
            }

            if (result.Any(n => n.Name == need.Name))
            {
                return ([], $"The secret \"{need.Name}\" is declared twice.");
            }

            if (need.Purpose is { Length: > MaxPurpose })
            {
                return ([], $"The purpose of \"{need.Name}\" is over {MaxPurpose} characters.");
            }

            var hosts = new List<string>();
            foreach (var text in need.Hosts ?? [])
            {
                if (SecretNames.Host(text) is not { } host)
                {
                    return ([], $"\"{text}\" in the hosts of \"{need.Name}\" is not a host, such as api.github.com or intranet.corp:8443.");
                }

                if (!hosts.Contains(host))
                {
                    hosts.Add(host);
                }
            }

            if (hosts.Count == 0 && !need.Read)
            {
                return ([], $"The secret \"{need.Name}\" names no hosts and does not ask to read it, so it could not be used.");
            }

            result.Add(need with { Hosts = hosts, Purpose = need.Purpose?.Trim() is { Length: > 0 } p ? p : null });
        }

        return (result, null);
    }
}
