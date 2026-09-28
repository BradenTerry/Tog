namespace AgentsDashboard.Extensions.CSharpCode;

/// <summary>The settings this extension adds under it in Settings, Extensions.</summary>
public static class CSharpSettings
{
    /// <summary>When a worktree's solution is loaded.</summary>
    public const string Load = "load";

    /// <summary>Only when Load is pressed on a C# file's bar. The default.</summary>
    public const string LoadOnClick = "click";

    /// <summary>As soon as a C# file in the worktree is opened.</summary>
    public const string LoadOnOpen = "open";

    public static readonly ExtensionSetting LoadSetting = ExtensionSetting.Choice(
        Load,
        "Load a worktree's solution",
        LoadOnClick,
        [
            new SettingChoice(LoadOnClick, "When I press Load", "Nothing loads until you ask"),
            new SettingChoice(LoadOnOpen, "When a C# file opens", "Once per worktree, until you unload it"),
        ],
        "Go to definition, references and colouring need the solution loaded. A load takes seconds and "
            + "a few hundred megabytes per worktree.");
}
