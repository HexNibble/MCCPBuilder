using MCCPBuilder.Core;
using MCCPBuilder.Models;

namespace MCCPBuilder.Tests;

public sealed class PreflightServiceTests
{
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
