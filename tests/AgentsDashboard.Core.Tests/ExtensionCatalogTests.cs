using AgentsDashboard.Core.Extensions;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class ExtensionCatalogTests
{
    private static string Manifest(string id, string api = "1.0", string? output = null) =>
        $$"""
        { "id": "{{id}}", "name": "{{id}} ext", "entry": "{{id}}.dll", "apiVersion": "{{api}}"{{(output is null ? "" : $", \"output\": \"{output}\"")}} }
        """;

    [Fact]
    public void A_good_manifest_parses()
    {
        var (manifest, error) = ExtensionManifests.Parse(Manifest("build-status"));

        Assert.Null(error);
        Assert.Equal("build-status", manifest!.Id);
        Assert.Equal("dotnet", manifest.Kind);
    }

    [Theory]
    [InlineData("Build_Status")]
    [InlineData("-leading")]
    [InlineData("has space")]
    public void An_id_that_would_not_be_safe_in_a_url_or_a_folder_is_refused(string id) =>
        Assert.NotNull(ExtensionManifests.Parse(Manifest(id)).Error);

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.1", false)]
    [InlineData("2.0", false)]
    [InlineData("0.9", false)]
    [InlineData("one", false)]
    public void Only_our_major_and_no_newer_minor_loads(string api, bool loads) =>
        Assert.Equal(loads, ExtensionManifests.Parse(Manifest("x", api)).Error is null);

    [Fact]
    public void Malformed_json_is_an_error_not_an_exception() =>
        Assert.NotNull(ExtensionManifests.Parse("{ not json").Error);

    [Fact]
    public void An_entry_that_is_a_path_is_refused() =>
        Assert.NotNull(ExtensionManifests.Parse("""{ "id": "x", "name": "x", "entry": "../x.dll" }""").Error);

    [Fact]
    public void The_entry_is_found_under_the_output_folder()
    {
        using var dir = new TempDir();
        dir.File("proj/extension.json", Manifest("demo", output: "bin/dashboard"));

        var found = ExtensionCatalog.Read(Path.Combine(dir.Path, "proj"), ExtensionSource.Linked);

        Assert.Equal(Path.Combine(dir.Path, "proj", "bin", "dashboard", "demo.dll"), found.EntryPath);
    }

    [Fact]
    public void Installed_linked_and_command_line_extensions_are_all_found()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Dir("data"));
        dir.File("data/extensions/one/extension.json", Manifest("one"));
        dir.File("linked/extension.json", Manifest("two"));
        dir.File("cli/extension.json", Manifest("three"));

        var found = ExtensionCatalog.Discover(
            paths,
            new Settings { LinkedExtensions = [Path.Combine(dir.Path, "linked")] },
            [Path.Combine(dir.Path, "cli")]);

        Assert.Equal(["one", "three", "two"], found.Select(f => f.Id).Order());
        Assert.Equal(ExtensionSource.Installed, found.Single(f => f.Id == "one").Source);
        Assert.False(found.Single(f => f.Id == "one").IsDev);
        Assert.True(found.Single(f => f.Id == "two").IsDev);
    }

    [Fact]
    public void A_linked_copy_wins_over_an_installed_one_with_the_same_id()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Dir("data"));
        dir.File("data/extensions/demo/extension.json", Manifest("demo"));
        dir.File("work/extension.json", Manifest("demo"));

        var found = ExtensionCatalog.Discover(
            paths,
            new Settings { LinkedExtensions = [Path.Combine(dir.Path, "work")] },
            []);

        var only = Assert.Single(found);
        Assert.Equal(ExtensionSource.Linked, only.Source);
    }

    [Fact]
    public void A_folder_with_no_manifest_is_listed_with_the_reason()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Dir("data"));
        dir.Dir("data/extensions/empty");

        var found = Assert.Single(ExtensionCatalog.Discover(paths, new Settings(), []));

        Assert.Null(found.Manifest);
        Assert.Contains("extension.json", found.Error);
    }

    [Fact]
    public void The_hash_changes_with_the_entry_assembly()
    {
        using var dir = new TempDir();
        dir.File("ext/extension.json", Manifest("demo"));
        dir.File("ext/demo.dll", "first");
        var found = ExtensionCatalog.Read(Path.Combine(dir.Path, "ext"), ExtensionSource.Installed);
        var before = ExtensionCatalog.Hash(found);

        dir.File("ext/demo.dll", "second");

        Assert.NotNull(before);
        Assert.NotEqual(before, ExtensionCatalog.Hash(found));
    }
}
