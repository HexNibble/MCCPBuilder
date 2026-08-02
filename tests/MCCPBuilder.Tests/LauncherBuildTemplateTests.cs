using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class LauncherBuildTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MCCPBuilderEmbeddedTemplateTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void EmbeddedTemplateContainsRequiredProjectsAndSources()
    {
        var files =
            LauncherBuildTemplateService.GetEmbeddedFilePaths();

        Assert.Contains(
            "Directory.Build.props",
            files);
        Assert.Contains(
            "MCCPBuilder.Launcher/MCCPBuilder.Launcher.csproj",
            files);
        Assert.Contains(
            "MCCPBuilder.Launcher/Program.cs",
            files);
        Assert.Contains(
            "MCCPBuilder.Core/MCCPBuilder.Core.csproj",
            files);
        Assert.Contains(
            "MCCPBuilder.Models/MCCPBuilder.Models.csproj",
            files);
        Assert.DoesNotContain(
            files,
            path => path.Contains(
                "/bin/",
                StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(
                        "/obj/",
                        StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EmbeddedTemplateExtractsToIndependentDirectory()
    {
        var destination = Path.Combine(
            _root,
            "包含 空格",
            "BuildTemplate");

        await LauncherBuildTemplateService.ExtractAsync(
            destination);

        LauncherBuildTemplateService.ValidateExtractedTemplate(
            destination);
        Assert.True(File.Exists(Path.Combine(
            destination,
            "MCCPBuilder.Launcher",
            "LoginWindow.xaml.cs")));
        Assert.True(File.Exists(Path.Combine(
            destination,
            "MCCPBuilder.Core",
            "ClientUpdateService.cs")));
    }

    [Fact]
    public async Task PublisherBuildsLauncherFromEmbeddedTemplate()
    {
        var destination = Path.Combine(
            _root,
            "独立 输出",
            "Launcher.exe");

        var result = await new LauncherPublisherService()
            .PublishAsync(
                destination,
                "9.8.7",
                "");

        Assert.Equal(
            Path.GetFullPath(destination),
            Path.GetFullPath(result.ExecutablePath));
        Assert.True(File.Exists(destination));
        Assert.True(new FileInfo(destination).Length > 1024 * 1024);
        var header = new byte[2];
        await using var stream = File.OpenRead(destination);
        _ = await stream.ReadAsync(header);
        Assert.Equal((byte)'M', header[0]);
        Assert.Equal((byte)'Z', header[1]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
