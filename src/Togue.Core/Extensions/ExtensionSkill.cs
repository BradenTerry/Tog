using Togue.Core.Claude;
using Togue.Core.Repos;

namespace Togue.Core.Extensions;

/// <summary>An agent's skills folder, where the extension skill can be added.</summary>
/// <param name="AgentName">The agent as Settings names it.</param>
/// <param name="SkillsDir">The agent's user-wide skills folder.</param>
/// <param name="Available">Whether the agent looks installed: its config folder exists.</param>
public sealed record SkillTarget(string AgentName, string SkillsDir, bool Available);

public enum SkillState { Missing, Current, Outdated }

/// <summary>
/// The skill that teaches an agent to write a Togue extension, and the
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
/// Writing the skill into an agent's config folder is the one write the app
/// makes there, and only when the user presses the button for it. Agents the
/// app runs get the same text from its MCP server without it.
/// </para>
/// </remarks>
public sealed class ExtensionSkill
{
    /// <summary>The skill's folder name, and the name in its front matter.</summary>
    public const string Name = "togue-extension";

    private readonly AppPaths _paths;
    private readonly ClaudePaths _claude;
    private readonly string _bundle;

    /// <param name="bundle">The folder the app runs from, holding <c>skill/</c> and <c>sdk-template/</c>.</param>
    public ExtensionSkill(AppPaths paths, ClaudePaths claude, string? bundle = null)
    {
        _paths = paths;
        _claude = claude;
        _bundle = bundle ?? AppContext.BaseDirectory;
    }

    /// <summary>The SDK folder for the API this app provides.</summary>
    public string SdkDir => Path.Combine(_paths.SdkDir, $"{ExtensionManifests.Api.Major}.{ExtensionManifests.Api.Minor}");

    /// <summary>Where the template is published for <c>dotnet new install</c>.</summary>
    public string TemplateDir => Path.Combine(SdkDir, "template");

    /// <summary>
    /// Every agent the skill can be added for. Only Claude Code today; another
    /// agent with a skills folder is another entry.
    /// </summary>
    public IReadOnlyList<SkillTarget> Targets =>
        [new("Claude Code", Path.Combine(_claude.Root, "skills"), Directory.Exists(_claude.Root))];

    /// <summary>The skill as it would be written on this machine, or null when this build does not carry it.</summary>
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

    public string FileFor(SkillTarget target) => Path.Combine(target.SkillsDir, Name, "SKILL.md");

    /// <summary>
    /// Whether the skill there is this build's. Any difference counts as
    /// outdated, including an edit of the user's: the paths and the API version
    /// in it are what go stale, and an update puts them right.
    /// </summary>
    public SkillState State(SkillTarget target)
    {
        var file = FileFor(target);
        if (!File.Exists(file))
        {
            return SkillState.Missing;
        }

        try
        {
            return File.ReadAllText(file) == Text() ? SkillState.Current : SkillState.Outdated;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return SkillState.Outdated;
        }
    }

    /// <summary>Writes the skill for an agent. Returns why not, or null.</summary>
    public string? Install(SkillTarget target)
    {
        if (Text() is not { } text)
        {
            return "This build of the app does not include the skill.";
        }

        try
        {
            var file = FileFor(target);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Removes the skill file, and its folder if that leaves it empty: anything
    /// else the user put there is theirs.
    /// </summary>
    public string? Remove(SkillTarget target)
    {
        try
        {
            var file = FileFor(target);
            File.Delete(file);
            var folder = Path.GetDirectoryName(file)!;
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
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
