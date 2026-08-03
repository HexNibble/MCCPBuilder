using MCCPBuilder.Core;
using MCCPBuilder.Models;

namespace MCCPBuilder.Tests;

public sealed class PreflightServiceTests
{
    [Fact]
    public async Task Check_BlocksBuildWhenJavaAgentFileIsMissing()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "MCCPBuilderMissingAgentPreflight",
            Guid.NewGuid().ToString("N"));
        var version = Path.Combine(root, "versions", "测试版本");
        Directory.CreateDirectory(version);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(version, "测试版本.jar"),
                "client");
            await File.WriteAllTextAsync(
                Path.Combine(version, "测试版本.json"),
                "{\"mainClass\":\"example.Main\",\"arguments\":{\"jvm\":[],\"game\":[]}}");
            var project = new ProjectConfig
            {
                Basic = new()
                {
                    ClientName = "Client",
                    ClientVersion = "1.0.0",
                    LauncherVersion = "1.0.0",
                    OutputFileName = "Setup"
                },
                Client = new()
                {
                    SourceDirectory = root,
                    MinecraftRootDirectory = root,
                    VersionDirectory = version,
                    VersionManifestPath = @"versions\测试版本\测试版本.json",
                    LaunchEntryPath = @"versions\测试版本\测试版本.jar",
                    IncludeRules = ["**/*"],
                    ExcludeRules = []
                },
                Launch = new()
                {
                    JvmArguments = ["-javaagent:missing-agent.jar"]
                }
            };
            var service = new PreflightService(
                new FileScanService(),
                new JavaDetectionService());

            var results = await service.CheckAsync(project);

            Assert.Contains(results, result =>
                result.Severity == CheckSeverity.Error &&
                result.Area == "JVM 文件" &&
                result.Message.Contains("missing-agent.jar", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Check_ReportsMissingSourceDirectory()
    {
        var project = new ProjectConfig
        {
            Basic = new() { ClientName = "Client", ClientVersion = "1.0.0", OutputFileName = "Setup" },
            Client = new() { SourceDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }
        };
        var service = new PreflightService(new FileScanService(), new JavaDetectionService());

        var results = await service.CheckAsync(project);

        Assert.Contains(results, result => result.Severity == CheckSeverity.Error && result.Area == "客户端");
    }

    [Fact]
    public async Task Check_RejectsInvalidAutoJoinServerAddress()
    {
        var project = new ProjectConfig
        {
            Basic = new() { ClientName = "Client", ClientVersion = "1.0.0", OutputFileName = "Setup" },
            Client = new() { SourceDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) },
            Launch = new() { AutoJoinServer = true, ServerAddress = "https://mc.example.test" }
        };
        var service = new PreflightService(new FileScanService(), new JavaDetectionService());

        var results = await service.CheckAsync(project);

        Assert.Contains(
            results,
            result => result.Severity == CheckSeverity.Error &&
                      result.Area == "启动" &&
                      result.Message.Contains("服务器地址", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("nide8auth.cache")]
    [InlineData("usercache.json")]
    [InlineData("launcher_profiles.json")]
    [InlineData("PCL.ini")]
    public async Task Check_AutomaticallyExcludesAuthenticationAndUserCacheFiles(string fileName)
    {
        var sourceDirectory = Path.Combine(
            Path.GetTempPath(),
            "MCCPBuilderSensitivePreflight",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, fileName), "sensitive");
            var project = new ProjectConfig
            {
                Basic = new()
                {
                    ClientName = "Client",
                    ClientVersion = "1.0.0",
                    OutputFileName = "Setup"
                },
                Client = new()
                {
                    SourceDirectory = sourceDirectory,
                    IncludeRules = ["**/*"],
                    ExcludeRules = []
                }
            };
            var service = new PreflightService(
                new FileScanService(),
                new JavaDetectionService());

            var results = await service.CheckAsync(project);

            Assert.Contains(
                results,
                result => result.Severity == CheckSeverity.Warning &&
                          result.Area == "隐私" &&
                          result.Message.Contains("自动排除", StringComparison.Ordinal) &&
                          result.Message.Contains(fileName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(sourceDirectory, true);
        }
    }
}
