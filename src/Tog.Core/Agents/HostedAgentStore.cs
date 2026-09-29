using System.Text.Json;
using Tog.Core.Repos;

namespace Tog.Core.Agents;

/// <summary>An agent Tog runs, as remembered between runs.</summary>
/// <param name="Prompt">Its first prompt, shown in place of a title until the agent names it.</param>
/// <param name="Context">How full its context window was last reported, so a stopped agent still shows it.</param>
/// <param name="Commands">The slash commands it last listed, so a stopped agent still offers them after a restart.</param>
/// <param name="TurnEndedAt">When its last turn ended, so one finished while you were away is still unread after a restart.</param>
public sealed record HostedAgentRecord(
    string SessionId,
    string Cwd,
    string? Title,
    DateTimeOffset AddedAt,
    string? Prompt = null,
    ContextUsage? Context = null,
    IReadOnlyList<AcpCommand>? Commands = null,
    DateTimeOffset? TurnEndedAt = null);

/// <summary>
/// The agents in the agent list, kept across restarts.
/// </summary>
/// <remarks>
/// Tog hosts its agents, so closing it ends their processes. Their
/// conversations are saved by Claude either way; this list is what lets them
/// come back as stopped agents you can pick up again, rather than vanishing from
/// the agent list until you go looking for them under Resume.
/// </remarks>
public sealed class HostedAgentStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly Lock _gate = new();

    private string FilePath => Path.Combine(paths.Root, "agents.json");

    public IReadOnlyList<HostedAgentRecord> Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<HostedAgentRecord>>(File.ReadAllText(FilePath), Options) ?? []
                    : [];
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    public void Save(IEnumerable<HostedAgentRecord> agents)
    {
        lock (_gate)
        {
            paths.EnsureCreated();
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(agents.ToList(), Options));
            File.Move(temp, FilePath, overwrite: true);
        }
    }
}
