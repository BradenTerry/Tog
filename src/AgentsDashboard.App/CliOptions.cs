namespace AgentsDashboard.App;

/// <summary>How the dashboard was asked to start.</summary>
/// <param name="Browser">Skip the native window and just serve the app.</param>
/// <param name="Port">A fixed port, when the user wants a stable URL.</param>
/// <param name="Verbose">Log at information level rather than warnings only.</param>
/// <param name="DataDir">
/// Where the dashboard keeps its own settings, in place of <c>~/.agents-dashboard</c>.
/// For a demo or a test run that should not see, or change, your real settings.
/// </param>
public sealed record CliOptions(bool Browser, int? Port, bool Verbose, string? DataDir = null)
{
    public static CliOptions Parse(string[] args)
    {
        var browser = false;
        int? port = null;
        var verbose = false;
        string? dataDir = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--browser" or "-b":
                    browser = true;
                    break;

                case "--verbose" or "-v":
                    verbose = true;
                    break;

                case "--port" or "-p":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
                    {
                        port = p;
                        i++;
                    }

                    break;

                case "--data-dir":
                    if (i + 1 < args.Length)
                    {
                        dataDir = args[++i];
                    }

                    break;
            }
        }

        return new CliOptions(browser, port, verbose, dataDir);
    }
}
