using System.Security.Cryptography;
using System.Text;

namespace AgentsDashboard.App.Services;

/// <summary>
/// Keeps the dashboard's pages for its own window. A key is made at every
/// start and put only in the address the window opens (or, with
/// <c>--browser</c>, the one printed for you); the first request swaps it for
/// a cookie, and every other request without that cookie is refused.
/// </summary>
/// <remarks>
/// <para>
/// The app listens on loopback, and its port is no secret: it is in
/// <c>mcp-link.json</c>, which any program running as you can read, agents
/// included. Without this, an agent could open the dashboard in a headless
/// browser and press what you would press: Allow on a secret prompt, Add on
/// a secret in Settings, Add and turn on for the extension it just wrote.
/// </para>
/// <para>
/// <c>/_mcp</c> is left to its own keys, one per agent session, since agents
/// are meant to call it. A program running as you can still dig the cookie out
/// of the web view's storage on disk; this stops an agent driving the app the
/// ordinary way, it does not stop code that sets out to get round it.
/// </para>
/// </remarks>
public sealed class UiAccess
{
    public const string QueryName = "ui-key";
    private const string CookieName = "agents-dashboard-ui";

    private readonly byte[] _key = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));

    /// <summary>The address to open: <paramref name="baseUrl"/> with this start's key.</summary>
    public string EntryUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/?{QueryName}={Encoding.ASCII.GetString(_key)}";

    private bool Matches(string? value) =>
        value is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(value), _key);

    public void Map(WebApplication app) => app.Use(async (http, next) =>
    {
        if (http.Request.Path.StartsWithSegments("/_mcp"))
        {
            await next(http);
            return;
        }

        if (Matches(http.Request.Query[QueryName]))
        {
            http.Response.Cookies.Append(CookieName, Encoding.ASCII.GetString(_key), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true,
            });

            // Off the address at once, so it is not in the page's history or
            // anything the page hands on.
            var rest = http.Request.Query.Where(q => q.Key != QueryName)
                .Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}");
            var query = string.Join('&', rest);
            http.Response.Redirect(http.Request.PathBase + http.Request.Path + (query.Length > 0 ? "?" + query : ""));
            return;
        }

        if (Matches(http.Request.Cookies[CookieName]))
        {
            await next(http);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        http.Response.ContentType = "text/plain; charset=utf-8";
        await http.Response.WriteAsync(
            "This is the Agents Dashboard. Open it from its own window, or with the address it printed when it started.");
    });
}
