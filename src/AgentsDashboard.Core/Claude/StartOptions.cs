namespace AgentsDashboard.Core.Claude;

/// <summary>How to start a background agent.</summary>
/// <remarks>
/// Null or blank means "say nothing", which leaves the CLI's own default in
/// place. That matters because the dashboard's idea of a default would otherwise
/// be pinned to whatever the CLI happened to offer on the day this was written.
/// </remarks>
/// <param name="Prompt">What to give the agent to do. Without one it starts idle.</param>
/// <param name="Model">A model alias or full name, see <see cref="StartChoices.Models"/>.</param>
/// <param name="Effort">See <see cref="StartChoices.Efforts"/>.</param>
/// <param name="PermissionMode">See <see cref="StartChoices.PermissionModes"/>.</param>
/// <param name="NewWorktree">Have the CLI create a git worktree for the session.</param>
/// <param name="WorktreeName">A name for that worktree. Without one the CLI picks.</param>
public sealed record StartOptions(
    string? Prompt = null,
    string? Model = null,
    string? Effort = null,
    string? PermissionMode = null,
    bool NewWorktree = false,
    string? WorktreeName = null);
