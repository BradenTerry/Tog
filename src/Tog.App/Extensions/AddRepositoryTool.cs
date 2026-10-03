using Tog.Core.Monitoring;
using Tog.Core.Repos;
using Tog.Extensions;

namespace Tog.App.Extensions;

/// <summary>
/// The app's own agent tool: lists a repository in Tog, as Add under
/// Settings, Repositories does.
/// </summary>
/// <remarks>
/// Done straight away rather than offered, unlike an extension: listing a
/// repository only makes New agent offer it. It never marks one trusted, so the
/// first agent started there still asks you, and taking it off is one click in
/// Settings.
/// </remarks>
public sealed class AddRepositoryTool(SettingsStore store, MonitorService monitor) : IAgentTool
{
    public string Name => "tog_add_repository";

    public string Description =>
        "Lists a git repository in Tog, so the user can start agents in it from New agent. "
        + "Pass the repository's folder, or a folder ending in /* to list every repository directly inside it, "
        + "including ones cloned there later. It does not let any agent into the repository: the user is still "
        + "asked the first time an agent is started there.";

    public string InputSchema => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The repository's folder, or a folder followed by /*, absolute or relative to your working folder." }
          },
          "required": ["path"]
        }
        """;

    public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct)
    {
        if (call.Text("path")?.Trim() is not { Length: > 0 } given)
        {
            return Task.FromResult(AgentToolResult.Error("path is required."));
        }

        var wildcard = RepoDiscovery.WildcardFolder(given);
        var folder = wildcard ?? given;
        if (!Path.IsPathFullyQualified(folder) && call.Cwd is null)
        {
            return Task.FromResult(AgentToolResult.Error($"{given} is relative and your working folder is not known; pass an absolute path."));
        }

        // Stored the way Settings would store a folder picked there: full, with
        // no trailing separator, so the same repository is not listed twice.
        var full = Path.TrimEndingDirectorySeparator(
            Path.IsPathFullyQualified(folder) ? Path.GetFullPath(folder) : Path.GetFullPath(folder, call.Cwd!));
        var root = wildcard is null ? full : Path.Join(full, "*");

        var check = RepoList.Check(root);
        if (!check.Added)
        {
            return Task.FromResult(AgentToolResult.Error($"{full}: {check.Note}"));
        }

        var settings = store.Load();
        if (RepoList.IsHidden(settings, root))
        {
            // Settings' Add unhides, since there the user is asking to see it again.
            // An agent is not the user, so a choice to hide it stands.
            return Task.FromResult(AgentToolResult.Error($"The user hid {root} in Tog; they can show it again in Settings."));
        }

        if (RepoList.Contains(settings, root))
        {
            return Task.FromResult(new AgentToolResult($"{root} is already listed in Tog."));
        }

        store.Save(RepoList.Add(settings, root));

        // The repo list is read on the monitor's slow timer; without this it
        // would take up to twenty seconds to appear.
        monitor.InvalidateWorktrees();

        var added = $"Listed {root} in Tog; New agent now offers it.";
        return Task.FromResult(new AgentToolResult(check.Note is { } note ? $"{added} {note}" : added));
    }
}
