using Tog.Core.Secrets;
using Tog.Extensions;

namespace Tog.App.Extensions;

/// <summary>
/// <see cref="ISecrets"/> for one loaded build of one extension. Made by the
/// host when it builds the extension's container, so the caller it names, and
/// the needs its manifest declared, are the host's word, not anything the
/// extension said at run time.
/// </summary>
/// <remarks>
/// The <c>purpose</c> an extension passes is not shown any more: since API
/// 1.12 the prompt shows the purpose from <c>extension.json</c>, which you can
/// read before its code ever runs.
/// </remarks>
internal sealed class ExtensionSecrets(SecretBroker broker, SecretCaller caller) : ISecrets
{
    public Task<string?> GetAsync(string name, string? purpose = null, CancellationToken ct = default) =>
        broker.ReadAsync(caller, name, ct);

    public Task<HttpResponseMessage?> SendAsync(
        string name, HttpRequestMessage request, SecretAuth? auth = null, string? purpose = null, CancellationToken ct = default)
    {
        var how = auth ?? SecretAuth.Bearer;
        return broker.SendAsync(caller, name, request, new SecretPlacement(how.Header, how.Scheme, how.Base64), ct);
    }
}

/// <summary>
/// What <c>@inject ISecrets</c> gets. The app's container cannot tell which
/// extension a component belongs to, and a binding is per extension, so this
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
