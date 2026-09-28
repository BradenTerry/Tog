namespace AgentsDashboard.Extensions;

/// <summary>
/// Secrets the user keeps in the dashboard, such as a GitHub, Jira or Linear
/// token, which an extension asks for by name. Since API 1.8.
/// </summary>
/// <remarks>
/// <para>
/// Only in the extension's own container: take it in a service's constructor
/// or, in a view, through <c>Context.Get&lt;ISecrets&gt;()</c>. It knows which
/// extension is asking from where it was handed out, so <c>@inject</c>, which
/// resolves from the app's container, gets one that refuses every call.
/// </para>
/// <para>
/// The first time a build of the extension asks for a secret, the user is
/// shown who is asking, for what, and why, and the call waits until they
/// answer. Their answer is kept with the extension's code hash, so the next
/// call returns at once, and a rebuilt or updated extension is asked about
/// again. A secret the user has not added yet can be entered in the prompt.
/// </para>
/// <para>
/// Prefer <see cref="SendAsync"/>: the app makes the request and adds the
/// secret, so the extension never holds it, and the user approves it for the
/// one host. Use <see cref="GetAsync"/> only for what a request cannot cover,
/// such as a client library that wants the token itself.
/// </para>
/// <para>
/// Never pass a secret to an agent: not in a tool's result, not in a prompt
/// offered with <see cref="IAgentOffers"/>, not in a file in a worktree.
/// </para>
/// </remarks>
public interface ISecrets
{
    /// <summary>
    /// The secret's value, once the user has let this extension read it. Null
    /// when they said no, or the secret does not exist and they did not add it.
    /// Waits for the user, however long that takes: pass a token that is
    /// cancelled when the result stops mattering, such as a worker's
    /// <c>stopping</c>.
    /// </summary>
    /// <param name="name">Lower case letters, digits, <c>.</c>, <c>-</c> and <c>_</c>, such as <c>github</c> or <c>linear</c>.</param>
    /// <param name="purpose">A sentence for the prompt: what the extension will do with it.</param>
    /// <param name="ct">Stops waiting. The prompt stays until the user answers it.</param>
    Task<string?> GetAsync(string name, string? purpose = null, CancellationToken ct = default);

    /// <summary>
    /// Sends <paramref name="request"/> with the secret added, once the user has
    /// let this extension use it with the request's host, and returns the
    /// response. Null when they said no. The request must be <c>https</c>.
    /// Redirects are not followed, since the secret would go with them: a 3xx
    /// comes back as it is.
    /// </summary>
    /// <param name="name">The secret's name.</param>
    /// <param name="request">The request. A header of the same name as <paramref name="auth"/>'s is replaced.</param>
    /// <param name="auth">Where the secret goes. <see cref="SecretAuth.Bearer"/> when null.</param>
    /// <param name="purpose">A sentence for the prompt: what the extension will do with it.</param>
    /// <param name="ct">Stops waiting, or the request.</param>
    Task<HttpResponseMessage?> SendAsync(
        string name,
        HttpRequestMessage request,
        SecretAuth? auth = null,
        string? purpose = null,
        CancellationToken ct = default);
}

/// <summary>Where <see cref="ISecrets.SendAsync"/> puts the secret. Since API 1.8.</summary>
/// <param name="Header">The header it goes in.</param>
/// <param name="Scheme">Written before it with a space, as in <c>Bearer</c>, or null for the value alone.</param>
/// <param name="Base64">Base64-encode the value first, as Basic authentication wants for <c>user:token</c>.</param>
public sealed record SecretAuth(string Header, string? Scheme = null, bool Base64 = false)
{
    /// <summary><c>Authorization: Bearer &lt;value&gt;</c>. GitHub, and most token APIs.</summary>
    public static SecretAuth Bearer { get; } = new("Authorization", "Bearer");

    /// <summary><c>Authorization: Basic base64(&lt;value&gt;)</c>, with the secret stored as <c>user:token</c>. Jira Cloud.</summary>
    public static SecretAuth Basic { get; } = new("Authorization", "Basic", Base64: true);

    /// <summary><c>Authorization: &lt;value&gt;</c>. Linear's personal keys.</summary>
    public static SecretAuth Plain { get; } = new("Authorization");
}
