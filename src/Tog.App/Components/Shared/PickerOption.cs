namespace Tog.App.Components.Shared;

/// <summary>
/// One entry in a <see cref="Picker"/>. <paramref name="Detail"/> is a quieter
/// second line; consecutive options with the same <paramref name="Group"/> are
/// listed under it as a heading.
/// </summary>
public sealed record PickerOption(string Value, string Label, string? Detail = null, string? Group = null);
