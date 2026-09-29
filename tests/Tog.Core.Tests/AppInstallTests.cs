using Tog.Core.Repos;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

public class AppInstallTests
{
    [Fact]
    public void Comes_from_the_variables_the_launcher_sets()
    {
        var vars = new Dictionary<string, string>
        {
            [AppInstall.BundleVariable] = "/Users/me/Desktop/Tog.app",
            [AppInstall.PayloadVariable] = "/Users/me/Library/Application Support/Tog/Tog",
        };

        var install = AppInstall.FromEnvironment(vars.GetValueOrDefault);

        Assert.Equal("/Users/me/Desktop/Tog.app", install?.Bundle);
    }

    [Fact]
    public void Run_any_other_way_there_is_none() =>
        Assert.Null(AppInstall.FromEnvironment(_ => null));

    [Fact]
    public void A_staged_build_is_read_from_next_and_its_info_file()
    {
        using var dir = new TempDir();
        dir.File("builds/20260926-1450-abc1234/build-info", "commit=abc1234\nsubject=Fix a thing\nstaged=2026-09-26T18:50:00Z\nversion=0.2.0+3.abc1234\n");
        dir.File("next", "20260926-1450-abc1234\n");

        var staged = new AppInstall("/x.app", dir.Path).ReadStaged();

        Assert.Equal(new StagedUpdate("20260926-1450-abc1234", "abc1234", "Fix a thing", DateTimeOffset.Parse("2026-09-26T18:50:00Z"), "0.2.0+3.abc1234"), staged);
    }

    [Fact]
    public void A_build_published_before_versions_has_none()
    {
        var staged = AppInstall.Parse("id", ["commit=abc1234", "subject=Older", "version="]);

        Assert.Null(staged.Version);
    }

    [Fact]
    public void Nothing_is_staged_without_next() =>
        Assert.Null(new AppInstall("/x.app", new TempDir().Path).ReadStaged());

    [Fact]
    public void A_build_still_being_copied_is_not_staged()
    {
        using var dir = new TempDir();
        dir.Dir("builds/half");
        dir.File("next", "half");

        Assert.Null(new AppInstall("/x.app", dir.Path).ReadStaged());
    }

    [Theory]
    [InlineData("../elsewhere")]
    [InlineData("..")]
    [InlineData("/etc")]
    public void Next_cannot_point_outside_the_builds(string id)
    {
        using var dir = new TempDir();
        dir.File("next", id);

        Assert.Null(new AppInstall("/x.app", dir.Path).ReadStaged());
    }
}
