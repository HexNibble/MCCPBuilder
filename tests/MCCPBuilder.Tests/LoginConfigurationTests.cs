using System.Text.Json;
using MCCPBuilder.Models;
using MCCPBuilder.Packaging;

namespace MCCPBuilder.Tests;

public sealed class LoginConfigurationTests
{
    [Fact]
    public void LauncherConfig_UsesNide8AuthAgentForUnifiedPassport()
    {
        const string serverIdentifier = "0123456789abcdef0123456789abcdef";
        var project = new ProjectConfig
        {
            LoginProviders =
            [
                new()
                {
                    Type = LoginProviderType.UnifiedPassport,
                    DisplayName = "统一通行证",
                    ServerUrl = new Uri("https://login.mc-user.com:233/"),
                    AuthenticationAgentPath = @"C:\Agents\nide8auth.jar",
                    ServerIdentifier = serverIdentifier,
                    IsDefault = true
                }
            ]
        };

        var json = new LauncherConfigGenerator().Generate(project);
        using var document = JsonDocument.Parse(json);
        var agent = document.RootElement
            .GetProperty("launch")
            .GetProperty("javaAgents")[0];

        Assert.Equal(@"LauncherConfig\Auth\nide8auth.jar", agent.GetProperty("path").GetString());
        Assert.Equal(serverIdentifier, agent.GetProperty("argument").GetString());
        Assert.True(agent.GetProperty("clientMode").GetBoolean());
        var provider = document.RootElement
            .GetProperty("login")
            .GetProperty("allowedProviders")[0];
        Assert.Equal(
            "https://auth.mc-user.com:233/",
            provider.GetProperty("serverUrl").GetString());
        Assert.Contains("\"UnifiedPassport\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LauncherConfig_ContainsAllowedLoginProvidersWithoutCredentials()
    {
        var project = new ProjectConfig
        {
            LoginProviders =
            [
                new()
                {
                    Type = LoginProviderType.Microsoft,
                    DisplayName = "Microsoft 正版登录",
                    IsDefault = true
                },
                new()
                {
                    Type = LoginProviderType.Offline,
                    DisplayName = "离线登录"
                },
                new()
                {
                    Type = LoginProviderType.ThirdPartyPassport,
                    DisplayName = "测试通行证",
                    ServerUrl = new Uri("https://auth.example.test/"),
                    ApiUrl = new Uri("https://auth.example.test/api/"),
                    ClientId = "public-client-id"
                }
            ]
        };

        var json = new LauncherConfigGenerator().Generate(project);
        using var document = JsonDocument.Parse(json);
        var providers = document.RootElement.GetProperty("login").GetProperty("allowedProviders");

        Assert.Equal(3, providers.GetArrayLength());
        Assert.Contains("\"Microsoft\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Offline\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ThirdPartyPassport\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LauncherConfig_ContainsBatchModeAndBothThirdPartyAgentTypes()
    {
        var project = new ProjectConfig
        {
            Launch = new LaunchOptions
            {
                UseBatchFile = true,
                PackagedBatchRelativePath = @"LauncherConfig\launch.bat"
            },
            LoginProviders =
            [
                new()
                {
                    Type = LoginProviderType.CustomAuthenticationServer,
                    DisplayName = "标准 Authlib",
                    ServerUrl = new Uri("https://authlib.example.test/api/yggdrasil")
                },
                new()
                {
                    Type = LoginProviderType.UnifiedPassport,
                    DisplayName = "统一通行证",
                    ServerIdentifier = "0123456789abcdef0123456789abcdef"
                }
            ]
        };

        var json = new LauncherConfigGenerator().Generate(project);
        using var document = JsonDocument.Parse(json);
        var launch = document.RootElement.GetProperty("launch");
        var agents = launch.GetProperty("javaAgents");

        Assert.Equal("GeneratedBatch", launch.GetProperty("mode").GetString());
        Assert.Equal(@"LauncherConfig\launch.bat", launch.GetProperty("batchFile").GetString());
        Assert.Equal(2, agents.GetArrayLength());
        Assert.Contains(
            agents.EnumerateArray(),
            agent => agent.GetProperty("path").GetString() == @"LauncherConfig\Auth\authlib-injector.jar");
        Assert.Contains(
            agents.EnumerateArray(),
            agent => agent.GetProperty("path").GetString() == @"LauncherConfig\Auth\nide8auth.jar");
    }

    [Fact]
    public void LauncherConfig_ContainsSelectedCleanupOptions()
    {
        var project = new ProjectConfig
        {
            Launch = new()
            {
                CleanCachesBeforeLaunch = true,
                CleanLogsBeforeLaunch = false
            }
        };

        var json = new LauncherConfigGenerator().Generate(project);
        using var document = JsonDocument.Parse(json);
        var cleanup = document.RootElement.GetProperty("launch").GetProperty("cleanup");

        Assert.True(cleanup.GetProperty("caches").GetBoolean());
        Assert.False(cleanup.GetProperty("logs").GetBoolean());
    }

    [Fact]
    public void LauncherConfig_ContainsCustomGameWindowTitle()
    {
        var project = new ProjectConfig
        {
            Launch = new()
            {
                GameWindowTitle = "  最后防线 2.2  "
            }
        };

        var json = new LauncherConfigGenerator().Generate(project);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            "最后防线 2.2",
            document.RootElement
                .GetProperty("launch")
                .GetProperty("gameWindowTitle")
                .GetString());
    }

    [Fact]
    public void LauncherConfig_ContainsCustomLauncherAppearance()
    {
        var project = new ProjectConfig
        {
            Basic = new()
            {
                ClientName = "默认客户端名",
                DisplayName = "默认显示名",
                LauncherTitle = "  最后防线启动器  ",
                LauncherBackgroundImagePath =
                    @"E:\素材 文件\登录背景.JPG"
            }
        };

        var json = new LauncherConfigGenerator().Generate(project);
        using var document = JsonDocument.Parse(json);
        var appearance = document.RootElement.GetProperty("appearance");

        Assert.Equal(
            "最后防线启动器",
            appearance.GetProperty("windowTitle").GetString());
        Assert.Equal(
            @"LauncherConfig\Appearance\background.jpg",
            appearance.GetProperty("backgroundImage").GetString());
        Assert.DoesNotContain(@"E:\素材 文件", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "", "Minecraft 登录")]
    [InlineData("客户端", "", "客户端")]
    [InlineData("客户端", "显示名称", "显示名称")]
    public void LauncherConfig_UsesSafeLauncherTitleFallback(
        string clientName,
        string displayName,
        string expected)
    {
        var project = new ProjectConfig
        {
            Basic = new()
            {
                ClientName = clientName,
                DisplayName = displayName
            }
        };

        var json = new LauncherConfigGenerator().Generate(project);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            expected,
            document.RootElement
                .GetProperty("appearance")
                .GetProperty("windowTitle")
                .GetString());
    }

    [Fact]
    public void LauncherConfig_RejectsUnsupportedBackgroundExtension()
    {
        var project = new ProjectConfig
        {
            Basic = new()
            {
                LauncherBackgroundImagePath = @"E:\素材\background.gif"
            }
        };

        Assert.Throws<InvalidDataException>(
            () => new LauncherConfigGenerator().Generate(project));
    }

    [Fact]
    public void LauncherConfig_WritesEmptyLoginUrlsInsteadOfJsonNull()
    {
        var project = new ProjectConfig
        {
            LoginProviders =
            [
                new()
                {
                    Type = LoginProviderType.UnifiedPassport,
                    DisplayName = "统一通行证",
                    ServerUrl = new Uri("https://auth.mc-user.com:233/"),
                    ServerIdentifier = "0123456789abcdef0123456789abcdef"
                }
            ]
        };

        var json = new LauncherConfigGenerator().Generate(project);
        using var document = JsonDocument.Parse(json);
        var provider = document.RootElement
            .GetProperty("login")
            .GetProperty("allowedProviders")[0];

        Assert.Equal(JsonValueKind.String, provider.GetProperty("apiUrl").ValueKind);
        Assert.Equal("", provider.GetProperty("apiUrl").GetString());
    }

    [Fact]
    public void LauncherConfig_PointsJarModeInsideMinecraftDirectory()
    {
        var project = new ProjectConfig
        {
            Client = new()
            {
                LaunchEntryPath = @"versions\最后防线\最后防线.jar"
            },
            Launch = new()
            {
                WorkingDirectory = "."
            }
        };

        var json = new LauncherConfigGenerator().Generate(project);
        using var document = JsonDocument.Parse(json);
        var launch = document.RootElement.GetProperty("launch");

        Assert.Equal(@".minecraft\versions\最后防线\最后防线.jar", launch.GetProperty("entry").GetString());
        Assert.Equal(".minecraft", launch.GetProperty("workingDirectory").GetString());
    }
}
