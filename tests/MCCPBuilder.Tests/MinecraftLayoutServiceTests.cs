using MCCPBuilder.Core;
using MCCPBuilder.Models;

namespace MCCPBuilder.Tests;

public sealed class MinecraftLayoutServiceTests : IDisposable
{
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), "MCCPBuilderLayoutTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Detect_VersionsParent_SelectsTheOnlyVersionDirectory()
    {
        var versionDirectory = CreateVersion("最后防线");
        var versionsDirectory = Directory.GetParent(versionDirectory)!.FullName;

        var result = new MinecraftLayoutService().Detect(versionsDirectory);

        Assert.True(result.IsRecognized);
        Assert.Equal(Path.Combine(_temporaryDirectory, ".minecraft"), result.MinecraftRootDirectory);
        Assert.Equal(versionDirectory, result.VersionDirectory);
    }

    [Fact]
    public void Detect_SpecificVersion_ResolvesMinecraftRootAndLaunchJar()
    {
        var versionDirectory = CreateVersion("最后防线");
        var options = new ClientContentOptions();
        var service = new MinecraftLayoutService();

        var result = service.Detect(versionDirectory);
        MinecraftLayoutService.Apply(options, result);

        Assert.Equal(Path.Combine(_temporaryDirectory, ".minecraft"), options.MinecraftRootDirectory);
        Assert.Equal(versionDirectory, options.VersionDirectory);
        Assert.Equal(Path.Combine("versions", "最后防线", "最后防线.json"), options.VersionManifestPath);
        Assert.Equal(Path.Combine("versions", "最后防线", "最后防线.jar"), options.LaunchEntryPath);
    }

    [Fact]
    public void Detect_VersionsParentWithMultipleVersions_RequiresExplicitSelection()
    {
        CreateVersion("最后防线");
        CreateVersion("另一个版本");
        var versionsDirectory = Path.Combine(_temporaryDirectory, ".minecraft", "versions");

        var result = new MinecraftLayoutService().Detect(versionsDirectory);

        Assert.True(result.IsRecognized);
        Assert.Null(result.VersionDirectory);
        Assert.Equal(2, result.AvailableVersionDirectories.Count);
    }

    [Fact]
    public void Apply_PrefersVersionDirectoryNamedJar_WhenManifestHasDifferentName()
    {
        var versionDirectory = Path.Combine(
            _temporaryDirectory,
            ".minecraft",
            "versions",
            "最后防线");
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(
            Path.Combine(versionDirectory, "1.20.1-Forge_47.4.20.json"),
            """{"mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher"}""");
        File.WriteAllText(
            Path.Combine(versionDirectory, "1.20.1-Forge_47.4.20.jar"),
            "duplicate forge jar");
        File.WriteAllText(
            Path.Combine(versionDirectory, "最后防线.jar"),
            "version client jar");
        var options = new ClientContentOptions();

        var result = new MinecraftLayoutService().Detect(versionDirectory);
        MinecraftLayoutService.Apply(options, result);

        Assert.Equal(
            Path.Combine("versions", "最后防线", "1.20.1-Forge_47.4.20.json"),
            options.VersionManifestPath);
        Assert.Equal(
            Path.Combine("versions", "最后防线", "最后防线.jar"),
            options.LaunchEntryPath);
    }

    private string CreateVersion(string name)
    {
        var directory = Path.Combine(_temporaryDirectory, ".minecraft", "versions", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), """{"mainClass":"example.Main"}""");
        File.WriteAllText(Path.Combine(directory, name + ".jar"), "jar");
        return directory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, true);
        }
    }
}
