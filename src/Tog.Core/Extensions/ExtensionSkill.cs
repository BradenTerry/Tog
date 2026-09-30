using Tog.Core.Repos;

namespace Tog.Core.Extensions;

/// <summary>
/// The skill that teaches an agent to write a Tog extension, and the
/// template it scaffolds from, as this build of the app ships them.
/// </summary>
/// <remarks>
/// <para>
/// The skill is only useful outside this repository, where neither the
/// template nor the docs are, so the app carries both. The template is copied
/// next to the SDK every start, like the API assembly, and the skill names
/// both by their absolute paths on this machine, so an agent in any folder can
/// follow it without looking anything up.
/// </para>
/// <para>
/// The app never writes it anywhere itself. Settings shows it with a button to
/// copy it, and the user gives it to whichever agent they use, as a skill, a
/// rules file or a message: every agent keeps these somewhere different, and
/// the app only reads their config. Agents the app runs get the same text from
/// its MCP server.
/// </para>
/// </remarks>
public sealed class ExtensionSkill
{
    /// <summary>The skill's folder name, and the name in its front matter.</summary>
    public const string Name = "tog-extension";

    private readonly AppPaths _paths;
    private readonly string _bundle;

    /// <param name="bundle">The folder the app runs from, holding <c>skill/</c> and <c>sdk-template/</c>.</param>
    public ExtensionSkill(AppPaths paths, string? bundle = null)
    {
        _paths = paths;
        _bundle = bundle ?? AppContext.BaseDirectory;
    }

    /// <summary>The SDK folder for the API this app provides.</summary>
    public string SdkDir => Path.Combine(_paths.SdkDir, $"{ExtensionManifests.Api.Major}.{ExtensionManifests.Api.Minor}");

    /// <summary>Where the template is published for <c>dotnet new install</c>.</summary>
    public string TemplateDir => Path.Combine(SdkDir, "template");

    /// <summary>The skill with this machine's paths filled in, or null when this build does not carry it.</summary>
    public string? Text()
    {
        var source = Path.Combine(_bundle, "skill", "SKILL.md");
        if (!File.Exists(source))
        {
            return null;
        }

        return File.ReadAllText(source)
            .Replace("{{api}}", $"{ExtensionManifests.Api.Major}.{ExtensionManifests.Api.Minor}", StringComparison.Ordinal)
            .Replace("{{sdk}}", SdkDir, StringComparison.Ordinal)
            .Replace("{{template}}", TemplateDir, StringComparison.Ordinal);
    }

    /// <summary>
    /// Replaces the published template with this build's. It is replaced
    /// rather than copied over so a file the template dropped does not linger
    /// in every project made from it.
    /// </summary>
    public void PublishTemplate()
    {
        var source = Path.Combine(_bundle, "sdk-template");
        if (!Directory.Exists(source))
        {
            return;
        }

        if (Directory.Exists(TemplateDir))
        {
            Directory.Delete(TemplateDir, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(TemplateDir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }
    }
}
