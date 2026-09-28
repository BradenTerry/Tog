namespace AgentsDashboard.Extensions;

/// <summary>
/// A setting the user changes under the extension in Settings, Extensions.
/// Declared with <see cref="IExtensionBuilder.AddSetting"/> and read with
/// <see cref="IExtensionSettings"/>. Since API 1.8.
/// </summary>
/// <remarks>
/// Values are strings, kept in the app's settings file under the extension's
/// id, and survive reloads, rebuilds and turning the extension off. A value the
/// extension no longer offers reads as <see cref="Default"/>, so renaming a
/// choice cannot leave the extension with something it does not understand.
/// </remarks>
/// <param name="Id">Unique within the extension.</param>
/// <param name="Title">The setting's label.</param>
/// <param name="Default">The value until the user picks another.</param>
/// <param name="Description">A sentence under the label, or null.</param>
/// <param name="Choices">The values offered, drawn as a list to pick from; null for an on/off switch.</param>
public sealed record ExtensionSetting(
    string Id,
    string Title,
    string Default,
    string? Description = null,
    IReadOnlyList<SettingChoice>? Choices = null)
{
    /// <summary>The value an on/off switch reads as when on.</summary>
    public const string On = "true";

    /// <summary>The value an on/off switch reads as when off.</summary>
    public const string Off = "false";

    /// <summary>An on/off switch.</summary>
    public static ExtensionSetting Toggle(string id, string title, bool on, string? description = null) =>
        new(id, title, on ? On : Off, description);

    /// <summary>One value out of a few, each with a label.</summary>
    public static ExtensionSetting Choice(
        string id, string title, string defaultValue, IReadOnlyList<SettingChoice> choices, string? description = null) =>
        new(id, title, defaultValue, description, choices);

    /// <summary>Whether a value is one this setting accepts.</summary>
    public bool Accepts(string value) => Choices is { } choices
        ? choices.Any(c => c.Value == value)
        : value is On or Off;
}

/// <summary>One value of a <see cref="ExtensionSetting.Choice"/> setting.</summary>
/// <param name="Value">What <see cref="IExtensionSettings.Get"/> returns.</param>
/// <param name="Label">What the list shows.</param>
/// <param name="Description">A hint beside the label, or null.</param>
public sealed record SettingChoice(string Value, string Label, string? Description = null);

/// <summary>
/// The current values of the extension's own settings. Since API 1.8. A
/// singleton in the extension's container, so a service can hold it.
/// </summary>
/// <remarks>Answers from memory, so it is safe to call on every render.</remarks>
public interface IExtensionSettings
{
    /// <summary>The value, or the default when the user has not changed it.</summary>
    string Get(string id);

    /// <summary>Whether an on/off setting is on.</summary>
    bool IsOn(string id);

    /// <summary>Raised with the setting's id after the user changes it, on any thread.</summary>
    event Action<string>? Changed;
}
