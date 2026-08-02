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
