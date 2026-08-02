using System.Text.Json;
using MCCPBuilder.Core;
using MCCPBuilder.Models;

namespace MCCPBuilder.Tests;

public sealed class BuildOutputServiceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "MCCPBuilderBuildOutputTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void InnoSetupLocator_PrefersConfiguredExistingCompiler()
    {
        var compilerPath = Path.Combine(
            _temporaryDirectory,
            "自定义 Inno",
            "ISCC.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(compilerPath)!);
        File.WriteAllBytes(compilerPath, [0x4D, 0x5A]);

        var located = new InnoSetupLocator().FindCompiler(compilerPath);

        Assert.Equal(Path.GetFullPath(compilerPath), located);
    }

    [Fact]
    public async Task ManifestAndLauncherConfig_ArePublishedWithChinesePaths()
    {
        var payload = Path.Combine(_temporaryDirectory, "客户端 Payload");
        var output = Path.Combine(_temporaryDirectory, "最终 输出");
        Directory.CreateDirectory(Path.Combine(payload, "LauncherConfig"));
        Directory.CreateDirectory(Path.Combine(payload, ".minecraft", "配置 文件"));
        await File.WriteAllTextAsync(
            Path.Combine(payload, "LauncherConfig", "launcher.json"),
            """{"schemaVersion":1}""");
        await File.WriteAllTextAsync(
            Path.Combine(payload, ".minecraft", "配置 文件", "中文.txt"),
            "payload");
        var service = new BuildArtifactService();

        var manifest = await service.GeneratePayloadManifestAsync(payload);
        service.PublishLauncherConfig(payload, output);

        Assert.Equal(2, manifest.FileCount);
        Assert.All(
            manifest.Files,
            entry => Assert.Equal(64, entry.Sha256.Length));
        Assert.Contains(
            manifest.Files,
            entry => entry.RelativePath ==
                     ".minecraft/配置 文件/中文.txt");

        var publishedConfig = Path.Combine(
            output,
            "LauncherConfig",
            "launcher.json");
        var publishedManifest = Path.Combine(
            output,
            "LauncherConfig",
            "client-files.json");
        Assert.True(File.Exists(publishedConfig));
        Assert.True(File.Exists(publishedManifest));
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(publishedManifest));
        Assert.Equal(
            2,
            document.RootElement.GetProperty("fileCount").GetInt32());
    }

    [Fact]
    public async Task Sha256Sidecar_MatchesGeneratedFile()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var installer = Path.Combine(_temporaryDirectory, "中文 安装包.exe");
        await File.WriteAllTextAsync(installer, "installer");
        var service = new BuildArtifactService();

        var hash = await service.WriteSha256FileAsync(installer);
        var checksum = await File.ReadAllTextAsync(installer + ".sha256");

        Assert.Equal(64, hash.Length);
        Assert.Contains(hash, checksum, StringComparison.Ordinal);
        Assert.Contains("*中文 安装包.exe", checksum, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLog_ContainsProjectSummaryResultAndNoPlainCredential()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var project = new ProjectConfig
        {
            Basic = new()
            {
                ClientName = "最后防线",
                ClientVersion = "2.2.0"
            }
        };

        var writer = new BuildLogWriter(_temporaryDirectory, project);
        writer.Info("password=temporary-secret");
        writer.Complete(true);
        var content = File.ReadAllText(writer.FilePath);

        Assert.Contains("项目名称：最后防线", content, StringComparison.Ordinal);
        Assert.Contains("项目版本：2.2.0", content, StringComparison.Ordinal);
        Assert.Contains("构建结果：成功", content, StringComparison.Ordinal);
        Assert.Contains("password=<redacted>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("temporary-secret", content, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, true);
        }
    }
}
