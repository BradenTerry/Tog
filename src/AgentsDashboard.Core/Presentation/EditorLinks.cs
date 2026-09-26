namespace AgentsDashboard.Core.Presentation;

/// <summary>
/// Links that open a file in the user's editor.
/// </summary>
/// <remarks>
/// Only the VS Code scheme is built here, because that is the one VS Code itself
/// registers with the OS. Cursor and VS Code Insiders are separate applications
/// with their own schemes (<c>cursor://</c> and <c>vscode-insiders://</c>), so a
/// link made here does not open them, and guessing between the three from inside
/// the app would open the wrong editor as often as the right one.
/// </remarks>
public static class EditorLinks
{
    /// <summary>A <c>vscode://file</c> link to an absolute path, optionally at a position.</summary>
    public static string VsCode(string absolutePath, int? line = null, int? column = null)
    {
        var path = absolutePath.Replace('\\', '/');

        // Escaping the whole path would escape the separators with it and leave one
        // opaque segment, so each segment is escaped and the slashes are put back.
        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

        var url = "vscode://file" + escaped;

        if (line is null)
        {
            return url;
        }

        return column is null ? $"{url}:{line}" : $"{url}:{line}:{column}";
    }
}
