using System.Net;
using AgentsDashboard.App;
using System.Net.Sockets;
using AgentsDashboard.App.Components;
using AgentsDashboard.App.Services;
using AgentsDashboard.Core.Claude;
using AgentsDashboard.Core.Code;
using AgentsDashboard.Core.Git;
using AgentsDashboard.Core.Monitoring;
using AgentsDashboard.Core.Platform;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Core.Review;
using AgentsDashboard.Core.Testing;

// The dashboard is a local web app in a native window. Blazor Server rather than
// a hybrid webview because its circuit is exactly the push channel this needs:
// a file watcher on a background thread publishes a snapshot and every open view
// re-renders, with no polling from the browser. It also means --browser works
// with no extra code, which is how you check on agents from another device.
var options = CliOptions.Parse(args);

var builder = WebApplication.CreateBuilder(args);

// Makes the framework's own web assets (blazor.web.js above all) resolvable when
// the app is run from its build output rather than a publish. CreateBuilder only
// does this for itself in the Development environment, and this app is normally
// launched with no environment set at all.
builder.WebHost.UseStaticWebAssets();

builder.Logging.SetMinimumLevel(options.Verbose ? LogLevel.Information : LogLevel.Warning);

var port = options.Port ?? FreePort();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

// The Files tab saves by sending the whole edited file up the circuit, and the
// hub's default cap on a client-to-server message is 32 KB, which most source
// files are comfortably over. WorktreeFiles.MaxBytes refuses to open anything
// past 2 MB, so that plus room for the interop envelope is the real ceiling.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 4 * 1024 * 1024);

// Platform
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IProcessProbe, ProcessProbe>();
builder.Services.AddSingleton<IClipboard, Clipboard>();

// Storage and settings
builder.Services.AddSingleton(new AppPaths(options.DataDir));
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<ReviewDraftStore>();

// Claude
builder.Services.AddSingleton(new ClaudePaths());
builder.Services.AddSingleton<SessionRegistryReader>();
builder.Services.AddSingleton<TranscriptLocator>();
builder.Services.AddSingleton<ConversationReader>();
builder.Services.AddSingleton<TranscriptReader>();
builder.Services.AddSingleton<SubagentReader>();
builder.Services.AddSingleton(_ => new ClaudeCli());
builder.Services.AddSingleton<AgentDirectory>();
builder.Services.AddSingleton<WorktreeViews>();

// Git
builder.Services.AddSingleton<IGitCli>(_ => new GitCli());
builder.Services.AddSingleton<WorktreeLister>();
builder.Services.AddSingleton<StatusReader>();
builder.Services.AddSingleton<DiffReader>();
builder.Services.AddSingleton<Staging>();
builder.Services.AddSingleton<WorktreeFiles>();
builder.Services.AddSingleton<RepoDiscovery>();

// Code
// Singletons because the whole point of the Roslyn solution is that it stays
// warm: a scoped one would be loaded again for every circuit, and a load costs
// seconds and hundreds of megabytes.
builder.Services.AddSingleton<SolutionLoader>();
builder.Services.AddSingleton<CodeIntelligence>();

// Tests
builder.Services.AddSingleton<ITestProcessScanner, TestProcessScanner>();
builder.Services.AddSingleton<TestRunTracker>();
builder.Services.AddSingleton<TestRunner>();
builder.Services.AddSingleton<TelemetryCache>();

// Review
builder.Services.AddSingleton<FeedbackDispatcher>();

// The loop
builder.Services.AddSingleton<DashboardState>();
builder.Services.AddSingleton<INotifier, OsNotifier>();
builder.Services.AddSingleton<MonitorService>();
builder.Services.AddHostedService<MonitorHost>();

var app = builder.Build();

// MapStaticAssets rather than UseStaticFiles: it serves from the build-time
// asset manifest, so the framework's own files (blazor.web.js above all) are
// there whether the app is run from bin or from a publish output. UseStaticFiles
// only finds them when ASP.NET Core happens to be in the Development environment.
app.MapStaticAssets();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.StartAsync();

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
DesktopWindow.Open(url, app.Services.GetRequiredService<ILoggerFactory>(), fallbackUrlPrinted: () =>
    Console.WriteLine($"Agents Dashboard is running at {url}"));

await app.StopAsync();

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
