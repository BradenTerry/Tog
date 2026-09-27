using AgentsDashboard.Core.Presentation;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void A_fresh_install_leaves_the_start_defaults_to_the_cli()
    {
        var settings = new Settings();

        Assert.Null(settings.DefaultModel);
        Assert.Null(settings.DefaultEffort);
        Assert.Null(settings.DefaultPermissionMode);
    }

    [Fact]
    public void Round_trips_the_start_defaults()
    {
        using var temp = new TempDir();
        var store = new SettingsStore(new AppPaths(temp.Path));

        store.Save(new Settings
        {
            RepoRoots = ["/repo"],
            DefaultModel = "opus",
            DefaultEffort = "xhigh",
            DefaultPermissionMode = "acceptEdits",
        });

        var loaded = store.Load();

        Assert.Equal(["/repo"], loaded.RepoRoots);
        Assert.Equal("opus", loaded.DefaultModel);
        Assert.Equal("xhigh", loaded.DefaultEffort);
        Assert.Equal("acceptEdits", loaded.DefaultPermissionMode);
    }

    [Fact]
    public void A_file_written_before_these_settings_existed_loads_with_the_defaults()
    {
        using var temp = new TempDir();
        temp.File("settings.json", """
            {
              "RepoRoots": [ "/repo" ],
              "HiddenRoots": [],
              "GitPollSeconds": 10,
              "NotifyOnWaiting": true
            }
            """);

        var loaded = new SettingsStore(new AppPaths(temp.Path)).Load();

        Assert.Equal(["/repo"], loaded.RepoRoots);
        Assert.Null(loaded.DefaultModel);
        Assert.Null(loaded.DefaultEffort);
        Assert.Null(loaded.DefaultPermissionMode);
    }

    [Fact]
    public void Round_trips_the_theme()
    {
        using var temp = new TempDir();
        var store = new SettingsStore(new AppPaths(temp.Path));

        store.Save(new Settings { Theme = "nord" });

        Assert.Equal("nord", store.Load().Theme);
    }

    [Theory]
    [InlineData(null, Themes.System)]
    [InlineData("", Themes.System)]
    [InlineData("gone", Themes.System)]
    [InlineData("light", "light")]
    [InlineData("solarized-light", "solarized-light")]
    public void An_unknown_theme_falls_back_to_system(string? stored, string expected) =>
        Assert.Equal(expected, Themes.Resolve(stored));
}
