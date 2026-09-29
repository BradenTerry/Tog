using Togue.Core.Secrets;

namespace Togue.App.Components.Shared;

/// <summary>How a declared secret need, and a binding, read in the prompt, the Extensions card and Secrets.</summary>
public static class SecretText
{
    /// <summary>What a binding would let the extension do: short, after the need's name.</summary>
    public static string Scope(SecretNeed need) =>
        need.Read ? "reads the value" : $"sent to {string.Join(", ", need.Hosts)}";

    /// <summary>The same, for a binding that was made.</summary>
    public static string Scope(SecretBinding binding) =>
        binding.Refused ? "refused"
        : binding.Access == SecretAccess.Read ? "can read it"
        : $"sent to {string.Join(", ", binding.Hosts)}";

    /// <summary>Every need of a manifest on one line, for the extension's row and the consent card.</summary>
    public static string Needs(IReadOnlyList<SecretNeed> needs) =>
        string.Join("; ", needs.Select(n => $"{n.Name} ({Scope(n)})"));
}
