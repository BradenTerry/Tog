using System.Diagnostics;
using System.Text.Json;

namespace AgentsDashboard.Core.Agents;

/// <summary>
/// Where the running dashboard serves its agent tools, and the key an agent
/// outside it calls with, as the app leaves it for <see cref="McpStdioBridge"/>.
/// </summary>
/// <remarks>
/// <para>
/// A file rather than a fixed address because the port is picked fresh on
/// every start, so a Claude config entry cannot name it. The bridge Claude
/// runs reads this on every message and so follows the app across restarts.
/// </para>
/// <para>
/// The key is minted per start and the file is readable by the user alone,
/// the same guard as <c>OpenRequests</c>: only the user's own processes can
/// call. What it cannot say is which agent is calling, so a tool called with
/// it gets the folder the bridge was started in and no agent id.
/// </para>
/// </remarks>
public sealed record McpLink(string Url, string Key, int Pid)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The link in <paramref name="file"/>, or null when there is none or its app has exited.</summary>
    public static McpLink? Read(string file)
    {
        McpLink? link;
        try
        {
            link = JsonSerializer.Deserialize<McpLink>(File.ReadAllText(file), Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        return link is { Url.Length: > 0, Key.Length: > 0 } && IsRunning(link.Pid) ? link : null;
    }

    /// <summary>Writes the link under another name and renames it, so a reader never sees half of it.</summary>
    public void Write(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = $"{file}.{Environment.ProcessId}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(stream, this, Json);
        }

        File.Move(temp, file, overwrite: true);
    }

    /// <summary>
    /// Deletes the file if it is still this process's. A second copy of the app
    /// started later took it over, and its link stays.
    /// </summary>
    public static void Withdraw(string file, int pid)
    {
        try
        {
            if (JsonSerializer.Deserialize<McpLink>(File.ReadAllText(file), Json)?.Pid == pid)
            {
                File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    /// <summary>A crash leaves the file behind, and its key would only be refused.</summary>
    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
