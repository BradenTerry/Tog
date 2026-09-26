using AgentsDashboard.Core.Repos;

namespace AgentsDashboard.Core.Tests;

public class AppBundleTests
{
    [Theory]
    [InlineData("/Users/me/Desktop/Agents Dashboard.app/Contents/Resources/app/")]
    [InlineData("/Users/me/Desktop/Agents Dashboard.app/Contents/Resources/app")]
    public void The_marker_is_beside_the_running_build_in_Resources(string baseDirectory)
    {
        var bundle = AppBundle.Locate(baseDirectory);

        Assert.NotNull(bundle);
        Assert.Equal("/Users/me/Desktop/Agents Dashboard.app", bundle.Bundle);
        Assert.Equal("/Users/me/Desktop/Agents Dashboard.app/Contents/Resources/app.next/.staged", bundle.Marker);
    }

    [Theory]
    [InlineData("/repo/src/AgentsDashboard.App/bin/Debug/net10.0/")]
    [InlineData("/Users/me/Desktop/NotABundle/Contents/Resources/app/")]
    [InlineData("/Users/me/Desktop/Agents Dashboard.app/Contents/Resources/other/")]
    public void Anything_but_a_bundle_has_none(string baseDirectory) =>
        Assert.Null(AppBundle.Locate(baseDirectory));
}
