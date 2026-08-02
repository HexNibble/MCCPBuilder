using MCCPBuilder.Core;
using MCCPBuilder.Models;
using System.Text.Json;

namespace MCCPBuilder.Tests;

public sealed class BatchLaunchServiceTests
{
    [Fact]
    public async Task PrepareAsync_RewritesJavaPathsAgentsAndSensitiveSessionValues()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"MCCPBuilder-Batch-{Guid.NewGuid():N}");
        var minecraftRoot = Path.Combine(tempRoot, "中文 客户端", ".minecraft");
        var versionDirectory = Path.Combine(minecraftRoot, "versions", "Test");
        var sourceBatch = Path.Combine(tempRoot, "启动 游戏.bat");
        var destinationBatch = Path.Combine(tempRoot, "payload", "LauncherConfig", "launch.bat");
        var destinationArguments = Path.Combine(
            Path.GetDirectoryName(destinationBatch)!,
            "launch.arguments.json");
        Directory.CreateDirectory(minecraftRoot);

        var oldJava = Path.Combine(tempRoot, "旧 Java", "bin", "java.exe");
        var classPath = Path.Combine(minecraftRoot, "libraries", "a.jar") + ";" +
                        Path.Combine(minecraftRoot, "versions", "Test", "Test.jar");
        var script = $"""
            chcp 65001>nul
            @echo off
            cd /D "{Path.Combine(minecraftRoot, "versions", "Test")}"
            "{oldJava}" -Djava.library.path={Path.Combine(minecraftRoot, "natives")} -cp {classPath} -javaagent:"C:\old\nide8auth.jar"=old-server Main --username PrivateName --uuid privateuuid --accessToken privateToken --clientId privateClient --xuid privateXuid --userType msa --gameDir {minecraftRoot} --assetsDir {Path.Combine(minecraftRoot, "assets")}
            pause
            """;
        await File.WriteAllTextAsync(sourceBatch, script);

        var project = new ProjectConfig
        {
            Client = new ClientContentOptions
            {
                MinecraftRootDirectory = minecraftRoot,
                VersionDirectory = versionDirectory
            },
            Launch = new LaunchOptions
            {
                UseBatchFile = true,
                BatchFilePath = sourceBatch
            },
            LoginProviders =
            [
                new()
                {
                    Type = LoginProviderType.CustomAuthenticationServer,
                    ServerUrl = new Uri("https://authlib.example.test/api/yggdrasil")
                },
                new()
                {
                    Type = LoginProviderType.UnifiedPassport,
                    ServerIdentifier = "0123456789abcdef0123456789abcdef"
                }
            ]
        };

        try
        {
            await new BatchLaunchService().PrepareAsync(project, destinationBatch);
            var result = await File.ReadAllTextAsync(destinationBatch);
            var argumentJson = await File.ReadAllTextAsync(destinationArguments);
            using var argumentDocument = JsonDocument.Parse(argumentJson);
            var arguments = argumentDocument.RootElement.GetProperty("Arguments")
                .EnumerateArray()
                .Select(element => element.GetString() ?? "")
                .ToArray();

            Assert.Contains(@"%MCCP_APP_ROOT%\Launcher.exe"" --run-generated", result, StringComparison.Ordinal);
            Assert.True(result.Split(["\r\n", "\n"], StringSplitOptions.None).Max(line => line.Length) < 8191);
            Assert.Contains(arguments, argument => argument.Contains("${MCCP_GAME_ROOT}", StringComparison.Ordinal));
            Assert.Contains("${MCCP_ACCESS_TOKEN}", arguments);
            Assert.Equal(
                @".minecraft\versions\Test",
                argumentDocument.RootElement.GetProperty("WorkingDirectory").GetString());
            Assert.DoesNotContain(oldJava, result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(minecraftRoot, result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(arguments, argument => argument.Contains(minecraftRoot, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("PrivateName", result, StringComparison.Ordinal);
            Assert.DoesNotContain("PrivateName", argumentJson, StringComparison.Ordinal);
            Assert.DoesNotContain("privateuuid", result, StringComparison.Ordinal);
            Assert.DoesNotContain("privateToken", result, StringComparison.Ordinal);
            Assert.DoesNotContain("privateClient", result, StringComparison.Ordinal);
            Assert.DoesNotContain("privateXuid", result, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Validate_RejectsCommandsOutsideSafeBatchSubset()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"MCCPBuilder-Batch-{Guid.NewGuid():N}");
        var sourceBatch = Path.Combine(tempRoot, "launch.bat");
        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(
            sourceBatch,
            "\"C:\\Java\\bin\\java.exe\" -version\r\npowershell -EncodedCommand ZgBvAG8A");

        try
        {
            var project = new ProjectConfig
            {
                Launch = new LaunchOptions
                {
                    UseBatchFile = true,
                    BatchFilePath = sourceBatch
                }
            };

            var errors = new BatchLaunchService().Validate(project);

            Assert.Contains(errors, error => error.Contains("不允许", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
