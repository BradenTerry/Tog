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
        return new ExtensionSkill(new AppPaths(Path.Combine(dir.Path, "data")), bundle);
    }

    private static TempDir WithSkill(string text = "api {{api}} sdk {{sdk}} template {{template}}")
    {
        var dir = new TempDir();
        dir.File("bundle/skill/SKILL.md", text);
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
    public void A_build_without_the_skill_has_nothing_to_copy()
    {
        using var dir = new TempDir();

        Assert.Null(Make(dir).Text());
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
