namespace Tog.App;

/// <summary>The app's URLs, built in one place.</summary>
public static class Urls
{
    /// <summary>An agent's page, optionally bringing up one of its panel tabs.</summary>
    public static string Chat(string? sessionId = null, string? tab = null) =>
        sessionId is null ? "chat"
        : tab is null ? "chat/" + Uri.EscapeDataString(sessionId)
        : "chat/" + Uri.EscapeDataString(sessionId) + "/" + tab;

    /// <summary>One file opened in an agent's editor, at an optional line.</summary>
    public static string AgentFile(string sessionId, string relativeFile, int? line = null)
    {
        var url = Chat(sessionId, "files") + "?file=" + Uri.EscapeDataString(relativeFile);
        return line is null ? url : url + "&line=" + line;
    }
}
