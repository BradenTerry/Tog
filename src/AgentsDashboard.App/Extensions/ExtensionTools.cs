using AgentsDashboard.Core.Extensions;
using AgentsDashboard.Extensions;

namespace AgentsDashboard.App.Extensions;

/// <summary>
/// How to write a dashboard extension, for the agents the dashboard runs: the
/// same text as the skill Settings installs, so they need no skill for it.
/// </summary>
public sealed class ExtensionGuideTool(ExtensionSkill skill) : IAgentTool
{
    public string Name => "dashboard_extension_guide";

    public string Description =>
        "How to create an Agents Dashboard extension (a new tab, panel, indicator, background worker, agent tool or "
        + "editor language support for the dashboard you are running in): where the template and the SDK are on this "
        + "machine, the API version, the steps and the rules. Call it before scaffolding or changing an extension, "
        + "unless the agents-dashboard-extension skill is already loaded. When it is built, add it with "
        + "dashboard_extension_add.";

    public string InputSchema => """{ "type": "object", "properties": {} }""";

    public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct) =>
        Task.FromResult(skill.Text() is { } text
            ? new AgentToolResult(text)
            : AgentToolResult.Error("This build of the dashboard does not include the extension guide."));
}

/// <summary>Asks the user to add an extension an agent has built. See <see cref="ExtensionRequests"/>.</summary>
public sealed class ExtensionAddTool(ExtensionRequests requests) : IAgentTool
{
    public string Name => "dashboard_extension_add";

    public string Description =>
        "Asks the user to add and turn on an Agents Dashboard extension you have built: the dashboard shows them a "
        + "prompt with its name and folder, and it loads only if they accept. Build it first (dotnet build), so its "
        + "entry assembly exists. The call returns once the prompt is shown, not when the user answers; tell them it "
        + "is waiting for them in the dashboard. Once added, every later build reloads it without asking.";

    public string InputSchema => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The extension's folder, the one holding extension.json, absolute or relative to your working folder." }
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

        var folder = Path.IsPathFullyQualified(given) ? Path.GetFullPath(given) : Path.GetFullPath(given, call.Cwd!);
        if (File.Exists(folder) && Path.GetFileName(folder) == ExtensionManifests.FileName)
        {
            folder = Path.GetDirectoryName(folder)!;
        }

        return Task.FromResult(requests.Ask(folder, call.Cwd) is { } problem
            ? AgentToolResult.Error(problem)
            : new AgentToolResult($"The user has been asked to add the extension in {folder}. It loads if they accept."));
    }
}
