namespace Tog.Core.Model;

/// <summary>What kind of line a conversation entry is.</summary>
public enum ChatKind
{
    /// <summary>Something a person typed to the agent.</summary>
    You,

    /// <summary>The agent's words.</summary>
    Agent,

    /// <summary>A run of tool calls between two things said, shown folded.</summary>
    Activity,

    /// <summary>Something the harness told the agent, such as a background task finishing.</summary>
    Notice,
}

/// <summary>One tool call, as a short phrase.</summary>
/// <param name="Tool">The tool's name, for example Bash or Edit.</param>
/// <param name="Summary">What it did: a command's description, a file path, a pattern.</param>
public sealed record ChatStep(string Tool, string Summary);

/// <summary>One line of a conversation with an agent.</summary>
/// <param name="Edits">For an <see cref="ChatKind.Activity"/> run, the file changes made in it with
/// the agent's edit tools, in order. Kept apart from <see cref="Steps"/> so they fold on their own.</param>
public sealed record ChatEntry(ChatKind Kind, DateTimeOffset At, string Text, IReadOnlyList<ChatStep> Steps)
{
    public IReadOnlyList<ChatEdit> Edits { get; init; } = [];

    public static ChatEntry Said(ChatKind kind, DateTimeOffset at, string text) => new(kind, at, text, []);

    /// <summary>
    /// The files changed in the run, each once however many times it was edited,
    /// with its edits in order.
    /// </summary>
    public IReadOnlyList<IGrouping<string, ChatEdit>> ChangedFiles =>
        Edits.GroupBy(e => e.Path, StringComparer.Ordinal).ToList();
}

/// <summary>A change the agent made to one file with Edit, MultiEdit or Write.</summary>
/// <param name="ToolUseId">The tool call's id, which its result is matched back to.</param>
/// <param name="Path">The file, as the agent named it: usually absolute.</param>
/// <param name="Created">Written from nothing rather than edited.</param>
/// <param name="Hunks">The diff. From the tool call's input until its result arrives, then from the
/// patch the result carries, which has line numbers and context.</param>
/// <param name="Failed">The tool reported an error, so the file was not changed.</param>
/// <param name="Truncated">Lines were left out to keep a very large change from swamping the chat.</param>
public sealed record ChatEdit(
    string ToolUseId,
    string Path,
    bool Created,
    IReadOnlyList<ChatHunk> Hunks,
    bool Failed = false,
    bool Truncated = false)
{
    public int Added => Hunks.Sum(h => h.Lines.Count(l => l.StartsWith('+')));

    public int Removed => Hunks.Sum(h => h.Lines.Count(l => l.StartsWith('-')));
}

/// <summary>One hunk of a diff, in unified-diff form.</summary>
/// <param name="OldStart">The first line in the old file, when known.</param>
/// <param name="NewStart">The first line in the new file, when known.</param>
/// <param name="Lines">Each line prefixed with <c>' '</c>, <c>'+'</c> or <c>'-'</c>.</param>
public sealed record ChatHunk(int? OldStart, int? NewStart, IReadOnlyList<string> Lines);
