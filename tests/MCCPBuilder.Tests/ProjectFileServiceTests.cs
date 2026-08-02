using MCCPBuilder.Core;
using MCCPBuilder.Models;

namespace MCCPBuilder.Tests;

public sealed class ProjectFileServiceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), "MCCPBuilderTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_PreservesProjectData_WithChineseAndSpacesInPath()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var path = Path.Combine(_temporaryDirectory, "中文 项目.mccpproject");
        var project = new ProjectConfig
        {
            Basic = new()
            {
                ClientName = "测试客户端",
                ClientVersion = "1.2.3",
                MinecraftVersion = "1.20.1",
                LauncherTitle = "最后防线启动器",
                LauncherBackgroundImagePath = @"E:\素材 文件\登录背景.png"
            },
            Launch = new()
            {
                UsePcl2JvmPreset = true,
                AutoJoinServer = true,
                ServerAddress = "mc.example.test:25565",
                CleanCachesBeforeLaunch = true,
                CleanLogsBeforeLaunch = true,
                CustomizeForgeMcpBranding = true,
                ForgeMcpBrandingText = "最后防线 2.2"
            },
            Installation = new()
            {
                RunLauncherAsAdministrator = true
            },
            Update = new()
            {
                DownloadConcurrency = 128
            }
        };
        var service = new ProjectFileService();

        await service.SaveAsync(project, path);
        var loaded = await service.LoadAsync(path);

        Assert.Equal("测试客户端", loaded.Basic.ClientName);
        Assert.Equal("1.2.3", loaded.Basic.ClientVersion);
        Assert.Equal("最后防线启动器", loaded.Basic.LauncherTitle);
        Assert.Equal(
            @"E:\素材 文件\登录背景.png",
            loaded.Basic.LauncherBackgroundImagePath);
        Assert.True(loaded.Launch.UsePcl2JvmPreset);
        Assert.True(loaded.Launch.AutoJoinServer);
        Assert.Equal("mc.example.test:25565", loaded.Launch.ServerAddress);
        Assert.True(loaded.Launch.CleanCachesBeforeLaunch);
        Assert.True(loaded.Launch.CleanLogsBeforeLaunch);
        Assert.True(loaded.Launch.CustomizeForgeMcpBranding);
        Assert.Equal("最后防线 2.2", loaded.Launch.ForgeMcpBrandingText);
        Assert.True(loaded.Installation.RunLauncherAsAdministrator);
        Assert.Equal(128, loaded.Update.DownloadConcurrency);
        Assert.Equal(LoginProviderType.Microsoft, loaded.LoginProviders.Single().Type);
    }

    [Fact]
    public async Task Load_RejectsUnknownProjectExtension()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var path = Path.Combine(_temporaryDirectory, "错误扩展名.legacyproject");
        await File.WriteAllTextAsync(
            path,
            """{"formatVersion":"1.0","basic":{"clientName":"旧版"}}""");

        await Assert.ThrowsAsync<ArgumentException>(
            () => new ProjectFileService().LoadAsync(path));
    }

    [Fact]
    public async Task Load_LegacyProjectWithoutDownloadConcurrency_UsesDefault200()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var path = Path.Combine(
            _temporaryDirectory,
            "旧版无下载线程配置.mccpproject");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "formatVersion": "1.0",
              "update": {
                "serverBaseUrl": "https://updates.example/",
                "productId": "legacy-client",
                "requireSuccessfulCheck": true
              }
            }
            """);

        var loaded = await new ProjectFileService().LoadAsync(path);

        Assert.Equal(200, loaded.Update.DownloadConcurrency);
    }

    [Fact]
    public async Task Load_RejectsUnsupportedFormatVersion()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var path = Path.Combine(_temporaryDirectory, "future.mccpproject");
        await File.WriteAllTextAsync(path, """{"formatVersion":"99.0"}""");

        await Assert.ThrowsAsync<NotSupportedException>(() => new ProjectFileService().LoadAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
            Directory.Delete(_temporaryDirectory, true);
    }
}
