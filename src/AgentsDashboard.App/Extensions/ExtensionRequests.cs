using AgentsDashboard.Core.Extensions;

namespace AgentsDashboard.App.Extensions;

/// <summary>An agent asking for an extension it wrote to be turned on.</summary>
/// <param name="Folder">The extension's folder, absolute.</param>
/// <param name="Manifest">Its manifest as read when it asked.</param>
/// <param name="AgentFolder">Where the asking agent works, when known.</param>
/// <param name="Id">
/// Set when nobody asked: the extension appeared in one of the user's
/// extension folders, which is found already, and only needs turning on.
/// </param>
public sealed record ExtensionRequest(string Folder, ExtensionManifest Manifest, string? AgentFolder, string? Id = null)
{
    public bool InFolder => Id is not null;
}

/// <summary>
/// Extensions agents have asked to add, waiting for the user.
/// </summary>
/// <remarks>
/// An extension is code that runs inside the app as the user, so an agent can
/// only ask. The request is held here and every window shows it until the user
/// answers in one of them; only their Add links the folder. What is checked on
/// the way in is what would make Add fail anyway, so the agent hears about it
/// at once rather than the user finding out from a prompt that cannot work.
///
/// An extension that appears in one of the user's extension folders is asked
/// about here too, with no agent behind the request: agents can write into
/// those folders without calling the add tool. It is read from the host's
/// state rather than queued, so it is asked again after a restart until it
/// is answered, and a no is kept as a disable.
/// </remarks>
public sealed class ExtensionRequests
{
    private readonly ExtensionHost _extensions;
    private readonly Lock _gate = new();
    private List<ExtensionRequest> _pending = [];

    public ExtensionRequests(ExtensionHost extensions)
    {
        _extensions = extensions;
        extensions.Changed += () => Changed?.Invoke();
    }

    /// <summary>Raised when a request arrives or is answered, on whatever thread did it.</summary>
    public event Action? Changed;

    public IReadOnlyList<ExtensionRequest> Pending
    {
        get
        {
            List<ExtensionRequest> asked;
            lock (_gate)
            {
                asked = _pending;
            }

            // An agent that wrote into a folder and also asked is shown once, as its request.
            var appeared = _extensions.Entries
                .Where(e => e is { Status: ExtensionStatus.NeedsConsent, Found.Source: ExtensionSource.InFolder, Found.Manifest: not null })
                .Where(e => asked.All(r => r.Folder != e.Found.Directory))
                .Select(e => new ExtensionRequest(e.Found.Directory, e.Found.Manifest!, null, e.Found.Id));
            return [.. asked, .. appeared];
        }
    }

    /// <summary>Queues a request. Returns why it cannot be made, or null.</summary>
    public string? Ask(string folder, string? agentFolder)
    {
        var full = Path.GetFullPath(folder);
        var found = ExtensionCatalog.Read(full, ExtensionSource.Linked);
        if (found.Manifest is not { } manifest)
        {
            return found.Error;
        }

        if (found.EntryPath is not { } entry || !File.Exists(entry))
        {
            return $"{manifest.Entry} is not built yet: expected at {found.EntryPath}. Build the project first.";
        }

        if (_extensions.Entries.FirstOrDefault(e => e.Found.Id == manifest.Id) is { Status: ExtensionStatus.Loaded } loaded)
        {
            return loaded.Found.Directory == full
                ? $"{manifest.Name} is already on. Every build reloads it."
                : $"An extension with the id {manifest.Id} is already on, from {loaded.Found.Directory}. Change the id in extension.json.";
        }

        lock (_gate)
        {
            // Asking again replaces the earlier request, which may have read an older manifest.
            _pending = [.. _pending.Where(r => r.Folder != full), new ExtensionRequest(full, manifest, agentFolder)];
        }

        Changed?.Invoke();
        return null;
    }

    /// <summary>
    /// The user's yes: links the folder, which turns it on. Returns why it
    /// failed, or null. A failed request stays, so the prompt can say why.
    /// </summary>
    public string? Accept(ExtensionRequest request)
    {
        if (request.Id is { } id)
        {
            _extensions.Enable(id);
            return null;
        }

        if (_extensions.Link(request.Folder) is { } problem)
        {
            return problem;
        }

        Drop(request);
        return null;
    }

    /// <summary>
    /// The user's no. One that appeared in a folder is turned off, so it stays
    /// off and is not asked about again; Settings can still turn it on.
    /// </summary>
    public void Dismiss(ExtensionRequest request)
    {
        if (request.Id is { } id)
        {
            _extensions.Disable(id);
            return;
        }

        Drop(request);
    }

    private void Drop(ExtensionRequest request)
    {
        lock (_gate)
        {
            _pending = [.. _pending.Where(r => r != request)];
        }

        Changed?.Invoke();
    }
}
