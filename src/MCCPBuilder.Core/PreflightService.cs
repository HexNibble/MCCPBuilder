using MCCPBuilder.Models;

namespace MCCPBuilder.Core;

public enum CheckSeverity { Info, Warning, Error }

public sealed record CheckResult(CheckSeverity Severity, string Area, string Message);

public sealed class PreflightService(FileScanService fileScanner, JavaDetectionService javaDetector)
{
    public async Task<IReadOnlyList<CheckResult>> CheckAsync(ProjectConfig project, CancellationToken cancellationToken = default)
    {
        var results = new List<CheckResult>();
        if (string.IsNullOrWhiteSpace(project.Basic.ClientName))
            results.Add(new(CheckSeverity.Error, "基本信息", "客户端名称不能为空。"));
        if (!InputValidator.IsValidVersion(project.Basic.ClientVersion))
            results.Add(new(CheckSeverity.Error, "基本信息", "客户端版本必须使用 x.y.z 格式。"));
        if (!InputValidator.IsValidVersion(project.Basic.LauncherVersion))
            results.Add(new(CheckSeverity.Error, "基本信息", "启动器版本必须使用 x.y.z 格式。"));
        var normalizedOutputName =
            project.Basic.OutputFileName?.EndsWith(
                ".exe",
                StringComparison.OrdinalIgnoreCase) == true
                ? project.Basic.OutputFileName[..^4]
                : project.Basic.OutputFileName;
        if (!InputValidator.IsValidFileName(normalizedOutputName))
            results.Add(new(CheckSeverity.Error, "输出", "输出文件名为空或包含非法字符。"));
        if (!InputValidator.IsValidOptionalLauncherTitle(project.Basic.LauncherTitle))
        {
            results.Add(new(
                CheckSeverity.Error,
                "启动器外观",
                $"启动器标题不能包含控制字符，且最多 {InputValidator.MaximumLauncherTitleLength} 个字符。"));
        }
        AddLauncherBackgroundValidation(
            results,
            project.Basic.LauncherBackgroundImagePath);
        if (!Uri.TryCreate(
                project.Update.ServerBaseUrl,
                UriKind.Absolute,
                out var updateServer))
        {
            results.Add(new(
                CheckSeverity.Error,
                "服务器更新",
                "更新服务器地址无效。"));
        }
        else
        {
            try
            {
                UpdatePublisherService.ValidateServerUri(updateServer);
            }
            catch (InvalidDataException exception)
            {
                results.Add(new(
                    CheckSeverity.Error,
                    "服务器更新",
                    exception.Message));
            }
        }

        try
        {
            _ = ReleaseBundleService.NormalizeProductId(
                project.Update.ProductId);
        }
        catch (InvalidDataException exception)
        {
            results.Add(new(
                CheckSeverity.Error,
                "服务器更新",
                exception.Message));
        }

        if (!project.Update.RequireSuccessfulCheck)
        {
            results.Add(new(
                CheckSeverity.Error,
                "服务器更新",
                "客户端必须在每次启动前成功检查更新。"));
        }
        if (project.Update.DownloadConcurrency is
            < ClientUpdateService.MinDownloadConcurrency or
            > ClientUpdateService.MaxDownloadConcurrency)
        {
            results.Add(new(
                CheckSeverity.Error,
                "服务器更新",
                $"下载线程数必须在 " +
                $"{ClientUpdateService.MinDownloadConcurrency} 到 " +
                $"{ClientUpdateService.MaxDownloadConcurrency} 之间。"));
        }
        if ((project.Update.ServerNoticeTitle ?? "").Length > 128 ||
            (project.Update.ServerNoticeMessage ?? "").Length > 4000 ||
            ((project.Update.ShowServerNotice ||
              project.Update.BlockGameLaunch) &&
             string.IsNullOrWhiteSpace(
                 project.Update.ServerNoticeMessage)))
        {
            results.Add(new(
                CheckSeverity.Error,
                "服务器公告",
                "启用公告或禁止启动时必须填写正文；标题最多128字符，正文最多4000字符。"));
        }
        var innoCompiler = new InnoSetupLocator().FindCompiler(
            project.Output.InnoCompilerPath);
        results.Add(innoCompiler is null
            ? new(
                CheckSeverity.Error,
                "安装包",
                "未找到 Inno Setup 6 的 ISCC.exe；无法生成最终安装包 EXE。")
            : new(
                CheckSeverity.Info,
                "安装包",
                $"已找到 Inno Setup 编译器：{innoCompiler}"));
        if (project.Launch.AutoJoinServer &&
            !InputValidator.IsValidMinecraftServerAddress(project.Launch.ServerAddress))
        {
            results.Add(new(
                CheckSeverity.Error,
                "启动",
                "自动进入服务器已启用，但服务器地址无效；请填写域名、IP或主机:端口，不要填写 http:// 或 https://。"));
        }
        if (!InputValidator.IsValidOptionalGameWindowTitle(project.Launch.GameWindowTitle))
        {
            results.Add(new(
                CheckSeverity.Error,
                "启动",
                $"自定义游戏标题不能包含控制字符，且最多 {GameWindowTitleService.MaximumTitleLength} 个字符。"));
        }
        if (!project.Client.DownloadMinecraftAndForgeFromOfficialSources)
        {
            results.Add(new(
                CheckSeverity.Error,
                "游戏下载",
                "当前版本仅支持从 Mojang 与 Forge 官方地址下载 Minecraft/Forge，不允许把原版游戏文件上传到更新服务器。"));
        }
        if (project.Client.ResourceDelivery is ResourceDeliveryMode.Modrinth or ResourceDeliveryMode.CurseForge)
        {
            var expectedExtension = project.Client.ResourceDelivery == ResourceDeliveryMode.Modrinth
                ? ".mrpack"
                : ".zip";
            if (!File.Exists(project.Client.ResourcePackagePath) ||
                !Path.GetExtension(project.Client.ResourcePackagePath).Equals(
                    expectedExtension,
                    StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new(
                    CheckSeverity.Error,
                    "内容来源",
                    $"{project.Client.ResourceDelivery} 模式必须选择有效的 {expectedExtension} 整合包。"));
            }
        }
        if (project.Launch.CustomizeForgeMcpBranding)
        {
            if (project.Client.DownloadMinecraftAndForgeFromOfficialSources)
            {
                results.Add(new(
                    CheckSeverity.Warning,
                    "Forge 显示",
                    "Forge JAR 将先从官方地址下载，再仅修改最终用户本机副本；服务器发布包仍不包含 Forge JAR。"));
            }
            if (!InputValidator.IsValidForgeMcpBrandingText(project.Launch.ForgeMcpBrandingText))
            {
                results.Add(new(
                    CheckSeverity.Error,
                    "Forge 显示",
                    "自定义 Forge 标识不能为空、不能包含控制字符，且最多 48 个字符。"));
            }
            else
            {
                try
                {
                    var relativePath = ForgeBrandingService.ResolveForgeUniversalRelativePath(project);
                    var sourceJar = Path.Combine(project.Client.MinecraftRootDirectory, relativePath);
                    if (!File.Exists(sourceJar))
                    {
                        results.Add(new(
                            CheckSeverity.Error,
                            "Forge 显示",
                            $"找不到需要修改的 Forge Universal JAR：{relativePath}"));
                    }
                    else
                    {
                        results.Add(new(
                            CheckSeverity.Warning,
                            "Forge 显示",
                            "自定义 Forge 标识会修改打包副本中的 Forge JAR 清单及 BrandingControl 类，可能不兼容会校验 Forge 文件哈希的反作弊。"));
                    }
                }
                catch (InvalidDataException exception)
                {
                    results.Add(new(CheckSeverity.Error, "Forge 显示", exception.Message));
                }
            }
        }
        AddIconValidation(results, "主程序图标", project.Basic.ApplicationIconPath);
        AddIconValidation(results, "安装包图标", project.Basic.InstallerIconPath);
        if (!Directory.Exists(project.Client.SourceDirectory))
        {
            results.Add(new(CheckSeverity.Error, "客户端", "客户端源目录不存在。"));
            return results;
        }

        if (string.IsNullOrWhiteSpace(project.Client.MinecraftRootDirectory) ||
            !Directory.Exists(project.Client.MinecraftRootDirectory))
        {
            results.Add(new(CheckSeverity.Error, "版本隔离", "未识别有效的 .minecraft 根目录。"));
        }
        else if (string.IsNullOrWhiteSpace(project.Client.VersionDirectory) ||
                 !Directory.Exists(project.Client.VersionDirectory))
        {
            results.Add(new(CheckSeverity.Error, "版本隔离", "请明确选择 versions 下的具体版本隔离目录，不能只选择 versions 父目录。"));
        }
        else
        {
            var expectedVersionsRoot = Path.Combine(project.Client.MinecraftRootDirectory, "versions");
            if (!InputValidator.IsPathInside(expectedVersionsRoot, project.Client.VersionDirectory))
            {
                results.Add(new(CheckSeverity.Error, "版本隔离", "版本隔离目录不在所选 .minecraft\\versions 中。"));
            }
        }

        FileScanResult scan;
        try
        {
            scan = await fileScanner.ScanAsync(project.Client, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            results.Add(new(CheckSeverity.Error, "客户端", $"无法扫描客户端目录：{exception.Message}"));
            return results;
        }

        if (scan.IncludedFiles.Count == 0)
            results.Add(new(CheckSeverity.Error, "客户端", "没有任何文件会进入安装包。"));
        foreach (var error in scan.Errors)
            results.Add(new(CheckSeverity.Error, "文件读取", error));
        foreach (var relativePath in scan.ExcludedFiles.Where(FileScanService.IsMandatorySensitiveFile))
            results.Add(new(CheckSeverity.Warning, "隐私", $"已自动排除敏感登录或用户身份数据：{relativePath}"));
        foreach (var file in scan.IncludedFiles.Where(file => Path.Combine(project.Client.SourceDirectory, file.RelativePath).Length >= 240))
            results.Add(new(CheckSeverity.Warning, "路径", $"路径可能过长：{file.RelativePath}"));

        foreach (var diagnostic in JvmFileReferenceValidator.Validate(project, scan))
        {
            results.Add(new(
                CheckSeverity.Error,
                "JVM 文件",
                diagnostic));
        }

        if (!string.IsNullOrWhiteSpace(project.Client.LaunchEntryPath))
        {
            var entryPath = Path.GetFullPath(Path.Combine(project.Client.SourceDirectory, project.Client.LaunchEntryPath));
            if (!InputValidator.IsPathInside(project.Client.SourceDirectory, entryPath) || !File.Exists(entryPath))
                results.Add(new(CheckSeverity.Error, "启动", "启动入口不存在或超出客户端目录。"));
        }
        else
        {
            results.Add(new(CheckSeverity.Warning, "启动", "尚未配置客户端启动入口。"));
        }

        if (!string.IsNullOrWhiteSpace(project.Client.LaunchEntryPath) &&
            !project.Launch.UseBatchFile &&
            !string.Equals(Path.GetExtension(project.Client.LaunchEntryPath), ".jar", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new(CheckSeverity.Error, "启动", "强制内置 JRE 模式下，启动入口必须是 .jar 文件。"));
        }

        try
        {
            _ = MinecraftLaunchProfileService.ResolveManifest(project);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            results.Add(new(CheckSeverity.Error, "启动", exception.Message));
        }

        if (project.LoginProviders.Count == 0)
        {
            results.Add(new(CheckSeverity.Error, "登录", "至少允许一种登录方式。"));
        }

        foreach (var provider in project.LoginProviders.Where(provider =>
                     provider.Type is LoginProviderType.CustomAuthenticationServer
                         or LoginProviderType.ThirdPartyPassport
                         or LoginProviderType.UnifiedPassport))
        {
            if (string.IsNullOrWhiteSpace(provider.DisplayName))
            {
                results.Add(new(CheckSeverity.Error, "登录", "第三方登录必须填写显示名称。"));
            }

            if (provider.ServerUrl is null && provider.ApiUrl is null)
            {
                results.Add(new(CheckSeverity.Error, "登录", $"第三方登录“{provider.DisplayName}”必须配置服务器或 API 地址。"));
            }

            if (provider.SecretPlaceholders.Values.Any(value =>
                    !string.IsNullOrWhiteSpace(value) &&
                    !value.StartsWith("${", StringComparison.Ordinal)))
            {
                results.Add(new(CheckSeverity.Error, "登录", $"第三方登录“{provider.DisplayName}”包含非占位符凭据，禁止写入项目。"));
            }

            if (provider.Type == LoginProviderType.UnifiedPassport)
            {
                if (!File.Exists(provider.AuthenticationAgentPath) ||
                    !string.Equals(Path.GetExtension(provider.AuthenticationAgentPath), ".jar", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new(CheckSeverity.Error, "登录", "统一通行证必须选择有效的 nide8auth.jar。"));
                }

                if (string.IsNullOrWhiteSpace(provider.ServerIdentifier) ||
                    provider.ServerIdentifier.Length is < 8 or > 128 ||
                    provider.ServerIdentifier.Any(character =>
                        !char.IsLetterOrDigit(character) && character is not '_' and not '-'))
                {
                    results.Add(new(CheckSeverity.Error, "登录", "统一通行证服务器标识格式无效。"));
                }

                if (provider.ServerUrl is null ||
                    provider.ServerUrl.Scheme != Uri.UriSchemeHttps)
                {
                    results.Add(new(CheckSeverity.Error, "登录", "统一通行证配置服务必须使用 HTTPS。"));
                }
            }

            if (provider.Type == LoginProviderType.CustomAuthenticationServer)
            {
                if (!File.Exists(provider.AuthenticationAgentPath) ||
                    !string.Equals(Path.GetExtension(provider.AuthenticationAgentPath), ".jar", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new(CheckSeverity.Error, "登录", "标准 Authlib Injector 必须选择有效的 authlib-injector.jar。"));
                }

                if (provider.ServerUrl is null ||
                    provider.ServerUrl.Scheme != Uri.UriSchemeHttps ||
                    provider.ServerUrl.ToString().IndexOfAny(['&', '|', '<', '>', '^', '%', '!', '"', '\r', '\n']) >= 0)
                {
                    results.Add(new(CheckSeverity.Error, "登录", "Authlib Injector API 地址必须是安全的 HTTPS URL。"));
                }
            }
        }

        if (project.Java.Mode != JavaMode.Bundled ||
            !project.Java.BundleJava ||
            !project.Java.ForceConfiguredJava)
        {
            results.Add(new(CheckSeverity.Error, "Java", "必须选择并强制使用内置 JRE ZIP，禁止回退系统 Java。"));
            return results;
        }

        var java = await javaDetector.ValidateAsync(project.Java, cancellationToken);
        results.Add(new(java.IsValid ? CheckSeverity.Info : CheckSeverity.Error, "Java", java.Diagnostic));
        if (results.All(result => result.Severity != CheckSeverity.Error))
            results.Add(new(CheckSeverity.Info, "检查", $"检查完成，将包含 {scan.IncludedFiles.Count} 个文件。"));
        return results;
    }

    private static void AddIconValidation(List<CheckResult> results, string name, string path)
    {
        var error = ExecutableIconService.ValidateIcon(path);
        if (error is not null)
        {
            results.Add(new(CheckSeverity.Error, "图标", $"{name}：{error}"));
        }
    }

    private static void AddLauncherBackgroundValidation(
        List<CheckResult> results,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!InputValidator.IsSupportedLauncherBackgroundImagePath(path))
        {
            results.Add(new(
                CheckSeverity.Error,
                "启动器外观",
                "启动器背景图片只支持 PNG、JPG、JPEG 或 BMP。"));
            return;
        }

        if (!File.Exists(path))
        {
            results.Add(new(
                CheckSeverity.Error,
                "启动器外观",
                "启动器背景图片不存在或无法访问。"));
            return;
        }

        try
        {
            const long maximumBytes = 50L * 1024 * 1024;
            if (new FileInfo(path).Length > maximumBytes)
            {
                results.Add(new(
                    CheckSeverity.Error,
                    "启动器外观",
                    "启动器背景图片不能超过 50 MB。"));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            results.Add(new(
                CheckSeverity.Error,
                "启动器外观",
                $"无法读取启动器背景图片：{exception.Message}"));
        }
    }
}
