using System.Net;
using AgentsDashboard.App;
using System.Net.Sockets;
using AgentsDashboard.App.Components;
using AgentsDashboard.App.Services;
using AgentsDashboard.Core.Agents;
using AgentsDashboard.Core.Claude;
using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Monitoring;
using AgentsDashboard.Core.Platform;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Core.Review;
using AgentsDashboard.App.Extensions;
using AgentsDashboard.Extensions;

// The dashboard is a local web app in a native window. Blazor Server rather than
// a hybrid webview because its circuit is exactly the push channel this needs:
// a file watcher on a background thread publishes a snapshot and every open view
// re-renders, with no polling from the browser. It also means --browser works
// with no extra code, which is how you check on agents from another device.
// `mcp` is not the app: it is the stdio server Claude starts for an agent in a
// terminal, which forwards to the running app and exits with its stdin. It
// returns before anything else here runs, since whatever it writes to stdout
// is read as MCP. See McpStdioBridge.
if (args is ["mcp", .. var bridgeArgs])
{
    await McpStdioBridge.RunConsoleAsync(new AppPaths(CliOptions.Parse(bridgeArgs).DataDir).McpLinkFile);
    return;
}

var options = CliOptions.Parse(args);

// By default the builder watches the content root to reload appsettings.json,
// which this app does not have. On macOS starting that watcher calls sync(),
// which waits for every disk on the machine to flush: with agents building,
// seconds before the window could load. See PathWatcher. The builder only reads
// the switch from the environment (a command-line switch comes too late), and
// it is put back at once so the processes the app starts, an agent's dotnet
// among them, do not inherit it.
const string ReloadSwitch = "DOTNET_hostBuilder__reloadConfigOnChange";
var reload = Environment.GetEnvironmentVariable(ReloadSwitch);
Environment.SetEnvironmentVariable(ReloadSwitch, "false");
var builder = WebApplication.CreateBuilder(args);
Environment.SetEnvironmentVariable(ReloadSwitch, reload);

// Every process the app starts (git above all, many times a second) resolves
// its program with the working folder in hand, and once that folder is gone
// every start throws. The app is often started from inside a worktree, and
// worktrees get removed, so the working folder moves somewhere that stays.
// The content root and the options were made absolute before this.
Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

// Makes the framework's own web assets (blazor.web.js above all) resolvable when
// the app is run from its build output rather than a publish. CreateBuilder only
// does this for itself in the Development environment, and this app is normally
// launched with no environment set at all.
builder.WebHost.UseStaticWebAssets();

builder.Logging.SetMinimumLevel(options.Verbose ? LogLevel.Information : LogLevel.Warning);

var port = options.Port ?? FreePort();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

// The editor saves by sending the whole edited file up the circuit, and the
// hub's default cap on a client-to-server message is 32 KB, which most source
// files are comfortably over. WorktreeFiles.MaxBytes refuses to open anything
// past 2 MB, so that plus room for the interop envelope is the real ceiling.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 4 * 1024 * 1024);

// Platform
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IClipboard, Clipboard>();

// Storage and settings
builder.Services.AddSingleton(new AppPaths(options.DataDir));
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<Shortcuts>();
builder.Services.AddSingleton<ReviewDraftStore>();
builder.Services.AddSingleton<LastViewStore>();
builder.Services.AddSingleton<PanelLayoutStore>();
builder.Services.AddSingleton<SeenTurnsStore>();
builder.Services.AddSingleton<WindowBoundsStore>();

// Claude
builder.Services.AddSingleton(new ClaudePaths());
builder.Services.AddSingleton<TranscriptLocator>();
builder.Services.AddSingleton<PastSessionReader>();
builder.Services.AddSingleton<ConversationReader>();
builder.Services.AddSingleton<TranscriptReader>();
builder.Services.AddSingleton<SubagentReader>();
builder.Services.AddSingleton<AgentDirectory>();
builder.Services.AddSingleton<ChatDrafts>();
builder.Services.AddSingleton<WorktreeViews>();
builder.Services.AddScoped<Workbench>();

// Agents
// Run over the Agent Client Protocol, inside this process. Claude is one agent
// backend, launched through the official ACP bridge that tools/vendor-acp.mjs
// installs next to the project; another agent is another AgentBackend.
builder.Services.AddSingleton(_ => AgentBackends.Claude(builder.Environment.ContentRootPath));
builder.Services.AddSingleton<IAgentLauncher, ProcessAgentLauncher>();
builder.Services.AddSingleton<HostedAgentStore>();
builder.Services.AddSingleton<PlanUsageStore>();
// The app's and the extensions' agent tools, served as an MCP server on this
// host and handed to every session the agent host starts or resumes.
builder.Services.AddSingleton<IAgentTool, OpenFileTool>();
builder.Services.AddSingleton<IAgentTool, ExtensionGuideTool>();
builder.Services.AddSingleton<IAgentTool, ExtensionAddTool>();
builder.Services.AddSingleton(new AgentToolServer.Endpoint(port));
builder.Services.AddSingleton<AgentToolServer>();
builder.Services.AddSingleton<IAgentMcpServers>(sp => sp.GetRequiredService<AgentToolServer>());
// The same tools for agents started in a terminal, added to Claude from Settings.
builder.Services.AddSingleton(BridgeCommand(options.DataDir));
builder.Services.AddSingleton<ClaudeMcpConfig>();
builder.Services.AddSingleton<AgentHost>();
builder.Services.AddSingleton<IAgentSessionSource>(sp => sp.GetRequiredService<AgentHost>());

// Git
builder.Services.AddSingleton<IGitCli>(_ => new GitCli());
builder.Services.AddSingleton<WorktreeLister>();
builder.Services.AddSingleton<StatusReader>();
builder.Services.AddSingleton<DiffReader>();
builder.Services.AddSingleton<Staging>();
builder.Services.AddSingleton<WorktreeFiles>();
builder.Services.AddSingleton<RepoDiscovery>();
builder.Services.AddSingleton<WorktreeCreator>();
builder.Services.AddSingleton<WorktreeCleanup>();
builder.Services.AddSingleton<WorktreeInventory>();

// Extensions
// The API services are in the app's container as well as each extension's, so
// an extension's component can @inject them like any other.
builder.Services.AddSingleton(new ExtensionOptions(options.Extensions ?? [], options.NoExtensions));
builder.Services.AddSingleton<AgentsDashboard.Extensions.IDashboardView, DashboardViewAdapter>();
builder.Services.AddSingleton<AgentsDashboard.Extensions.INavigation, Navigation>();
builder.Services.AddSingleton<AgentsDashboard.Extensions.ITextLinker, TextLinker>();
builder.Services.AddScoped<AgentsDashboard.Extensions.IEditorTabs, EditorTabs>();
builder.Services.AddScoped<AgentsDashboard.Extensions.IAgentOffers, AgentOffers>();
builder.Services.AddSingleton<ExtensionHost>();
// What an agent needs to write one, and the prompt it raises to add one.
builder.Services.AddSingleton<AgentsDashboard.Core.Extensions.ExtensionSkill>();
builder.Services.AddSingleton<ExtensionRequests>();
// Language support is an extension's: this only routes the editor's questions
// to whichever loaded extension answers for the file.
builder.Services.AddSingleton<CodeNavigation>();

// Review
builder.Services.AddSingleton<FeedbackDispatcher>();

// The loop
builder.Services.AddSingleton<DashboardState>();
builder.Services.AddSingleton<INotifier, OsNotifier>();
builder.Services.AddSingleton<FolderPicker>();
builder.Services.AddSingleton<ImageViews>();
builder.Services.AddSingleton<OpenRequests>();
builder.Services.AddSingleton<AppUpdate>();
builder.Services.AddSingleton<MonitorService>();
builder.Services.AddHostedService<MonitorHost>();

var app = builder.Build();

// The clock is a static on Fmt rather than a service, so it is read once here and
// the Settings page sets it again when it changes.
AgentsDashboard.Core.Presentation.Fmt.TwentyFourHourClock =
    app.Services.GetRequiredService<SettingsStore>().Load().TwentyFourHourClock;

// MapStaticAssets rather than UseStaticFiles: it serves from the build-time
// asset manifest, so the framework's own files (blazor.web.js above all) are
// there whether the app is run from bin or from a publish output. UseStaticFiles
// only finds them when ASP.NET Core happens to be in the Development environment.
app.MapStaticAssets();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapExtensionAssets();
AgentToolServer.Map(app);
ImageViews.Map(app);

// Before the first page, so an extension's tabs are there when it draws.
app.Services.GetRequiredService<ExtensionHost>().Start();

// Agents in any terminal ask for a file to be shown by dropping a request in a
// folder; see OpenRequests. A request that beats the first window is held for it.
app.Services.GetRequiredService<OpenRequests>().Start();

await app.StartAsync();

// Written once the server answers, so a bridge never finds a link to nothing.
var mcpLinkFile = app.Services.GetRequiredService<AppPaths>().McpLinkFile;
app.Services.GetRequiredService<AgentToolServer>().Link().Write(mcpLinkFile);
app.Lifetime.ApplicationStopping.Register(() => McpLink.Withdraw(mcpLinkFile, Environment.ProcessId));

var url = $"http://127.0.0.1:{port}";

if (options.Browser)
{
    Console.WriteLine($"Agents Dashboard is running at {url}");
    Console.WriteLine("Press Ctrl+C to stop.");
    await app.WaitForShutdownAsync();
    return;
}

// Photino owns the main thread and blocks until the window closes, so the host
// is already started above rather than run to completion.
void OpenWindow() => DesktopWindow.Open(url, app.Services.GetRequiredService<ILoggerFactory>(),
    app.Services.GetRequiredService<FolderPicker>(), app.Services.GetRequiredService<AppUpdate>(),
    app.Services.GetRequiredService<WindowBoundsStore>(), fallbackUrlPrinted: () =>
    Console.WriteLine($"Agents Dashboard is running at {url}"));

if (OperatingSystem.IsWindows())
{
    // WebView2 and the folder dialog are COM and need a single-threaded
    // apartment. An async Main cannot be [STAThread], and after the await above
    // this may be a pool thread anyway, so the window gets a thread of its own.
    var windowThread = new Thread(OpenWindow) { Name = "Photino" };
    windowThread.SetApartmentState(ApartmentState.STA);
    windowThread.Start();
    windowThread.Join();
}
else
{
    OpenWindow();
}

await app.StopAsync();

static McpCommand BridgeCommand(string? dataDir)
{
    // The command that started this copy, so the entry follows whichever copy
    // added it. Run through `dotnet app.dll`, the process is dotnet itself and
    // the app is its first argument.
    var exe = Environment.ProcessPath ?? "AgentsDashboard.App";
    List<string> args = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? [typeof(Program).Assembly.Location, "mcp"]
        : ["mcp"];
    if (dataDir is not null)
    {
        args.AddRange(["--data-dir", dataDir]);
    }

    return new McpCommand(exe, args);
}

static int FreePort()
{
    // Bind port 0, read what the OS handed out, release it. A fixed port would
    // collide with a second copy of the dashboard, and with anything else on the
    // machine that took it first.
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

/// <summary>Makes the generated entry point visible to WebApplicationFactory and tests.</summary>
public partial class Program;
