namespace AgentsDashboard.Core.Model;

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
public sealed record ChatEntry(ChatKind Kind, DateTimeOffset At, string Text, IReadOnlyList<ChatStep> Steps)
{
    public static ChatEntry Said(ChatKind kind, DateTimeOffset at, string text) => new(kind, at, text, []);
}
