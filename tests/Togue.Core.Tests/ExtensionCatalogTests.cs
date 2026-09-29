using Togue.Core.Extensions;
using Togue.Core.Repos;
using Togue.Core.Tests.Support;

namespace Togue.Core.Tests;

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
    [InlineData("1.1", true)]
    [InlineData("1.2", true)]
    [InlineData("1.3", true)]
    [InlineData("1.4", true)]
    [InlineData("1.5", true)]
    [InlineData("1.6", true)]
    [InlineData("1.7", true)]
    [InlineData("1.8", true)]
    [InlineData("1.9", true)]
    [InlineData("1.10", true)]
    [InlineData("1.11", true)]
    [InlineData("1.12", true)]
    [InlineData("1.13", false)]
    [InlineData("2.0", false)]
    [InlineData("0.9", false)]
    [InlineData("one", false)]
    public void Only_our_major_and_no_newer_minor_loads(string api, bool loads) =>
        Assert.Equal(loads, ExtensionManifests.Parse(Manifest("x", api)).Error is null);

    [Fact]
    public void Declared_secrets_are_read_with_their_hosts_written_one_way()
    {
        var (manifest, error) = ExtensionManifests.Parse("""
            { "id": "x", "name": "x", "entry": "x.dll", "apiVersion": "1.12",
              "secrets": [
                { "name": "github", "purpose": " Lists your pull requests. ", "hosts": ["API.GitHub.com", "api.github.com:443"] },
                { "name": "linear", "read": true } ] }
            """);

        Assert.Null(error);
        var github = manifest!.Secrets[0];
        Assert.Equal(["api.github.com"], github.Hosts);
        Assert.Equal("Lists your pull requests.", github.Purpose);
        Assert.False(github.Read);
        Assert.True(manifest.Secrets[1].Read);
    }

    [Theory]
    [InlineData("""{ "name": "GitHub", "hosts": ["api.github.com"] }""")]
    [InlineData("""{ "name": "github", "hosts": ["https://api.github.com/user"] }""")]
    [InlineData("""{ "name": "github", "hosts": ["*.github.com"] }""")]
    [InlineData("""{ "name": "github" }""")]
    [InlineData("""{ "hosts": ["api.github.com"] }""")]
    public void A_secret_that_could_not_be_used_as_declared_stops_the_manifest(string need) =>
        Assert.NotNull(ExtensionManifests.Parse(
            $$"""{ "id": "x", "name": "x", "entry": "x.dll", "secrets": [ {{need}} ] }""").Error);

    [Fact]
    public void A_secret_declared_twice_stops_the_manifest() =>
        Assert.NotNull(ExtensionManifests.Parse("""
            { "id": "x", "name": "x", "entry": "x.dll",
              "secrets": [ { "name": "github", "read": true }, { "name": "github", "read": true } ] }
            """).Error);

    [Fact]
    public void A_manifest_with_no_secrets_declares_none() =>
        Assert.Empty(ExtensionManifests.Parse(Manifest("x")).Manifest!.Secrets);

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
        dir.File("proj/extension.json", Manifest("demo", output: "bin/togue"));

        var found = ExtensionCatalog.Read(Path.Combine(dir.Path, "proj"), ExtensionSource.Linked);

        Assert.Equal(Path.Combine(dir.Path, "proj", "bin", "togue", "demo.dll"), found.EntryPath);
    }

    [Theory]
    [InlineData("/work/ext", "/work/ext", false)]
    [InlineData("  /work/ext  ", "/work/ext", false)]
    [InlineData("/work/exts/*", "/work/exts", true)]
    [InlineData(@"C:\work\exts\*", @"C:\work\exts", true)]
    [InlineData("/work/exts*", "/work/exts*", false)]
    [InlineData("*", "", true)]
    public void A_trailing_star_means_every_extension_in_the_folder(string typed, string folder, bool everything)
    {
        Assert.Equal((folder, everything), ExtensionCatalog.SplitWildcard(typed));
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
    public void Every_extension_directly_inside_an_extension_folder_is_found()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Dir("data"));
        dir.File("mine/one/extension.json", Manifest("one"));
        dir.File("mine/two/extension.json", Manifest("two"));
        dir.File("mine/notes/readme.md", "not an extension");
        dir.File("mine/deep/nested/extension.json", Manifest("deep"));

        var found = ExtensionCatalog.Discover(
            paths,
            new Settings { ExtensionFolders = [Path.Combine(dir.Path, "mine")] },
            []);

        Assert.Equal(["one", "two"], found.Select(f => f.Id));
        Assert.All(found, f => Assert.Equal(ExtensionSource.InFolder, f.Source));
        Assert.All(found, f => Assert.True(f.IsDev));
    }

    [Fact]
    public void A_linked_copy_wins_over_one_in_an_extension_folder()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Dir("data"));
        dir.File("data/extensions/demo/extension.json", Manifest("demo"));
        dir.File("mine/demo/extension.json", Manifest("demo"));
        dir.File("work/extension.json", Manifest("demo"));

        var inFolder = Assert.Single(ExtensionCatalog.Discover(
            paths,
            new Settings { ExtensionFolders = [Path.Combine(dir.Path, "mine")] },
            []));
        var linked = Assert.Single(ExtensionCatalog.Discover(
            paths,
            new Settings { ExtensionFolders = [Path.Combine(dir.Path, "mine")], LinkedExtensions = [Path.Combine(dir.Path, "work")] },
            []));

        Assert.Equal(ExtensionSource.InFolder, inFolder.Source);
        Assert.Equal(ExtensionSource.Linked, linked.Source);
    }

    [Fact]
    public void An_extension_folder_that_is_gone_finds_nothing()
    {
        using var dir = new TempDir();

        Assert.Empty(ExtensionCatalog.Discover(
            new AppPaths(dir.Dir("data")),
            new Settings { ExtensionFolders = [Path.Combine(dir.Path, "missing")] },
            []));
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
