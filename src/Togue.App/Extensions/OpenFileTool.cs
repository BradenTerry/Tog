using Togue.Core.Platform;
using Togue.Extensions;

namespace Togue.App.Extensions;

/// <summary>
/// The app's own agent tool: opens a file as a tab in Togue, the way a
/// request dropped into the open folder does.
/// </summary>
/// <remarks>
/// The folder still serves agents in a terminal, which do not get this server.
/// An agent Togue runs is better off here: it hears back whether the
/// file exists and whether a window took it, where a dropped request can only
/// be checked by watching the folder empty. A relative path is taken from the
/// agent's own folder, which the folder route cannot know.
/// </remarks>
public sealed class OpenFileTool(OpenRequests requests) : IAgentTool
{
    public string Name => "togue_open_file";

    public string Description =>
        "Shows a file to the user as a tab in the Togue editor. Images (png, jpeg, gif, webp, svg) open as "
        + "pictures, text files as text, which the user can edit and save. Use it for "
        + "whatever the user should look at, above all screenshots. Opening the same path again brings it forward and "
        + "reads it again, so after retaking a screenshot, open it again.";

    public string InputSchema => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The file, absolute or relative to your working folder." },
            "line": { "type": "integer", "minimum": 1, "description": "A line of a text file to land on." }
          },
          "required": ["path"]
        }
        """;

    public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct)
    {
        if (call.Text("path") is not { Length: > 0 } given)
        {
            return Task.FromResult(AgentToolResult.Error("path is required."));
        }

        if (!Path.IsPathFullyQualified(given) && call.Cwd is null)
        {
            return Task.FromResult(AgentToolResult.Error($"{given} is relative and your working folder is not known; pass an absolute path."));
        }

        // No "/" fallback for a missing folder: on Windows it is not a full path,
        // and GetFullPath throws even when the path given is absolute.
        var path = Path.IsPathFullyQualified(given) ? Path.GetFullPath(given) : Path.GetFullPath(given, call.Cwd!);
        if (Directory.Exists(path))
        {
            return Task.FromResult(AgentToolResult.Error($"{path} is a folder; name a file."));
        }

        if (!File.Exists(path))
        {
            return Task.FromResult(AgentToolResult.Error($"There is no file at {path}."));
        }

        int? line = call.Number("line") is > 0 and <= int.MaxValue and var n ? (int)n : null;
        return Task.FromResult(requests.Open(new OpenRequest(path, line))
            ? new AgentToolResult($"Opened {path} in Togue.")
            : new AgentToolResult($"No Togue window is open; {path} opens in the first one that does."));
    }
}
