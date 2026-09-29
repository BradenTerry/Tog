using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tog.Core.Agents;

namespace Tog.Core.Claude;

/// <summary>How Claude starts Tog's stdio bridge: the app itself, with <c>mcp</c>.</summary>
public sealed record McpCommand(string Command, IReadOnlyList<string> Args);

public enum McpEntryState
{
    /// <summary>Claude has no server called <see cref="McpStdioBridge.Name"/>.</summary>
    Missing,

    /// <summary>It starts this copy of the app.</summary>
    Current,

    /// <summary>It starts something else: another copy of the app, one moved since, or a server of the user's own.</summary>
    Different,
}

/// <summary>
/// Tog's entry in Claude Code's user-wide MCP servers, so an agent
/// started in a terminal gets the same tools as one started in Tog.
/// </summary>
/// <remarks>
/// <para>
/// Only ever added or removed from the button in Settings, and through
/// <c>claude mcp</c> rather than by editing Claude's file: Claude rewrites that
/// file all the time from every session, and its own command is the one
/// writer that knows how to take its turn. The file is only read here, to show
/// whether the entry is there.
/// </para>
/// <para>
/// User scope rather than a project's <c>.mcp.json</c>, because Tog
/// watches every repository and a project file is a change to the user's
/// repository, which the app never makes on its own.
/// </para>
/// </remarks>
public sealed class ClaudeMcpConfig(ClaudePaths claude)
{
    public string ConfigFile => claude.UserConfigFile;

    /// <summary>Whether Claude looks installed: its config tree or file exists.</summary>
    public bool Available => Directory.Exists(claude.Root) || File.Exists(claude.UserConfigFile);

    public McpEntryState State(McpCommand ours)
    {
        JsonNode? entry;
        try
        {
            entry = JsonNode.Parse(File.ReadAllText(ConfigFile))?["mcpServers"]?[McpStdioBridge.Name];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return McpEntryState.Missing;
        }

        if (entry is not JsonObject found)
        {
            return McpEntryState.Missing;
        }

        var args = found["args"] as JsonArray;
        var same = Text(found["command"]) == ours.Command
            && Text(found["type"]) is null or "stdio"
            && (args?.Select(Text).ToArray() ?? []).SequenceEqual(ours.Args);
        return same ? McpEntryState.Current : McpEntryState.Different;
    }

    /// <summary>The entry as Claude stores it.</summary>
    public static string EntryJson(McpCommand ours) => new JsonObject
    {
        ["type"] = "stdio",
        ["command"] = ours.Command,
        ["args"] = new JsonArray([.. ours.Args.Select(a => (JsonNode?)a)]),
    }.ToJsonString();

    /// <summary><c>add-json</c> rather than <c>add</c>, so no path in the command is read as one of its options.</summary>
    public static IReadOnlyList<string> AddArgs(McpCommand ours) =>
        ["mcp", "add-json", "--scope", "user", McpStdioBridge.Name, EntryJson(ours)];

    public static IReadOnlyList<string> RemoveArgs => ["mcp", "remove", "--scope", "user", McpStdioBridge.Name];

    /// <summary>The add command to paste into a shell, for when the app cannot find <c>claude</c> to run it.</summary>
    public static string ShellCommand(McpCommand ours) =>
        $"claude mcp add-json --scope user {McpStdioBridge.Name} '{EntryJson(ours).Replace("'", "'\\''", StringComparison.Ordinal)}'";

    /// <summary>Adds the entry, replacing one of the same name. Returns why not, or null.</summary>
    public async Task<string?> AddAsync(McpCommand ours, CancellationToken ct = default)
    {
        // add-json refuses a name that is taken, and an entry that points at
        // another copy of the app is exactly what Update is for.
        if (State(ours) != McpEntryState.Missing && await RunAsync(RemoveArgs, ct) is { } removeFailed)
        {
            return removeFailed;
        }

        return await RunAsync(AddArgs(ours), ct);
    }

    public Task<string?> RemoveAsync(CancellationToken ct = default) => RunAsync(RemoveArgs, ct);

    private async Task<string?> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (FindClaude() is not { } exe)
        {
            return "Could not find the claude command. Run the command below in a terminal instead.";
        }

        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode == 0)
            {
                return null;
            }

            var said = (await error).Trim() is { Length: > 0 } e ? e : (await output).Trim();
            return said.Length > 0 ? said : $"claude exited with code {process.ExitCode}.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "claude did not answer within 30 seconds.";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// The claude command. An app opened from the Finder or the Start menu has
    /// a bare PATH, so the places Claude's installers put it are tried too.
    /// </summary>
    public static string? FindClaude()
    {
        // The npm install on Windows is a .cmd, which cannot be handed JSON
        // arguments safely, so only the native claude.exe is run there.
        var name = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name));
        string[] known =
        [
            Path.Combine(home, ".local", "bin", name),
            Path.Combine(home, ".claude", "local", name),
            "/opt/homebrew/bin/claude",
            "/usr/local/bin/claude",
        ];

        return onPath.Concat(known).FirstOrDefault(File.Exists);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
