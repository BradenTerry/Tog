namespace Togue.Core.Agents;

/// <summary>A value a start setting can take, and what to call it.</summary>
public sealed record StartChoice(string Value, string Name);

/// <summary>
/// What the start form and Settings offer before any agent has run.
/// </summary>
/// <remarks>
/// The real lists come from the agent: every session it starts reports the
/// models, efforts and modes it has, and <see cref="AgentHost.KnownOptions"/>
/// keeps the last of those. These are only what is shown until then. A value is
/// matched against what the agent offers when a session starts, so an alias such
/// as "opus" still finds the agent's own name for it, and one the agent does not
/// have is left out rather than failing the start.
/// </remarks>
public static class StartChoices
{
    public static readonly IReadOnlyList<StartChoice> Models =
    [
        new("fable", "Fable"),
        new("opus", "Opus"),
        new("sonnet", "Sonnet"),
        new("haiku", "Haiku"),
    ];

    public static readonly IReadOnlyList<StartChoice> Efforts =
    [
        new("low", "Low"),
        new("medium", "Medium"),
        new("high", "High"),
        new("xhigh", "Xhigh"),
        new("max", "Max"),
    ];

    public static readonly IReadOnlyList<StartChoice> Modes =
    [
        new("default", "Manual"),
        new("acceptEdits", "Accept edits"),
        new("plan", "Plan"),
        new("auto", "Auto"),
        new("bypassPermissions", "Bypass permissions"),
    ];

    /// <summary>
    /// The choices for one setting: the agent's own when it has reported them,
    /// these otherwise.
    /// </summary>
    public static IReadOnlyList<StartChoice> For(string id, IReadOnlyList<AcpConfigOption> known)
    {
        if (known.FirstOrDefault(o => o.Id == id) is { Choices.Count: > 0 } option)
        {
            return option.Choices
                .Where(c => c.Value != "default" || id == "mode")
                .Select(c => new StartChoice(c.Value, c.Name))
                .ToList();
        }

        return id switch
        {
            "model" => Models,
            "effort" => Efforts,
            "mode" => Modes,
            _ => [],
        };
    }
}
