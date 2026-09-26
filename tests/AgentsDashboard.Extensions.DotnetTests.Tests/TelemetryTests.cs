using AgentsDashboard.Extensions.DotnetTests;
using AgentsDashboard.Extensions.DotnetTests.Tests.Support;

namespace AgentsDashboard.Extensions.DotnetTests.Tests;

public class TestProjectProbeTests
{
    private const string XunitV3Mtp = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
          </PropertyGroup>
          <ItemGroup><PackageReference Include="xunit.v3" /></ItemGroup>
        </Project>
        """;

    private const string ClassicVsTest = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="xunit" Version="2.9.3" />
          </ItemGroup>
        </Project>
        """;

    private const string NotATestProject = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <ItemGroup><PackageReference Include="Serilog" /></ItemGroup>
        </Project>
        """;

    [Fact]
    public void Finds_test_projects_and_ignores_the_rest()
    {
        using var dir = new TempDir();
        dir.File("src/App/App.csproj", NotATestProject);
        dir.File("tests/App.Tests/App.Tests.csproj", ClassicVsTest);

        var report = TestProjectProbe.Probe(dir.Path);

        Assert.Equal("App.Tests", Assert.Single(report.Projects).Name);
    }

    [Fact]
    public void Tells_the_two_runners_apart()
    {
        using var dir = new TempDir();
        dir.File("tests/New.Tests/New.Tests.csproj", XunitV3Mtp);
        dir.File("tests/Old.Tests/Old.Tests.csproj", ClassicVsTest);

        var report = TestProjectProbe.Probe(dir.Path);

        Assert.Equal(
            TestRunnerKind.MicrosoftTestingPlatform,
            report.Projects.Single(p => p.Name == "New.Tests").Runner);
        Assert.Equal(
            TestRunnerKind.VsTest,
            report.Projects.Single(p => p.Name == "Old.Tests").Runner);
    }

    [Fact]
    public void Sees_a_runner_switched_on_for_the_whole_repository()
    {
        using var dir = new TempDir();
        dir.File("Directory.Build.props", """
            <Project>
              <PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>
            </Project>
            """);
        dir.File("tests/A.Tests/A.Tests.csproj", ClassicVsTest);

        Assert.Equal(
            TestRunnerKind.MicrosoftTestingPlatform,
            Assert.Single(TestProjectProbe.Probe(dir.Path).Projects).Runner);
    }

    [Fact]
    public void Reports_that_nothing_forces_a_report_by_default()
    {
        using var dir = new TempDir();
        dir.File("tests/A.Tests/A.Tests.csproj", ClassicVsTest);

        // This is the case that makes an agent's runs invisible: neither runner
        // writes a report unless asked, and `dotnet test` does not ask.
        Assert.False(TestProjectProbe.Probe(dir.Path).TrxAlwaysOn);
    }

    [Theory]
    [InlineData("<VSTestLogger>trx</VSTestLogger>")]
    [InlineData("<TestingPlatformCommandLineArguments>--report-trx</TestingPlatformCommandLineArguments>")]
    public void Sees_an_always_on_report(string property)
    {
        using var dir = new TempDir();
        dir.File("Directory.Build.targets", $"<Project><PropertyGroup>{property}</PropertyGroup></Project>");
        dir.File("tests/A.Tests/A.Tests.csproj", ClassicVsTest);

        Assert.True(TestProjectProbe.Probe(dir.Path).TrxAlwaysOn);
    }

    [Fact]
    public void Sees_a_runsettings_that_turns_on_the_trx_logger()
    {
        using var dir = new TempDir();
        dir.File("ci.runsettings", """
            <RunSettings><LoggerRunSettings><Loggers><Logger friendlyName="TrxLogger" /></Loggers></LoggerRunSettings></RunSettings>
            """);
        dir.File("tests/A.Tests/A.Tests.csproj", ClassicVsTest);

        Assert.True(TestProjectProbe.Probe(dir.Path).TrxAlwaysOn);
    }

    [Fact]
    public void Does_not_walk_into_build_output()
    {
        using var dir = new TempDir();
        dir.File("tests/A.Tests/A.Tests.csproj", ClassicVsTest);
        dir.File("tests/A.Tests/obj/Debug/Copied.Tests.csproj", ClassicVsTest);
        dir.File("node_modules/thing/Thing.Tests.csproj", ClassicVsTest);

        Assert.Single(TestProjectProbe.Probe(dir.Path).Projects);
    }
}

public class TelemetryInstallerTests
{
    private static TestTelemetryReport Report(TempDir dir) => new()
    {
        RepoRoot = dir.Path,
        TargetsPath = Path.Combine(dir.Path, "Directory.Build.targets"),
    };

    [Fact]
    public void Creates_the_targets_file_when_there_is_none()
    {
        using var dir = new TempDir();
        var report = Report(dir);

        var install = TelemetryInstaller.Install(report);

        Assert.True(install.Created);
        var written = File.ReadAllText(install.Path);
        Assert.Contains("--report-trx", written);
        Assert.Contains("VSTestLogger", written);

        // Targets, not props: IsTestProject is set by the test SDK after props are
        // imported, so a condition on it in props matches nothing.
        Assert.EndsWith("Directory.Build.targets", install.Path, StringComparison.Ordinal);
        Assert.Contains("'$(IsTestProject)' == 'true'", written);
        Assert.True(TelemetryInstaller.IsInstalled(report));
    }

    [Fact]
    public void Adds_to_an_existing_targets_file_without_losing_what_is_in_it()
    {
        using var dir = new TempDir();
        dir.File("Directory.Build.targets", """
            <Project>
              <PropertyGroup><SomethingImportant>yes</SomethingImportant></PropertyGroup>
            </Project>
            """);

        var install = TelemetryInstaller.Install(Report(dir));

        Assert.False(install.Created);
        var written = File.ReadAllText(install.Path);
        Assert.Contains("SomethingImportant", written);
        Assert.Contains("--report-trx", written);
        Assert.Equal(1, written.Split("</Project>").Length - 1);
    }

    [Fact]
    public void Installing_twice_changes_nothing_the_second_time()
    {
        using var dir = new TempDir();
        var report = Report(dir);

        TelemetryInstaller.Install(report);
        var first = File.ReadAllText(report.TargetsPath);

        TelemetryInstaller.Install(report);

        Assert.Equal(first, File.ReadAllText(report.TargetsPath));
    }

    [Fact]
    public void Leaves_a_file_it_does_not_understand_alone_and_says_so()
    {
        using var dir = new TempDir();
        dir.File("Directory.Build.targets", "this is not xml at all");
        var report = Report(dir);

        Assert.True(TelemetryInstaller.NeedsManualEdit(report));

        TelemetryInstaller.Install(report);

        Assert.Equal("this is not xml at all", File.ReadAllText(report.TargetsPath));
    }

    [Fact]
    public void Preview_does_not_write_anything()
    {
        using var dir = new TempDir();
        var report = Report(dir);

        var preview = TelemetryInstaller.Preview(report);

        Assert.Contains("--report-trx", preview.Content);
        Assert.False(File.Exists(report.TargetsPath));
    }
}
