using Tog.Core.Claude;
using Tog.Core.Extensions;
using Tog.Core.Repos;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

public class ExtensionSkillTests
{
    private static string Api => $"{ExtensionManifests.Api.Major}.{ExtensionManifests.Api.Minor}";

    private static ExtensionSkill Make(TempDir dir, string? bundle = null)
    {
        bundle ??= dir.Dir("bundle");
        return new ExtensionSkill(
            new AppPaths(Path.Combine(dir.Path, "data")),
            new ClaudePaths(new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = Path.Combine(dir.Path, "claude") }),
            bundle);
    }

    private static TempDir WithSkill(string text = "api {{api}} sdk {{sdk}} template {{template}}")
    {
        var dir = new TempDir();
        dir.File("bundle/skill/SKILL.md", text);
        dir.Dir("claude");
        return dir;
    }

    [Fact]
    public void The_skill_names_this_machines_sdk_and_template_and_this_apps_api()
    {
        using var dir = WithSkill();
        var skill = Make(dir);

        Assert.Equal($"api {Api} sdk {skill.SdkDir} template {skill.TemplateDir}", skill.Text());
        Assert.Equal(Path.Combine(dir.Path, "data", "sdk", Api, "template"), skill.TemplateDir);
    }

    [Fact]
    public void The_shipped_skill_leaves_no_placeholder_unfilled()
    {
        // The bundle's skill/ is templates/skill/ in the repository.
        var templates = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "templates"));
        using var dir = new TempDir();
        var text = Make(dir, templates).Text();

        Assert.NotNull(text);
        Assert.DoesNotContain("{{", text);
        Assert.Contains($"name: {ExtensionSkill.Name}", text);
    }

    [Fact]
    public void A_build_without_the_skill_offers_nothing_to_write()
    {
        using var dir = new TempDir();
        dir.Dir("claude");
        var skill = Make(dir);
        var target = skill.Targets[0];

        Assert.Null(skill.Text());
        Assert.NotNull(skill.Install(target));
        Assert.Equal(SkillState.Missing, skill.State(target));
    }

    [Fact]
    public void Claude_is_only_offered_where_its_config_folder_exists()
    {
        using var dir = new TempDir();
        var skill = Make(dir);

        Assert.False(skill.Targets.Single().Available);
        dir.Dir("claude");
        Assert.True(skill.Targets.Single().Available);
        Assert.Equal(Path.Combine(dir.Path, "claude", "skills"), skill.Targets.Single().SkillsDir);
    }

    [Fact]
    public void Installed_is_current_until_it_differs_from_this_build()
    {
        using var dir = WithSkill();
        var skill = Make(dir);
        var target = skill.Targets[0];

        Assert.Equal(SkillState.Missing, skill.State(target));
        Assert.Null(skill.Install(target));
        Assert.Equal(Path.Combine(dir.Path, "claude", "skills", ExtensionSkill.Name, "SKILL.md"), skill.FileFor(target));
        Assert.Equal(SkillState.Current, skill.State(target));

        File.AppendAllText(skill.FileFor(target), "\nedited");
        Assert.Equal(SkillState.Outdated, skill.State(target));

        Assert.Null(skill.Install(target));
        Assert.Equal(SkillState.Current, skill.State(target));
    }

    [Fact]
    public void Removing_keeps_anything_else_the_user_put_in_the_folder()
    {
        using var dir = WithSkill();
        var skill = Make(dir);
        var target = skill.Targets[0];
        var folder = Path.GetDirectoryName(skill.FileFor(target))!;

        skill.Install(target);
        Assert.Null(skill.Remove(target));
        Assert.False(Directory.Exists(folder));

        skill.Install(target);
        File.WriteAllText(Path.Combine(folder, "notes.md"), "mine");
        Assert.Null(skill.Remove(target));
        Assert.Equal(SkillState.Missing, skill.State(target));
        Assert.True(File.Exists(Path.Combine(folder, "notes.md")));
    }

    [Fact]
    public void Publishing_the_template_replaces_the_old_one_hidden_files_and_all()
    {
        using var dir = new TempDir();
        dir.File("bundle/sdk-template/.template.config/template.json", "{}");
        dir.File("bundle/sdk-template/extension.json", "{}");
        var skill = Make(dir);
        Directory.CreateDirectory(skill.TemplateDir);
        File.WriteAllText(Path.Combine(skill.TemplateDir, "dropped.cs"), "old");

        skill.PublishTemplate();

        Assert.True(File.Exists(Path.Combine(skill.TemplateDir, ".template.config", "template.json")));
        Assert.True(File.Exists(Path.Combine(skill.TemplateDir, "extension.json")));
        Assert.False(File.Exists(Path.Combine(skill.TemplateDir, "dropped.cs")));
    }
}
