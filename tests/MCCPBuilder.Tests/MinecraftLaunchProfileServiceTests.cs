using System.Text.Json;
using MCCPBuilder.Core;
using MCCPBuilder.Models;

namespace MCCPBuilder.Tests;

public sealed class MinecraftLaunchProfileServiceTests : IDisposable
{
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), "MCCPBuilderLaunchProfileTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GenerateAsync_BuildsPortableArgumentsFromVersionJsonWithoutBatchSource()
    {
        var minecraftRoot = Path.Combine(_temporaryDirectory, "中文 客户端", ".minecraft");
        var versionDirectory = Path.Combine(minecraftRoot, "versions", "测试整合包");
        var manifestPath = Path.Combine(versionDirectory, "forge.json");
        var clientJar = Path.Combine(versionDirectory, "forge.jar");
        var library = Path.Combine(minecraftRoot, "libraries", "example", "demo", "1.0", "demo-1.0.jar");
        Directory.CreateDirectory(versionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        await File.WriteAllTextAsync(clientJar, "jar");
        await File.WriteAllTextAsync(library, "library");
        await File.WriteAllTextAsync(manifestPath, """
            {
              "id": "forge",
              "type": "release",
              "mainClass": "example.Main",
              "assets": "5",
              "arguments": {
                "jvm": ["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"],
                "game": ["--username", "${auth_player_name}", "--accessToken", "${auth_access_token}",
                         "--gameDir", "${game_directory}", "--width", "${resolution_width}"]
              },
              "libraries": [
                { "name": "example:demo:1.0",
                  "downloads": { "artifact": { "path": "example/demo/1.0/demo-1.0.jar" } } }
              ]
            }
            """);

        var project = new ProjectConfig
        {
            Client = new()
            {
                MinecraftRootDirectory = minecraftRoot,
                SourceDirectory = minecraftRoot,
                VersionDirectory = versionDirectory,
                VersionManifestPath = Path.GetRelativePath(minecraftRoot, manifestPath),
                LaunchEntryPath = Path.GetRelativePath(minecraftRoot, clientJar)
            },
            Launch = new()
            {
                MinimumMemoryMb = 1024,
                MaximumMemoryMb = 6246,
                WindowWidth = 1280,
                UsePcl2JvmPreset = true,
                JvmArguments = ["-XX:+UseG1GC"],
                AutoJoinServer = true,
                ServerAddress = "mc.example.test:25565"
            }
        };
        var output = Path.Combine(_temporaryDirectory, "LauncherConfig");

        await new MinecraftLaunchProfileService().GenerateAsync(project, output);

        var batch = await File.ReadAllTextAsync(Path.Combine(output, "launch.bat"));
        var json = await File.ReadAllTextAsync(Path.Combine(output, "launch.arguments.json"));
        using var document = JsonDocument.Parse(json);
        var arguments = document.RootElement.GetProperty("Arguments")
            .EnumerateArray()
            .Select(value => value.GetString() ?? "")
            .ToArray();

        Assert.Contains("Launcher.exe\" --run-generated", batch, StringComparison.Ordinal);
        Assert.DoesNotContain(_temporaryDirectory, batch, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("example.Main", arguments);
        Assert.Contains("${MCCP_USERNAME}", arguments);
        Assert.Contains("${MCCP_ACCESS_TOKEN}", arguments);
        Assert.Contains("-Xmx6246m", arguments);
        Assert.All(
            MinecraftLaunchProfileService.Pcl2JvmPresetArguments,
            preset => Assert.Contains(preset, arguments));
        Assert.Single(arguments, argument => argument == "-XX:+UseG1GC");
        var quickPlayIndex = Array.IndexOf(arguments, "--quickPlayMultiplayer");
        Assert.True(quickPlayIndex >= 0);
        Assert.Equal("mc.example.test:25565", arguments[quickPlayIndex + 1]);
        Assert.Contains(arguments, value =>
            value.Contains("${MCCP_GAME_ROOT}\\libraries\\example\\demo\\1.0\\demo-1.0.jar", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, value =>
            value.Contains(_temporaryDirectory, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
            Directory.Delete(_temporaryDirectory, true);
    }
}
