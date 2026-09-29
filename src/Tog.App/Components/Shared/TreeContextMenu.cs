namespace Tog.App.Components.Shared;

/// <summary>A right click on a tree row: the row, and where the pointer was in the window.</summary>
public readonly record struct TreeContextMenu(string Path, double X, double Y);
