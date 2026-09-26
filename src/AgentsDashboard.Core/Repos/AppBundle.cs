namespace AgentsDashboard.Core.Repos;

/// <summary>Where a macOS app bundle keeps a build staged beside the running one.</summary>
/// <param name="Bundle">The <c>.app</c> folder, which is what gets opened again.</param>
/// <param name="Marker">The file whose presence means a complete build is waiting.</param>
public sealed record AppBundle(string Bundle, string Marker)
{
    /// <summary>
    /// The bundle the app runs from, given its own folder, or null when it is not
    /// running from one (a build output, <c>dotnet run</c>).
    /// </summary>
    /// <remarks>
    /// <c>tools/publish-local.sh</c> installs the app to
    /// <c>Name.app/Contents/Resources/app</c> and stages the next build beside it
    /// in <c>Resources/app.next</c>.
    /// </remarks>
    public static AppBundle? Locate(string baseDirectory)
    {
        var app = new DirectoryInfo(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, '/'));
        return app is { Name: "app", Parent: { Name: "Resources", Parent: { Name: "Contents", Parent: { } bundle } } resources }
               && bundle.Name.EndsWith(".app", StringComparison.Ordinal)
            ? new AppBundle(bundle.FullName, Path.Combine(resources.FullName, "app.next", MarkerFile))
            : null;
    }

    /// <summary>Moved into place with the staged build, so its presence means the copy is complete.</summary>
    public const string MarkerFile = ".staged";
}
