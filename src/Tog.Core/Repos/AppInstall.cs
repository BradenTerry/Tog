namespace Tog.Core.Repos;

/// <summary>A build waiting to be started, as <c>tools/publish-local.sh</c> described it.</summary>
public sealed record StagedUpdate(string Id, string Commit, string Subject, DateTimeOffset? StagedAt, string? Version = null);

/// <summary>
/// Where an installed copy of the app lives: the <c>.app</c> bundle that is
/// opened, and the folder its builds are kept in.
/// </summary>
/// <remarks>
/// The builds live outside the bundle, in <c>~/Library/Application
/// Support/Tog/&lt;name&gt;</c>. macOS will not let an app that is
/// only signed ad hoc rewrite its own bundle once Finder has launched it
/// ("Operation not permitted"), so a bundle that swaps builds inside itself can
/// install from a terminal and never update itself. Outside it, each build has
/// its own folder under <c>builds</c>, and two one-line files name the build to
/// run (<c>current</c>) and the one waiting (<c>next</c>). The launcher, which
/// sets the variables read here, renames <c>next</c> over <c>current</c> before
/// the app starts. A running build's folder is never touched.
/// </remarks>
public sealed record AppInstall(string Bundle, string PayloadRoot)
{
    public const string BundleVariable = "TOG_BUNDLE";
    public const string PayloadVariable = "TOG_PAYLOAD";

    /// <summary>Written into every build folder by the publish script: commit, subject, staged, version.</summary>
    public const string InfoFile = "build-info";

    /// <summary>The installed copy this process runs as, or null when it was not started by a bundle's launcher.</summary>
    public static AppInstall? FromEnvironment(Func<string, string?> variable) =>
        variable(BundleVariable) is { Length: > 0 } bundle && variable(PayloadVariable) is { Length: > 0 } payload
            ? new AppInstall(bundle, payload)
            : null;

    /// <summary>
    /// The build waiting in <c>next</c>, or null when there is none or it is not
    /// complete yet. Only a plain name is followed, never a path, so the file
    /// cannot point outside <c>builds</c>.
    /// </summary>
    public StagedUpdate? ReadStaged()
    {
        try
        {
            var next = Path.Combine(PayloadRoot, "next");
            if (!File.Exists(next))
            {
                return null;
            }

            var id = File.ReadAllText(next).Trim();
            if (id.Length == 0 || id != Path.GetFileName(id) || id is "." or "..")
            {
                return null;
            }

            var info = Path.Combine(PayloadRoot, "builds", id, InfoFile);
            return File.Exists(info) ? Parse(id, File.ReadAllLines(info)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Mid-write; the next check sees how it ended.
            return null;
        }
    }

    /// <summary>The info file is <c>key=value</c> lines.</summary>
    public static StagedUpdate Parse(string id, IEnumerable<string> lines)
    {
        var values = lines
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.Ordinal);

        return new StagedUpdate(
            id,
            values.GetValueOrDefault("commit") ?? "unknown",
            values.GetValueOrDefault("subject") ?? "",
            DateTimeOffset.TryParse(values.GetValueOrDefault("staged"), out var at) ? at : null,
            values.GetValueOrDefault("version") is { Length: > 0 } version ? version : null);
    }
}
