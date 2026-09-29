using System.Text.Json;
using System.Text.Json.Serialization;
using Tog.Core.Repos;

namespace Tog.Core.Agents;

/// <summary>One of the subscription plan's usage limits, as Claude last reported it.</summary>
/// <param name="Window">Which limit: <c>five_hour</c>, <c>seven_day</c>, <c>seven_day_opus</c> and so on.</param>
/// <param name="Utilization">The share of it used, 0 to 1, when reported.</param>
/// <param name="Threshold">A warning line it has crossed, 0 to 1, for when only that is reported.</param>
/// <param name="Status"><c>allowed</c>, <c>allowed_warning</c> or <c>rejected</c>, when reported.</param>
/// <param name="ResetsAt">When the window starts over.</param>
public sealed record PlanLimit(
    string Window,
    double? Utilization,
    double? Threshold,
    string? Status,
    DateTimeOffset? ResetsAt,
    DateTimeOffset SeenAt)
{
    private static readonly string[] Order =
        ["five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet", "seven_day_overage_included", "overage"];

    /// <summary>Used as a whole percentage, or the threshold it passed when that is all there is.</summary>
    [JsonIgnore]
    public int? Percent => (Utilization ?? Threshold) is { } share ? (int)Math.Round(Math.Clamp(share, 0, 1) * 100) : null;

    /// <summary>True when <see cref="Percent"/> is a threshold passed, so the real figure is at least that.</summary>
    [JsonIgnore]
    public bool AtLeast => Utilization is null && Threshold is not null;

    [JsonIgnore]
    public bool Blocked => Status == "rejected";

    [JsonIgnore]
    public bool Warning => Status == "allowed_warning";

    [JsonIgnore]
    public string Label => Window switch
    {
        "five_hour" => "5-hour",
        "seven_day" => "Weekly",
        "seven_day_opus" => "Opus weekly",
        "seven_day_sonnet" => "Sonnet weekly",
        "seven_day_overage_included" => "Weekly with extra",
        "overage" => "Extra usage",
        _ => Window,
    };

    /// <summary>How long the window runs, for the windows named by their length.</summary>
    [JsonIgnore]
    public TimeSpan? Length => Window switch
    {
        "five_hour" => TimeSpan.FromHours(5),
        _ when Window.StartsWith("seven_day", StringComparison.Ordinal) => TimeSpan.FromDays(7),
        _ => null,
    };

    /// <summary>
    /// The share of the window gone by, 0 to 1. Only the reset is reported, so
    /// the start is taken as the reset less the window's length.
    /// </summary>
    public double? Elapsed(DateTimeOffset now) =>
        Length is { } length && ResetsAt is { } reset ? Math.Clamp(1 - (reset - now) / length, 0, 1) : null;

    internal int Rank =>Array.IndexOf(Order, Window) is var i and >= 0 ? i : Order.Length;

    /// <summary>Whether this reading still describes the window it was taken in.</summary>
    public bool CurrentAt(DateTimeOffset now) => ResetsAt is not { } reset || reset > now;

    /// <summary>
    /// Reads the <c>_claude/rateLimit</c> the Claude bridge attaches to a
    /// <c>usage_update</c>.
    /// </summary>
    /// <remarks>
    /// The payload is the SDK's <c>SDKRateLimitInfo</c>: the headline is the limit
    /// the last response was measured against (<c>rateLimitType</c>), and newer
    /// versions add <c>unifiedWindows</c> with the 5-hour and weekly windows side
    /// by side. Both are read, and the headline wins where they overlap because it
    /// alone carries a status. Times are Unix seconds and shares are fractions.
    /// </remarks>
    public static IReadOnlyList<PlanLimit> Read(JsonElement rateLimit, DateTimeOffset now)
    {
        if (rateLimit.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var limits = new Dictionary<string, PlanLimit>(StringComparer.Ordinal);
        if (rateLimit.TryGetProperty("unifiedWindows", out var windows) && windows.ValueKind == JsonValueKind.Object)
        {
            foreach (var window in windows.EnumerateObject())
            {
                limits[window.Name] = new PlanLimit(
                    window.Name, Share(window.Value, "utilization"), null, null, Time(window.Value, "resetsAt"), now);
            }
        }

        if (AcpClient.Text(rateLimit, "rateLimitType") is { Length: > 0 } type)
        {
            limits.TryGetValue(type, out var unified);
            limits[type] = new PlanLimit(
                type,
                Share(rateLimit, "utilization") ?? unified?.Utilization,
                Share(rateLimit, "surpassedThreshold"),
                AcpClient.Text(rateLimit, "status"),
                Time(rateLimit, "resetsAt") ?? unified?.ResetsAt,
                now);
        }

        return [.. limits.Values];
    }

    /// <summary>
    /// Folds a new reading into what was known. A reading without a figure keeps
    /// the last one while the window is the same, since a status-only event does
    /// not mean usage went back to nothing.
    /// </summary>
    public PlanLimit After(PlanLimit? earlier) =>
        earlier is null || earlier.ResetsAt != ResetsAt
            ? this
            : this with
            {
                Utilization = Utilization ?? earlier.Utilization,
                Threshold = Threshold ?? earlier.Threshold,
                Status = Status ?? earlier.Status,
            };

    private static double? Share(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds((long)seconds)
            : null;
}

/// <summary>
/// The plan's usage limits as last reported, kept across restarts so a weekly
/// figure does not vanish until the next turn.
/// </summary>
public sealed class PlanUsageStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly Lock _gate = new();

    private string FilePath => Path.Combine(paths.Root, "plan-usage.json");

    public IReadOnlyList<PlanLimit> Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<PlanLimit>>(File.ReadAllText(FilePath), Options) ?? []
                    : [];
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    public void Save(IEnumerable<PlanLimit> limits)
    {
        lock (_gate)
        {
            paths.EnsureCreated();
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(limits.ToList(), Options));
            File.Move(temp, FilePath, overwrite: true);
        }
    }
}
