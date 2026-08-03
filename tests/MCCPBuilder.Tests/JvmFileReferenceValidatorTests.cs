using MCCPBuilder.Core;
using MCCPBuilder.Models;

namespace MCCPBuilder.Tests;

public sealed class JvmFileReferenceValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MCCPBuilderJvmFileTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Validate_AllowsExistingJavaAgentThatWillEnterPayload()
    {
        var project = CreateProject();
        WriteFile(project.Client.VersionDirectory, "dac-agent.jar", "agent");
        project.Launch.JvmArguments =
        [
            "-Dexample=true -javaagent:\"dac-agent.jar\"=server-id"
        ];
        var scan = await new FileScanService().ScanAsync(project.Client);

        var diagnostics = JvmFileReferenceValidator.Validate(project, scan);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Validate_RejectsMissingJavaAgent()
    {
        var project = CreateProject();
        project.Launch.JvmArguments = ["-javaagent:missing-agent.jar"];
        var scan = await new FileScanService().ScanAsync(project.Client);

        var diagnostics = JvmFileReferenceValidator.Validate(project, scan);

        Assert.Contains(diagnostics, message =>
            message.Contains("文件不存在", StringComparison.Ordinal) &&
            message.Contains("missing-agent.jar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_RejectsMissingJarFile()
    {
        var project = CreateProject();
        project.Java.Arguments = ["-jar missing-bootstrap.jar"];
        var scan = await new FileScanService().ScanAsync(project.Client);

        var diagnostics = JvmFileReferenceValidator.Validate(project, scan);

        Assert.Contains(diagnostics, message =>
            message.Contains("-jar", StringComparison.Ordinal) &&
            message.Contains("文件不存在", StringComparison.Ordinal) &&
            message.Contains("missing-bootstrap.jar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_RejectsJavaAgentExcludedFromPayload()
    {
        var project = CreateProject();
        WriteFile(project.Client.VersionDirectory, "dac-agent.jar", "agent");
        project.Client.ExcludeRules = ["**/dac-agent.jar"];
        project.Launch.JvmArguments = ["-javaagent:dac-agent.jar"];
        var scan = await new FileScanService().ScanAsync(project.Client);

        var diagnostics = JvmFileReferenceValidator.Validate(project, scan);

        Assert.Contains(diagnostics, message =>
            message.Contains("不会进入最终 Payload", StringComparison.Ordinal) &&
            message.Contains("dac-agent.jar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_RejectsAbsoluteJavaFilePath()
    {
        var project = CreateProject();
        var helper = Path.Combine(project.Client.VersionDirectory, "helper.jar");
        WriteFile(project.Client.VersionDirectory, "helper.jar", "helper");
        project.Java.Arguments = [$"-java:\"{helper}\""];
        var scan = await new FileScanService().ScanAsync(project.Client);

        var diagnostics = JvmFileReferenceValidator.Validate(project, scan);

        Assert.Contains(diagnostics, message =>
            message.Contains("不能使用打包电脑的绝对路径", StringComparison.Ordinal));
    }

    private ProjectConfig CreateProject()
    {
        var source = Path.Combine(_root, ".minecraft");
        var version = Path.Combine(source, "versions", "测试版本");
        WriteFile(version, "测试版本.jar", "official main jar");
        WriteFile(version, "测试版本.json", "{}");
        return new ProjectConfig
        {
            Client = new()
            {
                SourceDirectory = source,
                MinecraftRootDirectory = source,
                VersionDirectory = version,
                VersionManifestPath = @"versions\测试版本\测试版本.json",
                LaunchEntryPath = @"versions\测试版本\测试版本.jar",
                IncludeRules = ["**/*"],
                ExcludeRules = [],
                DownloadMinecraftAndForgeFromOfficialSources = true,
                ResourceDelivery = ResourceDeliveryMode.CustomServer
            }
        };
    }

    private static void WriteFile(
        string root,
        string relativePath,
        string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
