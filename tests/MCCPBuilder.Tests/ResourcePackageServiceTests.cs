using System.IO.Compression;
using System.Text;
using MCCPBuilder.Core;
using MCCPBuilder.Models;

namespace MCCPBuilder.Tests;

public sealed class ResourcePackageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MCCPBuilderResourcePackageTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ModrinthPack_ProducesOfficialDownloadManifestAndStagesOverrides()
    {
        Directory.CreateDirectory(_root);
        var packagePath = Path.Combine(_root, "测试整合包.mrpack");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            var index = archive.CreateEntry("modrinth.index.json");
            await using (var stream = index.Open())
            {
                var json = """
                    {"files":[{"path":"mods/example.jar","hashes":{"sha1":"0123456789012345678901234567890123456789"},"downloads":["https://cdn.modrinth.com/data/test/versions/1/example.jar"],"fileSize":123}]}
                    """;
                await stream.WriteAsync(Encoding.UTF8.GetBytes(json));
            }
            var config = archive.CreateEntry("overrides/config/client.toml");
            await using var configStream = config.Open();
            await configStream.WriteAsync(Encoding.UTF8.GetBytes("enabled=true"));
        }
        var payload = Path.Combine(_root, "payload");
        var launcherConfig = Path.Combine(payload, "LauncherConfig");
        var options = new ClientContentOptions
        {
            ResourceDelivery = ResourceDeliveryMode.Modrinth,
            ResourcePackagePath = packagePath
        };

        var result = await new ResourcePackageService().StageAsync(
            options, launcherConfig, payload, "");

        Assert.Equal("Modrinth", result.Provider);
        Assert.Single(result.Files);
        Assert.Equal("mods/example.jar", result.Files[0].Path);
        Assert.True(File.Exists(Path.Combine(payload, ".minecraft", "config", "client.toml")));
        Assert.True(File.Exists(Path.Combine(launcherConfig, "Resources", "downloads.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
