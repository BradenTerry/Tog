using AgentsDashboard.Core.Secrets;
using AgentsDashboard.Extensions;

namespace AgentsDashboard.App.Extensions;

/// <summary>
/// <see cref="ISecrets"/> for one loaded build of one extension. Made by the
/// host when it builds the extension's container, so the caller it names is
/// the host's word, not anything the extension said.
/// </summary>
internal sealed class ExtensionSecrets(SecretBroker broker, SecretCaller caller) : ISecrets
{
    public Task<string?> GetAsync(string name, string? purpose = null, CancellationToken ct = default) =>
        broker.ReadAsync(caller, name, Purpose(purpose), ct);

    public Task<HttpResponseMessage?> SendAsync(
        string name, HttpRequestMessage request, SecretAuth? auth = null, string? purpose = null, CancellationToken ct = default)
    {
        var how = auth ?? SecretAuth.Bearer;
        return broker.SendAsync(caller, name, request, new SecretPlacement(how.Header, how.Scheme, how.Base64), Purpose(purpose), ct);
    }

    /// <summary>The extension's words go in a prompt; keep them to a sentence or two.</summary>
    private static string? Purpose(string? purpose) =>
        string.IsNullOrWhiteSpace(purpose) ? null
        : purpose.Length > 300 ? purpose[..300].TrimEnd() + "..."
        : purpose.Trim();
}

/// <summary>
/// What <c>@inject ISecrets</c> gets. The app's container cannot tell which
/// extension a component belongs to, and a grant is per extension, so this
/// answers every call with where the real one is. Registered rather than left
/// out so the view shows the error in its boundary instead of failing to be
/// created.
/// </summary>
internal sealed class UnboundSecrets : ISecrets
{
    private const string Message =
        "ISecrets is per extension: take it with Context.Get<ISecrets>() in a view, or in a service's constructor, not @inject.";

    public Task<string?> GetAsync(string name, string? purpose = null, CancellationToken ct = default) =>
        throw new InvalidOperationException(Message);

    public Task<HttpResponseMessage?> SendAsync(
        string name, HttpRequestMessage request, SecretAuth? auth = null, string? purpose = null, CancellationToken ct = default) =>
        throw new InvalidOperationException(Message);
}
