using System.Security.Cryptography;
using AgentsDashboard.Core.Repos;

namespace AgentsDashboard.Core.Extensions;

/// <summary>Where an extension was found.</summary>
public enum ExtensionSource
{
    /// <summary>A copy in the data folder's <c>extensions</c> directory.</summary>
    Installed,

    /// <summary>
    /// A folder directly inside one of the user's extension folders. Declared
    /// between the two so a folder the user linked by hand wins over it.
    /// </summary>
    InFolder,

    /// <summary>A folder the user linked in Settings, usually a project being written.</summary>
    Linked,

    /// <summary>Passed with <c>--extension</c> for this run only.</summary>
    CommandLine,
}

/// <summary>An extension on disk, found but not loaded.</summary>
public sealed record FoundExtension
{
    public required string Directory { get; init; }
    public required ExtensionSource Source { get; init; }
    public ExtensionManifest? Manifest { get; init; }
    public string? Error { get; init; }

    /// <summary>The id, or the folder name when the manifest could not be read.</summary>
    public string Id => Manifest?.Id ?? Path.GetFileName(Directory.TrimEnd('/', '\\'));

    /// <summary>The folder the entry assembly and its dependencies are in.</summary>
    public string? OutputDirectory => Manifest is { } m
        ? Path.GetFullPath(Path.Combine(Directory, m.Output ?? ""))
        : null;

    public string? EntryPath => Manifest is { } m && OutputDirectory is { } output
        ? Path.Combine(output, m.Entry)
        : null;

    /// <summary>
    /// A folder you linked or passed yourself is code you are writing: it is not
    /// asked about again on every build.
    /// </summary>
    public bool IsDev => Source != ExtensionSource.Installed;
}

/// <summary>
/// Finds extensions: installed copies, linked folders, and command-line paths.
/// Reads manifests only; no extension code runs here.
/// </summary>
public static class ExtensionCatalog
{
    /// <summary>
    /// Every extension found. Where an id is in more than one place the command
    /// line wins over a linked folder, which wins over an installed copy: the
    /// nearer one is the one being worked on.
    /// </summary>
    public static IReadOnlyList<FoundExtension> Discover(
        AppPaths paths,
        Settings settings,
        IReadOnlyList<string> commandLine)
    {
        var found = new List<FoundExtension>();

        if (System.IO.Directory.Exists(paths.ExtensionsDir))
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories(paths.ExtensionsDir).Order(StringComparer.Ordinal))
            {
                found.Add(Read(dir, ExtensionSource.Installed));
            }
        }

        found.AddRange(settings.ExtensionFolders.SelectMany(InFolder).Select(dir => Read(dir, ExtensionSource.InFolder)));
        found.AddRange(settings.LinkedExtensions.Select(dir => Read(dir, ExtensionSource.Linked)));
        found.AddRange(commandLine.Select(dir => Read(Path.GetFullPath(dir), ExtensionSource.CommandLine)));

        return found
            .GroupBy(f => f.Id, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(f => f.Source).First())
            .OrderBy(f => f.Manifest?.Name ?? f.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The folders directly inside an extension folder that have a manifest.
    /// Anything else in there is not an extension and is not listed: the folder
    /// may well hold other projects too.
    /// </summary>
    public static IEnumerable<string> InFolder(string folder)
    {
        if (!System.IO.Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            return System.IO.Directory.EnumerateDirectories(folder)
                .Where(dir => File.Exists(Path.Combine(dir, ExtensionManifests.FileName)))
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static FoundExtension Read(string directory, ExtensionSource source)
    {
        var file = Path.Combine(directory, ExtensionManifests.FileName);
        if (!File.Exists(file))
        {
            return new FoundExtension
            {
                Directory = directory,
                Source = source,
                Error = $"No {ExtensionManifests.FileName} in {directory}.",
            };
        }

        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new FoundExtension { Directory = directory, Source = source, Error = e.Message };
        }

        var (manifest, error) = ExtensionManifests.Parse(json);
        return new FoundExtension { Directory = directory, Source = source, Manifest = manifest, Error = error };
    }

    /// <summary>
    /// The SHA-256 of the entry assembly, which is what the user trusted when they
    /// enabled an installed extension. Null when it is not there.
    /// </summary>
    public static string? Hash(FoundExtension extension)
    {
        if (extension.EntryPath is not { } entry || !File.Exists(entry))
        {
            return null;
        }

        using var stream = File.OpenRead(entry);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
