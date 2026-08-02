using System.Text.Json;
using MCCPBuilder.Models;

namespace MCCPBuilder.Packaging;

public sealed class LauncherConfigGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> SupportedBackgroundExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp" };

    public string Generate(ProjectConfig project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Java.Mode != JavaMode.Bundled ||
            !project.Java.BundleJava ||
            !project.Java.ForceConfiguredJava)
        {
            throw new InvalidOperationException("当前打包要求必须强制使用内置 JRE。");
        }

        var javaAgents = new List<LauncherJavaAgentConfig>();
        javaAgents.AddRange(project.LoginProviders
            .Where(provider => provider.Type == LoginProviderType.UnifiedPassport)
            .Select(provider => new LauncherJavaAgentConfig(
                @"LauncherConfig\Auth\nide8auth.jar",
                provider.ServerIdentifier,
                true)));
        javaAgents.AddRange(project.LoginProviders
            .Where(provider => provider.Type == LoginProviderType.CustomAuthenticationServer)
            .Select(provider => new LauncherJavaAgentConfig(
                @"LauncherConfig\Auth\authlib-injector.jar",
                provider.ServerUrl?.ToString() ?? "",
                false)));

        var config = new
        {
            schemaVersion = 1,
            appearance = new
            {
                windowTitle = GetLauncherTitle(project),
                backgroundImage = GetPackagedBackgroundImagePath(
                    project.Basic.LauncherBackgroundImagePath)
            },
            java = new
            {
                executable = @"JAVA\bin\java.exe",
                home = "JAVA",
                allowSystemJavaFallback = false,
                requiredArchitecture = project.Java.RequiredArchitecture,
                minimumMajorVersion = project.Java.MinimumMajorVersion,
                maximumMajorVersion = project.Java.MaximumMajorVersion
            },
            launch = new
            {
                mode = "GeneratedBatch",
                batchFile = project.Launch.PackagedBatchRelativePath,
                generatedArgumentsFile = @"LauncherConfig\launch.arguments.json",
                entry = ToPackagedMinecraftPath(project.Client.LaunchEntryPath),
                workingDirectory = ToPackagedMinecraftPath(project.Launch.WorkingDirectory),
                gameWindowTitle = project.Launch.GameWindowTitle.Trim(),
                jvmArguments = project.Launch.JvmArguments,
                gameArguments = project.Launch.GameArguments,
                cleanup = new
                {
                    caches = project.Launch.CleanCachesBeforeLaunch,
                    logs = project.Launch.CleanLogsBeforeLaunch
                },
                javaAgents
            },
            login = new
            {
                allowedProviders = project.LoginProviders.Select(provider => new
                {
                    type = provider.Type.ToString(),
                    provider.DisplayName,
                    serverUrl = GetRuntimeServerUrl(provider),
                    apiUrl = provider.ApiUrl?.ToString() ?? "",
                    provider.ClientId,
                    callbackUrl = provider.CallbackUrl?.ToString(),
                    provider.IsDefault,
                    provider.IsRequired,
                    provider.ServerIdentifier
                })
            }
        };
        return JsonSerializer.Serialize(config, JsonOptions);
    }

    public static string GetPackagedBackgroundImagePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return "";
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!SupportedBackgroundExtensions.Contains(extension))
        {
            throw new InvalidDataException(
                "启动器背景图片只支持 PNG、JPG、JPEG 或 BMP。");
        }

        return $@"LauncherConfig\Appearance\background{extension}";
    }

    private static string GetLauncherTitle(ProjectConfig project)
    {
        var configuredTitle = (project.Basic.LauncherTitle ?? "").Trim();
        if (!string.IsNullOrEmpty(configuredTitle))
        {
            return configuredTitle;
        }

        var displayName = (project.Basic.DisplayName ?? "").Trim();
        if (!string.IsNullOrEmpty(displayName))
        {
            return displayName;
        }

        var clientName = (project.Basic.ClientName ?? "").Trim();
        return string.IsNullOrEmpty(clientName) ? "Minecraft 登录" : clientName;
    }

    private static string? GetRuntimeServerUrl(LoginProviderOptions provider)
    {
        if (provider.Type == LoginProviderType.UnifiedPassport &&
            provider.ServerUrl?.Host.Equals(
                "login.mc-user.com",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return "https://auth.mc-user.com:233/";
        }

        return provider.ServerUrl?.ToString();
    }

    private static string ToPackagedMinecraftPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
        {
            return ".minecraft";
        }

        return Path.Combine(".minecraft", relativePath);
    }

    private sealed record LauncherJavaAgentConfig(string path, string argument, bool clientMode);
}
