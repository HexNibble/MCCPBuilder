using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using MCCPBuilder.Core;

namespace MCCPBuilder.Launcher;

internal static class Program
{
    private const string RequiredJavaPath = @"JAVA\bin\java.exe";
    private enum UpdateStartupResult
    {
        Continue,
        Blocked,
        LauncherUpdaterStarted
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (args.Contains("--clear-user-data", StringComparer.OrdinalIgnoreCase))
        {
            return ClearUserData();
        }

        try
        {
            var installationDirectory =
                Path.GetFullPath(AppContext.BaseDirectory);
            var bootstrapPath = ResolveInside(
                installationDirectory,
                @"LauncherConfig\update.json");
            var productionBootstrap =
                ClientUpdateService.LoadBootstrap(bootstrapPath);
            if (productionBootstrap.RequireAdministrator &&
                !IsCurrentProcessAdministrator())
            {
                StartElevatedLauncher(installationDirectory, args);
                return 0;
            }

            var channel = LauncherChannelService.Prepare(
                installationDirectory,
                productionBootstrap);
            var applicationDirectory = channel.RuntimeRoot;
            var bootstrap = channel.Bootstrap;
            if (!args.Contains("--run-generated", StringComparer.OrdinalIgnoreCase))
            {
                var updateResult = RunRequiredUpdate(
                    applicationDirectory,
                    bootstrap);
                if (updateResult == UpdateStartupResult.LauncherUpdaterStarted)
                {
                    return 0;
                }

                if (updateResult == UpdateStartupResult.Blocked)
                {
                    return 2;
                }
            }

            var configPath = ResolveInside(applicationDirectory, @"LauncherConfig\launcher.json");
            var config = LoadConfig(configPath);
            if (!string.Equals(config.Java.Executable, RequiredJavaPath, StringComparison.OrdinalIgnoreCase) ||
                config.Java.AllowSystemJavaFallback)
            {
                throw new InvalidDataException("启动配置必须强制使用 JAVA\\bin\\java.exe，且禁止系统 Java 回退。");
            }

            var javaExecutable = ResolveInside(applicationDirectory, RequiredJavaPath);
            if (!File.Exists(javaExecutable))
            {
                throw new FileNotFoundException("内置 JRE 不完整，缺少 JAVA\\bin\\java.exe。", javaExecutable);
            }

            if (args.Contains("--run-generated", StringComparer.OrdinalIgnoreCase))
            {
                return StartGeneratedJava(applicationDirectory, javaExecutable, config);
            }

            var session = RequestLogin(
                config.Login,
                config.Appearance,
                applicationDirectory,
                channel,
                out var channelSwitchRequested);
            if (channelSwitchRequested)
            {
                RestartLauncher(channel.InstallationRoot);
                return 0;
            }

            if (session is null)
            {
                WriteLog("用户取消登录，未启动 Minecraft。");
                return 0;
            }

            RunCleanup(applicationDirectory, config.Launch.Cleanup);

            if (config.Launch.Mode.Equals("GeneratedBatch", StringComparison.OrdinalIgnoreCase))
            {
                return StartGeneratedJava(
                    applicationDirectory,
                    javaExecutable,
                    config,
                    session);
            }

            if (config.Launch.Mode.Equals("Batch", StringComparison.OrdinalIgnoreCase))
            {
                return StartBatch(applicationDirectory, config, session);
            }

            var gameEntry = ResolveInside(applicationDirectory, config.Launch.Entry);
            if (!File.Exists(gameEntry))
            {
                throw new FileNotFoundException("Minecraft 启动入口不存在。", gameEntry);
            }

            if (!string.Equals(Path.GetExtension(gameEntry), ".jar", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("当前启动入口必须是由内置 JRE 启动的 .jar 文件。");
            }

            var workingDirectory = ResolveInside(applicationDirectory, config.Launch.WorkingDirectory);
            if (!Directory.Exists(workingDirectory))
            {
                throw new DirectoryNotFoundException($"游戏工作目录不存在：{workingDirectory}");
            }

            RepairSelectedLanguageAsset(
                applicationDirectory,
                ResolveGameDirectory(applicationDirectory, config),
                null);

            var startInfo = HiddenProcessStartInfoFactory.Create(
                javaExecutable,
                workingDirectory);
            AddJavaAgents(startInfo, applicationDirectory, config.Launch.JavaAgents);

            foreach (var argument in config.Launch.JvmArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.ArgumentList.Add("-jar");
            startInfo.ArgumentList.Add(gameEntry);
            foreach (var argument in config.Launch.GameArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            SetJavaEnvironment(startInfo, applicationDirectory);

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法创建 Minecraft Java 进程。");
            WriteLog("已使用内置 JAVA\\bin\\java.exe 启动 Minecraft。");
            ApplyCustomGameWindowTitle(process, config.Launch.GameWindowTitle);
            return 0;
        }
        catch (Exception exception)
        {
            WriteLog($"启动失败：{exception.GetType().Name}: {exception.Message}");
            MessageBox.Show(
                $"Minecraft 启动失败：{exception.Message}",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
    }

    private static bool IsCurrentProcessAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(
            WindowsBuiltInRole.Administrator);
    }

    private static void StartElevatedLauncher(
        string applicationDirectory,
        IEnumerable<string> arguments)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            !File.Exists(executable))
        {
            throw new FileNotFoundException(
                "无法定位当前 Launcher.exe，不能申请管理员权限。",
                executable);
        }

        var startInfo = ElevatedProcessStartInfoFactory.Create(
            executable,
            applicationDirectory,
            arguments);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "无法以管理员身份重新启动 Launcher。");
        WriteLog("已请求 UAC，并准备以管理员身份重新启动 Launcher。");
    }

    private static int StartGeneratedJava(
        string applicationDirectory,
        string javaExecutable,
        LauncherRuntimeConfig config,
        LoginSession? session = null)
    {
        if (session is null)
        {
            if (GetCompatibleEnvironmentVariable(
                    "MCCP_SESSION_READY",
                    "MCC_SESSION_READY") != "1")
            {
                throw new InvalidOperationException("启动会话尚未通过登录窗口建立。");
            }

            session = new LoginSession(
                GetRequiredEnvironment("MCCP_USERNAME", "MCC_USERNAME"),
                GetRequiredEnvironment("MCCP_UUID", "MCC_UUID"),
                GetRequiredEnvironment(
                    "MCCP_ACCESS_TOKEN",
                    "MCC_ACCESS_TOKEN"),
                GetRequiredEnvironment(
                    "MCCP_CLIENT_ID",
                    "MCC_CLIENT_ID"),
                GetRequiredEnvironment(
                    "MCCP_USER_TYPE",
                    "MCC_USER_TYPE"),
                GetRequiredEnvironment("MCCP_XUID", "MCC_XUID"));
        }

        var argumentsPath = ResolveInside(
            applicationDirectory,
            config.Launch.GeneratedArgumentsFile);
        if (!File.Exists(argumentsPath))
        {
            throw new FileNotFoundException("缺少自动生成的 Java 参数配置。", argumentsPath);
        }

        var generated = JsonSerializer.Deserialize<GeneratedJavaArgumentsRuntimeConfig>(
            File.ReadAllText(argumentsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("自动生成的 Java 参数配置为空。");
        var workingDirectory = ResolveInside(applicationDirectory, generated.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Minecraft 版本工作目录不存在：{workingDirectory}");
        }

        RepairSelectedLanguageAsset(
            applicationDirectory,
            workingDirectory,
            GetArgumentValue(generated.Arguments, "--assetIndex"));

        var startInfo = HiddenProcessStartInfoFactory.Create(
            javaExecutable,
            workingDirectory);
        AddJavaAgents(startInfo, applicationDirectory, config.Launch.JavaAgents);
        foreach (var argument in generated.Arguments)
        {
            var resolved = ResolveGeneratedArgument(
                argument,
                applicationDirectory,
                session);
            if (resolved.Contains('\r') || resolved.Contains('\n'))
            {
                throw new InvalidDataException("自动生成的 Java 参数包含非法换行。");
            }

            startInfo.ArgumentList.Add(resolved);
        }

        SetJavaEnvironment(startInfo, applicationDirectory);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法创建 Minecraft Java 进程。");
        WriteLog("已绕过 CMD，直接通过自动生成的参数配置启动 Minecraft。");
        ApplyCustomGameWindowTitle(process, config.Launch.GameWindowTitle);
        return 0;
    }

    private static int StartBatch(
        string applicationDirectory,
        LauncherRuntimeConfig config,
        LoginSession session)
    {
        var batchPath = ResolveInside(applicationDirectory, config.Launch.BatchFile);
        if (!File.Exists(batchPath))
        {
            throw new FileNotFoundException("打包后的 BAT 启动文件不存在。", batchPath);
        }

        RepairSelectedLanguageAsset(
            applicationDirectory,
            ResolveGameDirectory(applicationDirectory, config),
            null);

        var commandInterpreter = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var startInfo = HiddenProcessStartInfoFactory.Create(
            commandInterpreter,
            applicationDirectory);
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(batchPath);
        startInfo.Environment["MCCP_SESSION_READY"] = "1";
        startInfo.Environment["MCCP_USERNAME"] = session.Username;
        startInfo.Environment["MCCP_UUID"] = session.Uuid;
        startInfo.Environment["MCCP_ACCESS_TOKEN"] = session.AccessToken;
        startInfo.Environment["MCCP_CLIENT_ID"] = session.ClientId;
        startInfo.Environment["MCCP_XUID"] = session.Xuid;
        startInfo.Environment["MCCP_USER_TYPE"] = session.UserType;
        startInfo.Environment["MCC_SESSION_READY"] = "1";
        startInfo.Environment["MCC_USERNAME"] = session.Username;
        startInfo.Environment["MCC_UUID"] = session.Uuid;
        startInfo.Environment["MCC_ACCESS_TOKEN"] = session.AccessToken;
        startInfo.Environment["MCC_CLIENT_ID"] = session.ClientId;
        startInfo.Environment["MCC_XUID"] = session.Xuid;
        startInfo.Environment["MCC_USER_TYPE"] = session.UserType;
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("无法执行打包后的 BAT。");
        WriteLog("已执行打包后的 BAT 启动文件。");
        return 0;
    }

    private static void AddJavaAgents(
        ProcessStartInfo startInfo,
        string applicationDirectory,
        IEnumerable<JavaAgentRuntimeConfig> agents)
    {
        foreach (var agent in agents)
        {
            var agentPath = ResolveInside(applicationDirectory, agent.Path);
            if (!File.Exists(agentPath))
            {
                throw new FileNotFoundException("登录认证 Agent 不存在。", agentPath);
            }

            if (agent.Argument.Contains('\r') || agent.Argument.Contains('\n'))
            {
                throw new InvalidDataException("登录认证 Agent 参数包含非法换行。");
            }

            var agentArgumentPath = agentPath.Contains('=')
                ? Path.GetRelativePath(startInfo.WorkingDirectory, agentPath)
                : agentPath;
            startInfo.ArgumentList.Add(string.IsNullOrEmpty(agent.Argument)
                ? $"-javaagent:{agentArgumentPath}"
                : $"-javaagent:{agentArgumentPath}={agent.Argument}");
            if (agent.ClientMode)
            {
                startInfo.ArgumentList.Add("-Dnide8auth.client=true");
            }
        }
    }

    private static LoginSession? RequestLogin(
        LoginRuntimeConfig login,
        LauncherAppearanceRuntimeConfig appearance,
        string applicationDirectory,
        LauncherChannelContext channel,
        out bool channelSwitchRequested)
    {
        if (login.AllowedProviders.Count == 0)
        {
            throw new InvalidDataException("启动配置没有允许的登录方式。");
        }

        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        var window = new LoginWindow(
            login.AllowedProviders,
            applicationDirectory,
            appearance,
            channel.InstallationRoot,
            channel.TestChannelAvailable,
            channel.IsTestChannel);
        var accepted = window.ShowDialog() == true;
        channelSwitchRequested = window.ChannelSwitchRequested;
        return accepted ? window.Session : null;
    }

    private static void RestartLauncher(string installationDirectory)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            !File.Exists(executable))
        {
            executable = ResolveInside(
                installationDirectory,
                "Launcher.exe");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = installationDirectory,
            UseShellExecute = true
        });
    }

    private static UpdateStartupResult RunRequiredUpdate(
        string applicationDirectory,
        UpdateBootstrapConfig bootstrap)
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        ProgramLog.Write = WriteLog;
        var window = new UpdateProgressWindow(
            applicationDirectory,
            bootstrap);
        _ = window.ShowDialog();
        if (window.Failure is not null)
        {
            throw new InvalidOperationException(
                "无法完成强制更新检查，已阻止启动。原因：" +
                window.Failure.Message,
                window.Failure);
        }

        if (!window.Succeeded)
        {
            throw new InvalidOperationException(
                "更新检查未成功完成，已阻止启动。");
        }

        if (window.Result?.LauncherUpdate is not null)
        {
            StartLauncherInstaller(
                applicationDirectory,
                window.Result.LauncherInstallerPath,
                window.Result.LauncherUpdate.Version);
            return UpdateStartupResult.LauncherUpdaterStarted;
        }

        var policy = window.Result?.Policy ?? new ClientLaunchPolicy();
        if (policy.ShowMessage || policy.BlockLaunch)
        {
            MessageBox.Show(
                policy.Message,
                string.IsNullOrWhiteSpace(policy.Title)
                    ? "服务器通知"
                    : policy.Title,
                MessageBoxButton.OK,
                policy.BlockLaunch
                    ? MessageBoxImage.Stop
                    : MessageBoxImage.Information);
        }

        if (policy.BlockLaunch)
        {
            WriteLog(
                "服务器启动策略已禁止运行 Minecraft。");
            return UpdateStartupResult.Blocked;
        }

        return UpdateStartupResult.Continue;
    }

    private static void StartLauncherInstaller(
        string applicationDirectory,
        string installerPath,
        string version)
    {
        var installer = Path.GetFullPath(installerPath);
        if (!File.Exists(installer) ||
            !string.Equals(
                Path.GetExtension(installer),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(
                "已下载的启动器安装包不存在或格式无效。",
                installer);
        }

        var installDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(applicationDirectory));
        var startInfo = ElevatedProcessStartInfoFactory.Create(
            installer,
            Path.GetDirectoryName(installer)!,
            [
                "/VERYSILENT",
                "/SUPPRESSMSGBOXES",
                "/NORESTART",
                "/CLOSEAPPLICATIONS",
                $"/DIR={installDirectory}",
                "/MCCPSELFUPDATE=1"
            ]);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "无法启动新版启动器安装程序。");
        WriteLog(
            $"已启动 Launcher {version} 原位升级；" +
            "安装目录和 .minecraft 用户数据将保留。");
    }

    private static string ResolveGeneratedArgument(
        string argument,
        string applicationDirectory,
        LoginSession session)
    {
        var minecraftRoot = Path.Combine(applicationDirectory, ".minecraft");
        return argument
            .Replace("${MCCP_APP_ROOT}", applicationDirectory, StringComparison.Ordinal)
            .Replace("${MCCP_GAME_ROOT}", minecraftRoot, StringComparison.Ordinal)
            .Replace("${MCCP_USERNAME}", session.Username, StringComparison.Ordinal)
            .Replace("${MCCP_UUID}", session.Uuid, StringComparison.Ordinal)
            .Replace("${MCCP_ACCESS_TOKEN}", session.AccessToken, StringComparison.Ordinal)
            .Replace("${MCCP_CLIENT_ID}", session.ClientId, StringComparison.Ordinal)
            .Replace("${MCCP_XUID}", session.Xuid, StringComparison.Ordinal)
            .Replace("${MCCP_USER_TYPE}", session.UserType, StringComparison.Ordinal)
            .Replace("${MCC_APP_ROOT}", applicationDirectory, StringComparison.Ordinal)
            .Replace("${MCC_GAME_ROOT}", minecraftRoot, StringComparison.Ordinal)
            .Replace("${MCC_USERNAME}", session.Username, StringComparison.Ordinal)
            .Replace("${MCC_UUID}", session.Uuid, StringComparison.Ordinal)
            .Replace("${MCC_ACCESS_TOKEN}", session.AccessToken, StringComparison.Ordinal)
            .Replace("${MCC_CLIENT_ID}", session.ClientId, StringComparison.Ordinal)
            .Replace("${MCC_XUID}", session.Xuid, StringComparison.Ordinal)
            .Replace("${MCC_USER_TYPE}", session.UserType, StringComparison.Ordinal);
    }

    private static string GetRequiredEnvironment(
        string currentName,
        string legacyName)
    {
        var value = GetCompatibleEnvironmentVariable(
            currentName,
            legacyName);
        return string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException(
                $"登录会话缺少 {currentName}。")
            : value;
    }

    private static string? GetCompatibleEnvironmentVariable(
        string currentName,
        string legacyName) =>
        Environment.GetEnvironmentVariable(currentName) ??
        Environment.GetEnvironmentVariable(legacyName);

    private static void ApplyCustomGameWindowTitle(Process process, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        WriteLog($"开始应用自定义游戏窗口标题：{title}");
        GameWindowTitleService.ApplyWhileRunning(process, title);
        WriteLog("Minecraft 进程已退出，停止应用自定义游戏窗口标题。");
    }

    private static void SetJavaEnvironment(ProcessStartInfo startInfo, string applicationDirectory)
    {
        var javaHome = ResolveInside(applicationDirectory, "JAVA");
        var javaBin = Path.Combine(javaHome, "bin");
        startInfo.Environment.TryGetValue("PATH", out var inheritedPath);
        startInfo.Environment["JAVA_HOME"] = javaHome;
        startInfo.Environment["JRE_HOME"] = javaHome;
        startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(inheritedPath)
            ? javaBin
            : javaBin + Path.PathSeparator + inheritedPath;
    }

    private static void RepairSelectedLanguageAsset(
        string applicationDirectory,
        string gameDirectory,
        string? assetIndexId)
    {
        var minecraftDirectory = ResolveInside(
            applicationDirectory,
            ".minecraft");
        WriteLog("正在检查 Minecraft 当前语言所需的官方资源。");
        var result = new MinecraftAssetRepairService()
            .EnsureSelectedLanguageAsync(
                minecraftDirectory,
                gameDirectory,
                assetIndexId)
            .GetAwaiter()
            .GetResult();
        WriteLog(result.Diagnostic);
    }

    private static string ResolveGameDirectory(
        string applicationDirectory,
        LauncherRuntimeConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Launch.Entry))
        {
            var entry = ResolveInside(
                applicationDirectory,
                config.Launch.Entry);
            var entryDirectory = Path.GetDirectoryName(entry);
            if (!string.IsNullOrWhiteSpace(entryDirectory) &&
                Directory.Exists(entryDirectory))
            {
                return entryDirectory;
            }
        }

        return ResolveInside(
            applicationDirectory,
            config.Launch.WorkingDirectory);
    }

    private static string? GetArgumentValue(
        IReadOnlyList<string> arguments,
        string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(
                    option,
                    StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static void RunCleanup(
        string applicationDirectory,
        CleanupRuntimeConfig cleanup)
    {
        if (!cleanup.Caches && !cleanup.Logs)
        {
            return;
        }

        var minecraftDirectory = ResolveInside(applicationDirectory, ".minecraft");
        var result = new MinecraftCleanupService().Clean(
            minecraftDirectory,
            cleanup.Caches,
            cleanup.Logs);
        WriteLog(
            $"自动清理完成：目录 {result.DeletedDirectoryCount} 个，文件 {result.DeletedFileCount} 个。");
        foreach (var warning in result.Warnings)
        {
            WriteLog($"自动清理警告：{warning}");
        }
    }

    private static LauncherRuntimeConfig LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("缺少 LauncherConfig\\launcher.json。", configPath);
        }

        var config = JsonSerializer.Deserialize<LauncherRuntimeConfig>(
            File.ReadAllText(configPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return config ?? throw new InvalidDataException("启动配置为空或格式无效。");
    }

    private static string ResolveInside(string applicationDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"配置路径必须是相对路径：{relativePath}");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"配置路径超出安装目录：{relativePath}");
        }

        return candidate;
    }

    private static int ClearUserData()
    {
        try
        {
            var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var loginStore = new SecureLoginStore(applicationDirectory);
            loginStore.Delete();

            var logDirectory = GetLogDirectory(applicationDirectory);
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, true);
            }

            var legacyLogDirectory = GetLogDirectory(
                applicationDirectory,
                "MCCBuilder");
            if (Directory.Exists(legacyLogDirectory))
            {
                Directory.Delete(legacyLogDirectory, true);
            }

            DeleteIfEmpty(Path.GetDirectoryName(loginStore.FilePath));
            DeleteIfEmpty(Path.GetDirectoryName(logDirectory));
            DeleteIfEmpty(Path.GetDirectoryName(legacyLogDirectory));
            DeleteIfEmpty(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MCCPBuilder"));
            DeleteIfEmpty(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MCCBuilder"));
            return 0;
        }
        catch
        {
            // 卸载器通过退出代码判断清理是否成功；此模式不得弹窗或重新创建日志。
            return 1;
        }
    }

    private static string GetLogDirectory(string applicationDirectory)
        => GetLogDirectory(applicationDirectory, "MCCPBuilder");

    private static string GetLogDirectory(
        string applicationDirectory,
        string productDirectoryName)
    {
        var identity = SecureLoginStore.CreateApplicationIdentityHash(applicationDirectory);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            productDirectoryName,
            "LaunchLogs",
            identity[..32]);
    }

    private static void DeleteIfEmpty(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory) &&
            Directory.Exists(directory) &&
            !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            var logDirectory = GetLogDirectory(AppContext.BaseDirectory);
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, $"launcher-{DateTime.Now:yyyyMMdd}.log"),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不得触发系统 Java 回退或改变启动策略。
        }
    }
}

internal sealed class LauncherRuntimeConfig
{
    public LauncherAppearanceRuntimeConfig Appearance { get; set; } = new();
    public JavaRuntimeConfig Java { get; set; } = new();
    public LaunchRuntimeConfig Launch { get; set; } = new();
    public LoginRuntimeConfig Login { get; set; } = new();
}

internal sealed class LauncherAppearanceRuntimeConfig
{
    private string _windowTitle = "";
    private string _backgroundImage = "";

    public string WindowTitle
    {
        get => _windowTitle;
        set => _windowTitle = value ?? "";
    }

    public string BackgroundImage
    {
        get => _backgroundImage;
        set => _backgroundImage = value ?? "";
    }
}

internal sealed class JavaRuntimeConfig
{
    public string Executable { get; set; } = "";
    public bool AllowSystemJavaFallback { get; set; }
}

internal sealed class LaunchRuntimeConfig
{
    public string Mode { get; set; } = "Jar";
    public string BatchFile { get; set; } = "";
    public string GeneratedArgumentsFile { get; set; } = "";
    public string Entry { get; set; } = "";
    public string WorkingDirectory { get; set; } = ".";
    public string GameWindowTitle { get; set; } = "";
    public List<string> JvmArguments { get; set; } = [];
    public List<string> GameArguments { get; set; } = [];
    public CleanupRuntimeConfig Cleanup { get; set; } = new();
    public List<JavaAgentRuntimeConfig> JavaAgents { get; set; } = [];
}

internal sealed class CleanupRuntimeConfig
{
    public bool Caches { get; set; }
    public bool Logs { get; set; }
}

internal sealed class JavaAgentRuntimeConfig
{
    public string Path { get; set; } = "";
    public string Argument { get; set; } = "";
    public bool ClientMode { get; set; }
}

internal sealed class GeneratedJavaArgumentsRuntimeConfig
{
    public string WorkingDirectory { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
}

internal sealed class LoginRuntimeConfig
{
    public List<LoginProviderRuntimeConfig> AllowedProviders { get; set; } = [];
}

internal sealed class LoginProviderRuntimeConfig
{
    private string _type = "";
    private string _displayName = "";
    private string _serverUrl = "";
    private string _apiUrl = "";
    private string _serverIdentifier = "";

    public string Type { get => _type; set => _type = value ?? ""; }
    public string DisplayName { get => _displayName; set => _displayName = value ?? ""; }
    public string ServerUrl { get => _serverUrl; set => _serverUrl = value ?? ""; }
    public string ApiUrl { get => _apiUrl; set => _apiUrl = value ?? ""; }
    public string ServerIdentifier { get => _serverIdentifier; set => _serverIdentifier = value ?? ""; }
    public bool IsDefault { get; set; }
    public bool IsRequired { get; set; }

    public override string ToString()
    {
        var displayName = DisplayName.Trim();
        return string.IsNullOrEmpty(displayName)
            ? Type.Trim()
            : displayName;
    }
}
