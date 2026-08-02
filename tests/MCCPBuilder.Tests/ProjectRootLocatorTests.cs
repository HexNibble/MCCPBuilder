using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class ProjectRootLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MCCPBuilderRootLocatorTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LocateFindsProjectFromNestedExecutableDirectory()
    {
        var projectRoot = CreateProject("project");
        var executableDirectory = Path.Combine(
            projectRoot,
            "artifacts",
            "MCCPBuilder.App",
            "win-x64");
        Directory.CreateDirectory(executableDirectory);

        var found = ProjectRootLocator.Locate(
            [executableDirectory]);

        Assert.Equal(
            Path.GetFullPath(projectRoot),
            found);
    }

    [Fact]
    public void LocateFindsExplicitProjectWhenExecutableWasCopiedElsewhere()
    {
        var copiedExecutableDirectory = Path.Combine(
            _root,
            "desktop-copy");
        Directory.CreateDirectory(copiedExecutableDirectory);
        var projectRoot = CreateProject(
            Path.Combine("other-drive", "MCCP", "MCCPBuilder"));

        var found = ProjectRootLocator.Locate(
            [copiedExecutableDirectory, projectRoot]);

        Assert.Equal(
            Path.GetFullPath(projectRoot),
            found);
    }

    [Fact]
    public void LocateExplainsEnvironmentVariableWhenProjectIsMissing()
    {
        var unrelatedDirectory = Path.Combine(_root, "unrelated");
        Directory.CreateDirectory(unrelatedDirectory);

        var exception = Assert.Throws<DirectoryNotFoundException>(
            () => ProjectRootLocator.Locate([unrelatedDirectory]));

        Assert.Contains(
            ProjectRootLocator.ProjectRootEnvironmentVariable,
            exception.Message);
        Assert.Contains(
            "找不到 MCCPBuilder 开发项目",
            exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private string CreateProject(string relativePath)
    {
        var projectRoot = Path.Combine(_root, relativePath);
        var launcherDirectory = Path.Combine(
            projectRoot,
            "src",
            "MCCPBuilder.Launcher");
        Directory.CreateDirectory(launcherDirectory);
        File.WriteAllText(
            Path.Combine(projectRoot, "MCCPBuilder.sln"),
            "");
        File.WriteAllText(
            Path.Combine(
                launcherDirectory,
                "MCCPBuilder.Launcher.csproj"),
            "");
        return projectRoot;
    }
}
