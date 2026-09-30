namespace Tog.Core.Model;

/// <summary>How a folded run of tool calls reads in the chat.</summary>
public static class ChatSteps
{
    /// <summary>The most lines one edit shows. A write of a generated file can be thousands.</summary>
    public const int MaxEditLines = 400;

    /// <summary>How a run of steps reads folded: "ran 3 commands, edited 2 files".</summary>
    public static string Describe(IReadOnlyList<ChatStep> steps)
    {
        var parts = steps
            .GroupBy(s => Verb(s.Tool))
            .Select(g => g.Key.Count(g.Count()))
            .ToList();
        return parts.Count == 0 ? "" : char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..];
    }

    private sealed record VerbPhrase(string Verb, string One, string Many)
    {
        public string Count(int n) => n == 1 ? $"{Verb} {One}" : $"{Verb} {n} {Many}";
    }

    /// <summary>
    /// Claude's tool names, and the kinds ACP gives every tool call (lower
    /// case), which is all an agent without an adapter of its own history has.
    /// </summary>
    private static VerbPhrase Verb(string tool) => tool switch
    {
        "Bash" or "execute" => new("ran", "a command", "commands"),
        "Read" or "read" => new("read", "a file", "files"),
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "edit" or "delete" or "move" => new("edited", "a file", "files"),
        "Grep" or "Glob" or "search" => new("searched", "once", "times"),
        "Skill" => new("used", "a skill", "skills"),
        "Agent" or "Task" => new("started", "a subagent", "subagents"),
        "WebFetch" or "WebSearch" or "fetch" => new("looked up", "a page", "pages"),
        "TodoWrite" => new("updated", "its todo list", "its todo list"),
        "think" => new("thought", "once", "times"),
        "other" => new("used", "a tool", "tools"),
        _ => new("used", tool, tool),
    };
}
