using System.Text.Json;
using System.Text.RegularExpressions;
using Tog.Core.Secrets;

namespace Tog.Core.Extensions;

/// <summary>An extension's <c>extension.json</c>.</summary>
/// <remarks>
/// Read before any of the extension's code is loaded, so Settings can say what
/// an extension is before the user enables it.
/// </remarks>
public sealed record ExtensionManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "0.0.0";
    public string ApiVersion { get; init; } = "1.0";

    /// <summary>The entry assembly's file name.</summary>
    public required string Entry { get; init; }

    /// <summary>
    /// Where the entry assembly is, relative to the manifest. A linked project
    /// folder points this at its build output; an installed copy leaves it empty.
    /// </summary>
    public string? Output { get; init; }

    public string? Description { get; init; }
    public string? Author { get; init; }

    /// <summary>Only <c>dotnet</c> today. Reserved so other kinds need no format change.</summary>
    public string Kind { get; init; } = "dotnet";

    /// <summary>
    /// The secrets it needs, by its own names for them, and where each is sent.
    /// The only ones it can ask for. Since API 1.12; see <see cref="SecretNeed"/>.
    /// </summary>
    public IReadOnlyList<SecretNeed> Secrets { get; init; } = [];
}

/// <summary>Reads and checks manifests.</summary>
public static partial class ExtensionManifests
{
    public const string FileName = "extension.json";

    /// <summary>The API version this app provides.</summary>
    public static readonly Version Api = new(1, 12);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    /// <summary>
    /// Parses a manifest, or says why it cannot be used. An extension whose
    /// manifest fails here is listed in Settings with the reason and never loaded.
    /// </summary>
    public static (ExtensionManifest? Manifest, string? Error) Parse(string json)
    {
        ExtensionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ExtensionManifest>(json, Options);
        }
        catch (JsonException e)
        {
            return (null, $"extension.json is not valid: {e.Message}");
        }

        if (manifest is null)
        {
            return (null, "extension.json is empty.");
        }

        if (!IdPattern().IsMatch(manifest.Id))
        {
            return (null, $"The id \"{manifest.Id}\" must be lowercase letters, digits and dashes.");
        }

        if (manifest.Kind != "dotnet")
        {
            return (null, $"Kind \"{manifest.Kind}\" is not supported by this version of the app.");
        }

        if (manifest.Entry.Contains('/') || manifest.Entry.Contains('\\'))
        {
            return (null, "The entry is a file name, not a path. Point output at its folder.");
        }

        if (ApiError(manifest.ApiVersion) is { } error)
        {
            return (null, error);
        }

        var (needs, needsError) = SecretNeed.Check(manifest.Secrets);
        return needsError is null ? (manifest with { Secrets = needs }, null) : (null, needsError);
    }

    /// <summary>
    /// A minor version only adds and a major version may break, so an extension
    /// loads when its major is ours and its minor is not newer than ours.
    /// </summary>
    public static string? ApiError(string apiVersion)
    {
        if (!Version.TryParse(apiVersion, out var wanted))
        {
            return $"apiVersion \"{apiVersion}\" is not a version.";
        }

        if (wanted.Major != Api.Major || wanted.Minor > Api.Minor)
        {
            return $"Built for API {wanted.Major}.{wanted.Minor}; this app provides {Api.Major}.{Api.Minor}.";
        }

        return null;
    }
}
