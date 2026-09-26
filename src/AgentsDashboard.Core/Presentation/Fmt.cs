using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Presentation;

/// <summary>
/// Turning the model into the words the views show.
/// </summary>
/// <remarks>
/// Kept out of the UI project so it can be tested without an ASP.NET host. It is
/// pure string formatting over the model and has no rendering concerns of its own.
/// </remarks>
public static class Fmt
{
    /// <summary>
    /// A duration at the precision a person actually reads: seconds while it is
    /// seconds, then minutes, then hours. "4m 12s" rather than "00:04:12".
    /// </summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 60)
        {
            return $"{(int)span.TotalSeconds}s";
        }

        if (span.TotalMinutes < 60)
        {
            var seconds = span.Seconds;
            return seconds == 0 ? $"{(int)span.TotalMinutes}m" : $"{(int)span.TotalMinutes}m {seconds}s";
        }

        var minutes = span.Minutes;
        return minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {minutes}m";
    }

    /// <summary>How long ago something happened.</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now) =>
        now - at < TimeSpan.FromSeconds(5) ? "just now" : Duration(now - at) + " ago";

    /// <summary>
    /// When something last happened, for things measured in days rather than
    /// seconds: a timer within the day, then days, then the date. "385h ago" is
    /// accurate and useless.
    /// </summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromDays(1))
        {
            return Ago(at, now);
        }

        if (age < TimeSpan.FromDays(7))
        {
            var days = (int)age.TotalDays;
            return $"{days} {Plural(days, "day", "days")} ago";
        }

        var local = at.ToLocalTime();
        return local.Year == now.ToLocalTime().Year
            ? local.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture)
            : local.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A fixed point in time: the time of day when it was today, the date when it
    /// was not. For things that are over, where "12m ago" would keep counting.
    /// </summary>
    public static string At(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime();
        var invariant = System.Globalization.CultureInfo.InvariantCulture;

        if (local.Date == today.Date)
        {
            return local.ToString("h:mm tt", invariant);
        }

        return local.Year == today.Year
            ? local.ToString("MMM d", invariant)
            : local.ToString("MMM d, yyyy", invariant);
    }

    public static string StatusName(AgentStatus status) => status switch
    {
        AgentStatus.Waiting => "waiting",
        AgentStatus.Active => "active",
        _ => "idle",
    };

    /// <summary>
    /// What a status means, for the title attribute. Status is shown as a colour
    /// and a word; this is the sentence behind both.
    /// </summary>
    public static string StatusHelp(AgentStatus status) => status switch
    {
        AgentStatus.Waiting => "Blocked on you: a permission prompt or a question",
        AgentStatus.Active => "Working: a prompt, a tool call or a shell command",
        _ => "Idle: started, or finished responding and awaiting you",
    };

    public static string Plural(int n, string one, string many) => n == 1 ? one : many;

    /// <summary>A file size at the precision a person reads.</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    public static string Count(int n, string noun) => $"{n} {Plural(n, noun, noun + "s")}";

    /// <summary>A short, readable git position: "3 ahead, 1 behind".</summary>
    public static string? Position(GitStatusInfo status)
    {
        var parts = new List<string>(2);
        if (status.Ahead > 0)
        {
            parts.Add($"{status.Ahead} ahead");
        }

        if (status.Behind > 0)
        {
            parts.Add($"{status.Behind} behind");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>A one-line summary of a worktree's changes, or null when clean.</summary>
    public static string? Changes(GitStatusInfo status)
    {
        var parts = new List<string>(3);
        if (status.Staged > 0)
        {
            parts.Add($"{status.Staged} staged");
        }

        if (status.Changed > 0)
        {
            parts.Add($"{status.Changed} changed");
        }

        if (status.Untracked > 0)
        {
            parts.Add($"{status.Untracked} new");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public static string RunStateName(TestRunState state) => state switch
    {
        TestRunState.Running => "running",
        TestRunState.Passed => "passed",
        TestRunState.Failed => "failed",
        _ => "stopped",
    };

    /// <summary>The last path segment, for a label that must stay short.</summary>
    public static string Leaf(string path) =>
        Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : path;

    /// <summary>Everything but the last path segment, for the dimmer prefix beside it.</summary>
    public static string Parent(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "" : normalized[..(slash + 1)];
    }
}
