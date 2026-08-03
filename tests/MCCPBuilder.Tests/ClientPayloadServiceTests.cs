using MCCPBuilder.Core;
using MCCPBuilder.Models;

namespace MCCPBuilder.Tests;

public sealed class ClientPayloadServiceTests : IDisposable
{
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), "MCCPBuilderPayloadTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CopyAndPublish_IncludesMinecraftFilesAndRemovesStalePayload()
    {
        var source = Path.Combine(_temporaryDirectory, "中文 客户端", ".minecraft");
        WriteFile(source, @"versions\最后防线\最后防线.jar", "version");
        WriteFile(source, @"versions\最后防线 - 副本\副本.jar", "duplicate version");
        WriteFile(source, @"libraries\example\library.jar", "library");
        WriteFile(source, @"assets\indexes\5.json", "assets");
        WriteFile(source, @"logs\latest.log", "private log");
        Directory.CreateDirectory(Path.Combine(source, "versions", "最后防线", "最后防线-natives"));
        Directory.CreateDirectory(Path.Combine(source, "saves", "不应创建"));

        var output = Path.Combine(_temporaryDirectory, "output");
        var finalPayload = Path.Combine(output, "ClientPayload");
        WriteFile(finalPayload, "stale.txt", "stale");
        var staging = Path.Combine(output, ".ClientPayload.test.tmp");
        var options = new ClientContentOptions
        {
            SourceDirectory = source,
            MinecraftRootDirectory = source,
            VersionDirectory = Path.Combine(source, "versions", "最后防线"),
            DownloadMinecraftAndForgeFromOfficialSources = false,
            IncludeRules = ["**/*"],
            ExcludeRules = ["**/logs/**", "**/*.log"]
        };
        var service = new ClientPayloadService(new FileScanService());

        var result = await service.CopyClientAsync(options, staging);
        ClientPayloadService.Publish(staging, finalPayload);

        Assert.Equal(3, result.FileCount);
        var packagedMinecraft = Path.Combine(finalPayload, ".minecraft");
        Assert.True(File.Exists(Path.Combine(packagedMinecraft, "versions", "最后防线", "最后防线.jar")));
        Assert.True(File.Exists(Path.Combine(packagedMinecraft, "libraries", "example", "library.jar")));
        Assert.True(File.Exists(Path.Combine(packagedMinecraft, "assets", "indexes", "5.json")));
        Assert.False(Directory.Exists(Path.Combine(
            packagedMinecraft,
            "versions",
            "最后防线 - 副本")));
        Assert.True(Directory.Exists(Path.Combine(
            packagedMinecraft,
            "versions",
            "最后防线",
            "最后防线-natives")));
        Assert.False(File.Exists(Path.Combine(packagedMinecraft, "logs", "latest.log")));
        Assert.False(Directory.Exists(Path.Combine(packagedMinecraft, "saves")));
        Assert.False(Directory.Exists(Path.Combine(finalPayload, "versions")));
        Assert.False(File.Exists(Path.Combine(finalPayload, "stale.txt")));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task OfficialMode_ExcludesGameCodeButKeepsSelectedConfiguration()
    {
        var source = Path.Combine(_temporaryDirectory, "official", ".minecraft");
        WriteFile(source, @"versions\最后防线\最后防线.jar", "mojang client");
        WriteFile(source, @"versions\最后防线\最后防线.json", "manifest");
        WriteFile(source, @"versions\最后防线\dac-agent.jar", "anti-cheat agent");
        WriteFile(source, @"versions\最后防线\anti-cheat.json", "anti-cheat config");
        WriteFile(source, @"versions\最后防线\config\client.toml", "config");
        WriteFile(source, @"versions\最后防线\mods\custom.jar", "mod");
        WriteFile(source, @"versions\最后防线\最后防线-natives\native.dll", "native");
        WriteFile(source, @"libraries\example\library.jar", "library");
        WriteFile(source, @"assets\indexes\5.json", "assets");
        var staging = Path.Combine(_temporaryDirectory, "official-output", ".tmp");
        var options = new ClientContentOptions
        {
            SourceDirectory = source,
            MinecraftRootDirectory = source,
            VersionDirectory = Path.Combine(source, "versions", "最后防线"),
            IncludeRules = ["**/*"],
            ExcludeRules = [],
            DownloadMinecraftAndForgeFromOfficialSources = true,
            ResourceDelivery = ResourceDeliveryMode.CustomServer
        };

        var result = await new ClientPayloadService(new FileScanService())
            .CopyClientAsync(options, staging);

        Assert.Equal(4, result.FileCount);
        Assert.False(File.Exists(Path.Combine(staging, ".minecraft", "versions", "最后防线", "最后防线.jar")));
        Assert.False(File.Exists(Path.Combine(staging, ".minecraft", "versions", "最后防线", "最后防线.json")));
        Assert.False(Directory.Exists(Path.Combine(staging, ".minecraft", "versions", "最后防线", "最后防线-natives")));
        Assert.False(Directory.Exists(Path.Combine(staging, ".minecraft", "libraries", "example")));
        Assert.True(File.Exists(Path.Combine(staging, ".minecraft", "versions", "最后防线", "dac-agent.jar")));
        Assert.True(File.Exists(Path.Combine(staging, ".minecraft", "versions", "最后防线", "anti-cheat.json")));
        Assert.True(File.Exists(Path.Combine(staging, ".minecraft", "versions", "最后防线", "config", "client.toml")));
        Assert.True(File.Exists(Path.Combine(staging, ".minecraft", "versions", "最后防线", "mods", "custom.jar")));
    }

    [Fact]
    public async Task ModrinthMode_DoesNotUploadProviderManagedMods()
    {
        var source = Path.Combine(_temporaryDirectory, "modrinth", ".minecraft");
        WriteFile(source, @"versions\最后防线\config\client.toml", "config");
        WriteFile(source, @"versions\最后防线\mods\provider.jar", "mod");
        var staging = Path.Combine(_temporaryDirectory, "modrinth-output", ".tmp");
        var options = new ClientContentOptions
        {
            SourceDirectory = source,
            MinecraftRootDirectory = source,
            VersionDirectory = Path.Combine(source, "versions", "最后防线"),
            IncludeRules = ["**/*"],
            ExcludeRules = [],
            ResourceDelivery = ResourceDeliveryMode.Modrinth
        };

        var result = await new ClientPayloadService(new FileScanService())
            .CopyClientAsync(options, staging);

        Assert.Equal(1, result.FileCount);
        Assert.True(File.Exists(Path.Combine(staging, ".minecraft", "versions", "最后防线", "config", "client.toml")));
        Assert.False(File.Exists(Path.Combine(staging, ".minecraft", "versions", "最后防线", "mods", "provider.jar")));
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, true);
        }
    }
}
