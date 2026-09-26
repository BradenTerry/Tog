namespace AgentsDashboard.App;

/// <summary>The app's URLs, built in one place.</summary>
public static class Urls
{
    /// <summary>The agent view, optionally open on one agent and one of its tabs.</summary>
    public static string Chat(string? sessionId = null, string? tab = null) =>
        sessionId is null ? "chat"
        : tab is null ? "chat/" + Uri.EscapeDataString(sessionId)
        : "chat/" + Uri.EscapeDataString(sessionId) + "/" + tab;

    /// <summary>One file in an agent's own Files tab, at an optional line.</summary>
    public static string AgentFile(string sessionId, string relativeFile, int? line = null)
    {
        var url = Chat(sessionId, "files") + "?file=" + Uri.EscapeDataString(relativeFile);
        return line is null ? url : url + "&line=" + line;
    }
}
