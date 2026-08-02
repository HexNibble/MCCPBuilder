using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MCCPBuilder.Core;
using MCCPBuilder.Models;
using MCCPBuilder.Packaging;
using Microsoft.Win32;

namespace MCCPBuilder.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ProjectFileService _projectFiles = new();
    private readonly FileScanService _fileScanner = new();
    private readonly ClientPayloadService _clientPayload;
    private readonly MinecraftLayoutService _minecraftLayout = new();
    private readonly JavaDetectionService _javaDetector = new();
    private readonly JavaArchiveService _javaArchive = new();
    private readonly BatchLaunchService _batchLaunch = new();
    private readonly MinecraftLaunchProfileService _minecraftLaunchProfile = new();
    private readonly ForgeBrandingService _forgeBranding = new();
    private readonly LauncherPublisherService _launcherPublisher = new();
    private readonly InnoScriptGenerator _scriptGenerator = new();
    private readonly LauncherConfigGenerator _launcherConfigGenerator = new();
    private readonly BuildArtifactService _buildArtifacts = new();
    private readonly ReleaseBundleService _releaseBundles = new();
    private readonly UpdatePublisherService _updatePublisher = new();
    private readonly ResourcePackageService _resourcePackages = new();
    private readonly InnoSetupLocator _innoSetupLocator = new();
    private ProjectConfig _project = new();
    private string? _projectFilePath;
    private string _statusText = "就绪";
    private string _logText = "";
    private string _publisherKeyPath = "";
    private string _latestReleaseArchivePath = "";
    private string _latestTestReleaseArchivePath = "";
    private string _latestInstallerPath = "";
    private string _operationActivity = "等待操作";
    private double _operationProgress;
    private bool _isOperationProgressIndeterminate;
    private CancellationTokenSource? _operationCancellation;
    private string _curseForgeApiKey = "";

    public MainWindow()
    {
        _clientPayload = new(_fileScanner);
        ApplyDetectedInnoCompiler(_project);
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProjectConfig Project
    {
        get => _project;
        private set
        {
            _project = value;
            ApplyDetectedInnoCompiler(_project);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IncludeRulesText));
            OnPropertyChanged(nameof(ExcludeRulesText));
            OnPropertyChanged(nameof(GameArgumentsText));
            OnPropertyChanged(nameof(JvmArgumentsText));
            OnPropertyChanged(nameof(MicrosoftLoginEnabled));
            OnPropertyChanged(nameof(OfflineLoginEnabled));
            OnPropertyChanged(nameof(AuthlibLoginEnabled));
            OnPropertyChanged(nameof(AuthlibDisplayName));
            OnPropertyChanged(nameof(AuthlibServerUrl));
            OnPropertyChanged(nameof(AuthlibAgentPath));
            OnPropertyChanged(nameof(ThirdPartyLoginEnabled));
            OnPropertyChanged(nameof(ThirdPartyDisplayName));
            OnPropertyChanged(nameof(ThirdPartyServerUrl));
            OnPropertyChanged(nameof(ThirdPartyApiUrl));
            OnPropertyChanged(nameof(ThirdPartyClientId));
            OnPropertyChanged(nameof(UnifiedPassportAgentPath));
            OnPropertyChanged(nameof(UnifiedPassportServerIdentifier));
            OnPropertyChanged(nameof(IsModrinthDelivery));
            OnPropertyChanged(nameof(IsCurseForgeDelivery));
            OnPropertyChanged(nameof(IsCustomServerDelivery));
        }
    }

    public ObservableCollection<CheckResult> CheckResults { get; } = [];
    public IReadOnlyList<int> DownloadConcurrencyOptions { get; } =
        [1, 2, 4, 8, 16, 32, 64, 128, 200];

    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string LogText { get => _logText; private set => SetField(ref _logText, value); }
    public string OperationActivity
    {
        get => _operationActivity;
        private set => SetField(ref _operationActivity, value);
    }
    public double OperationProgress
    {
        get => _operationProgress;
        private set => SetField(ref _operationProgress, value);
    }
    public bool IsOperationProgressIndeterminate
    {
        get => _isOperationProgressIndeterminate;
        private set => SetField(
            ref _isOperationProgressIndeterminate,
            value);
    }
    public string PublisherKeyPath
    {
        get => _publisherKeyPath;
        set => SetField(ref _publisherKeyPath, value);
    }
    public string LatestReleaseArchivePath
    {
        get => _latestReleaseArchivePath;
        private set => SetField(ref _latestReleaseArchivePath, value);
    }
    public string LatestTestReleaseArchivePath
    {
        get => _latestTestReleaseArchivePath;
        private set => SetField(ref _latestTestReleaseArchivePath, value);
    }
    public string LatestInstallerPath
    {
        get => _latestInstallerPath;
        private set => SetField(ref _latestInstallerPath, value);
    }

    public string IncludeRulesText
    {
        get => string.Join(Environment.NewLine, Project.Client.IncludeRules);
        set => Project.Client.IncludeRules = SplitLines(value);
    }

    public string ExcludeRulesText
    {
        get => string.Join(Environment.NewLine, Project.Client.ExcludeRules);
        set => Project.Client.ExcludeRules = SplitLines(value);
    }

    public string GameArgumentsText
    {
        get => string.Join(Environment.NewLine, Project.Launch.GameArguments);
        set => Project.Launch.GameArguments = SplitLines(value);
    }

    public string JvmArgumentsText
    {
        get => string.Join(Environment.NewLine, Project.Launch.JvmArguments);
        set => Project.Launch.JvmArguments = SplitLines(value);
    }

    public bool IsModrinthDelivery
    {
        get => Project.Client.ResourceDelivery == ResourceDeliveryMode.Modrinth;
        set { if (value) SetResourceDelivery(ResourceDeliveryMode.Modrinth); }
    }

    public bool IsCurseForgeDelivery
    {
        get => Project.Client.ResourceDelivery == ResourceDeliveryMode.CurseForge;
        set { if (value) SetResourceDelivery(ResourceDeliveryMode.CurseForge); }
    }

    public bool IsCustomServerDelivery
    {
        get => Project.Client.ResourceDelivery == ResourceDeliveryMode.CustomServer;
        set { if (value) SetResourceDelivery(ResourceDeliveryMode.CustomServer); }
    }

    public bool MicrosoftLoginEnabled
    {
        get => HasLoginProvider(LoginProviderType.Microsoft);
        set => SetLoginProviderEnabled(LoginProviderType.Microsoft, value, "Microsoft 正版登录");
    }

    public bool OfflineLoginEnabled
    {
        get => HasLoginProvider(LoginProviderType.Offline);
        set => SetLoginProviderEnabled(LoginProviderType.Offline, value, "离线登录");
    }

    public bool AuthlibLoginEnabled
    {
        get => HasLoginProvider(LoginProviderType.CustomAuthenticationServer);
        set
        {
            SetLoginProviderEnabled(LoginProviderType.CustomAuthenticationServer, value, "Authlib Injector");
            OnPropertyChanged(nameof(AuthlibDisplayName));
            OnPropertyChanged(nameof(AuthlibServerUrl));
            OnPropertyChanged(nameof(AuthlibAgentPath));
        }
    }

    public string AuthlibDisplayName
    {
        get => GetLoginProvider(LoginProviderType.CustomAuthenticationServer)?.DisplayName ?? "";
        set => GetOrCreateAuthlib().DisplayName = value;
    }

    public string AuthlibServerUrl
    {
        get => GetLoginProvider(LoginProviderType.CustomAuthenticationServer)?.ServerUrl?.ToString() ?? "";
        set => GetOrCreateAuthlib().ServerUrl = ParseAbsoluteUri(value);
    }

    public string AuthlibAgentPath
    {
        get => GetLoginProvider(LoginProviderType.CustomAuthenticationServer)?.AuthenticationAgentPath ?? "";
        set => GetOrCreateAuthlib().AuthenticationAgentPath = value;
    }

    public bool ThirdPartyLoginEnabled
    {
        get => HasLoginProvider(LoginProviderType.UnifiedPassport);
        set
        {
            SetLoginProviderEnabled(LoginProviderType.UnifiedPassport, value, "统一通行证");
            OnPropertyChanged(nameof(ThirdPartyDisplayName));
            OnPropertyChanged(nameof(ThirdPartyServerUrl));
            OnPropertyChanged(nameof(ThirdPartyApiUrl));
            OnPropertyChanged(nameof(ThirdPartyClientId));
            OnPropertyChanged(nameof(UnifiedPassportAgentPath));
            OnPropertyChanged(nameof(UnifiedPassportServerIdentifier));
        }
    }

    public string ThirdPartyDisplayName
    {
        get => GetLoginProvider(LoginProviderType.UnifiedPassport)?.DisplayName ?? "";
        set => GetOrCreateThirdParty().DisplayName = value;
    }

    public string ThirdPartyServerUrl
    {
        get => GetLoginProvider(LoginProviderType.UnifiedPassport)?.ServerUrl?.ToString() ?? "";
        set
        {
            var address = ParseAbsoluteUri(value);
            GetOrCreateThirdParty().ServerUrl =
                address?.Host.Equals("login.mc-user.com", StringComparison.OrdinalIgnoreCase) == true
                    ? new Uri("https://auth.mc-user.com:233/")
                    : address;
        }
    }

    public string ThirdPartyApiUrl
    {
        get => GetLoginProvider(LoginProviderType.UnifiedPassport)?.ApiUrl?.ToString() ?? "";
        set => GetOrCreateThirdParty().ApiUrl = ParseAbsoluteUri(value);
    }

    public string ThirdPartyClientId
    {
        get => GetLoginProvider(LoginProviderType.UnifiedPassport)?.ClientId ?? "";
        set => GetOrCreateThirdParty().ClientId = value;
    }

    public string UnifiedPassportAgentPath
    {
        get => GetLoginProvider(LoginProviderType.UnifiedPassport)?.AuthenticationAgentPath ?? "";
        set => GetOrCreateThirdParty().AuthenticationAgentPath = value;
    }

    public string UnifiedPassportServerIdentifier
    {
        get => GetLoginProvider(LoginProviderType.UnifiedPassport)?.ServerIdentifier ?? "";
        set => GetOrCreateThirdParty().ServerIdentifier = value.Trim();
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        Project = new();
        _projectFilePath = null;
        CheckResults.Clear();
        Log("已创建新项目。");
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "MCCPBuilder 项目 (*.mccpproject)|*.mccpproject"
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync(async token =>
        {
            Project = await _projectFiles.LoadAsync(dialog.FileName, token);
            _projectFilePath = dialog.FileName;
            Log($"已打开项目：{dialog.FileName}");
        }, "正在打开项目配置…");
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_projectFilePath is null)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "MCCPBuilder 项目 (*.mccpproject)|*.mccpproject",
                FileName = "project.mccpproject",
                AddExtension = true,
                DefaultExt = ".mccpproject"
            };
            if (dialog.ShowDialog(this) != true) return;
            _projectFilePath = dialog.FileName;
        }

        await RunOperationAsync(async token =>
        {
            await _projectFiles.SaveAsync(Project, _projectFilePath, token);
            Log($"已保存项目：{_projectFilePath}");
        }, "正在保存项目配置…");
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 .minecraft、versions、具体版本隔离目录或包含 .minecraft 的客户端目录",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            var layout = _minecraftLayout.Detect(dialog.FolderName);
            if (!layout.IsRecognized)
            {
                MessageBox.Show(this, layout.Diagnostic, "无法识别 Minecraft 目录", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MinecraftLayoutService.Apply(Project.Client, layout);
            OnPropertyChanged(nameof(Project));
            Log(layout.Diagnostic);
        }
    }

    private void BrowseVersion_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Directory.Exists(Project.Client.VersionDirectory)
            ? Project.Client.VersionDirectory
            : Directory.Exists(Project.Client.MinecraftRootDirectory)
                ? Path.Combine(Project.Client.MinecraftRootDirectory, "versions")
                : "";
        var dialog = new OpenFolderDialog
        {
            Title = "选择 versions 下的具体版本隔离目录",
            InitialDirectory = initialDirectory,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            var layout = _minecraftLayout.Detect(dialog.FolderName);
            if (!layout.IsRecognized || layout.VersionDirectory is null)
            {
                MessageBox.Show(this, "请选择 versions 下的具体版本目录，例如 versions\\最后防线。", "版本目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MinecraftLayoutService.Apply(Project.Client, layout);
            OnPropertyChanged(nameof(Project));
            Log(layout.Diagnostic);
        }
    }

    private void BrowseResourcePackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Modrinth .mrpack 或 CurseForge 整合包 ZIP",
            Filter = "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            Project.Client.ResourcePackagePath = dialog.FileName;
            OnPropertyChanged(nameof(Project));
        }
    }

    private void CurseForgeApiKey_Changed(object sender, RoutedEventArgs e)
    {
        _curseForgeApiKey = ((PasswordBox)sender).Password;
    }

    private void BrowseJavaArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要随客户端打包的 JRE ZIP",
            Filter = "JRE ZIP 压缩文件 (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            Project.Java.Mode = JavaMode.Bundled;
            Project.Java.JavaArchivePath = dialog.FileName;
            Project.Java.BundleJava = true;
            Project.Java.ForceConfiguredJava = true;
            OnPropertyChanged(nameof(Project));
            Log($"已选择内置 JRE ZIP：{dialog.FileName}");
        }
    }

    private void BrowseInnoCompiler_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Inno Setup 6 编译器 ISCC.exe",
            Filter = "Inno Setup 编译器 (ISCC.exe)|ISCC.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        Project.Output.InnoCompilerPath = dialog.FileName;
        OnPropertyChanged(nameof(Project));
        Log($"已选择 Inno Setup 编译器：{dialog.FileName}");
    }

    private void BrowseOutputDirectory_Click(
        object sender,
        RoutedEventArgs e)
    {
        var currentDirectory = "";
        try
        {
            currentDirectory = ResolveOutputRoot();
        }
        catch (Exception)
        {
            // 当前文本无效时仍允许用户重新选择目录。
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择独立构建输出目录",
            Multiselect = false,
            InitialDirectory = Directory.Exists(currentDirectory)
                ? currentDirectory
                : GetExecutableDirectory()
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        Project.Output.OutputDirectory = dialog.FolderName;
        OnPropertyChanged(nameof(Project));
        Log($"已选择独立输出目录：{dialog.FolderName}");
    }

    private void BrowsePublisherKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择从服务器通过 SSH 下载的发布密钥文件",
            Filter = "MCCP 发布密钥 (*.key)|*.key|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _ = UpdatePublisherService.ReadKeyFile(dialog.FileName);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "发布密钥无效",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        PublisherKeyPath = dialog.FileName;
        Log("已载入发布密钥文件。密钥内容不会保存到项目或日志。");
    }

    private void BrowseApplicationIcon_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectIcon("选择最终 Launcher.exe 的图标");
        if (path is null) return;
        Project.Basic.ApplicationIconPath = path;
        OnPropertyChanged(nameof(Project));
        Log($"已选择主程序 EXE 图标：{path}");
    }

    private void BrowseInstallerIcon_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectIcon("选择安装包 EXE 的图标");
        if (path is null) return;
        Project.Basic.InstallerIconPath = path;
        OnPropertyChanged(nameof(Project));
        Log($"已选择安装包 EXE 图标：{path}");
    }

    private void BrowseLauncherBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择最终 Launcher 的背景图片",
            Filter = "支持的图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        Project.Basic.LauncherBackgroundImagePath = dialog.FileName;
        OnPropertyChanged(nameof(Project));
        Log($"已选择启动器背景图片：{dialog.FileName}");
    }

    private string? SelectIcon(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Windows 图标 (*.ico)|*.ico",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void BrowseBatchFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Minecraft 启动 BAT",
            Filter = "Windows 批处理文件 (*.bat)|*.bat",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            Project.Launch.UseBatchFile = true;
            Project.Launch.BatchFilePath = dialog.FileName;
            OnPropertyChanged(nameof(Project));
            Log($"已选择 BAT 启动文件：{dialog.FileName}");
        }
    }

    private void BrowseAuthlibAgent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择标准 authlib-injector.jar",
            Filter = "Java Agent (*.jar)|*.jar",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            AuthlibLoginEnabled = true;
            AuthlibAgentPath = dialog.FileName;
            OnPropertyChanged(nameof(AuthlibAgentPath));
            Log($"已选择 Authlib Injector Agent：{dialog.FileName}");
        }
    }

    private void BrowseUnifiedPassportAgent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择统一通行证 nide8auth.jar",
            Filter = "Java Agent (*.jar)|*.jar",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            ThirdPartyLoginEnabled = true;
            UnifiedPassportAgentPath = dialog.FileName;
            OnPropertyChanged(nameof(UnifiedPassportAgentPath));
            Log($"已选择统一通行证 Agent：{dialog.FileName}");
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(async token =>
        {
            var result = await _fileScanner.ScanAsync(Project.Client, cancellationToken: token);
            Log($"扫描完成：包含 {result.IncludedFiles.Count}，排除 {result.ExcludedFiles.Count}，错误 {result.Errors.Count}。");
        }, "正在扫描客户端文件…");

    private async void DetectJava_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(async token =>
        {
            var result = await _javaDetector.ValidateAsync(Project.Java, token);
            Log($"Java 检测：{result.Diagnostic}，路径：{result.ExecutablePath}");
        }, "正在检测 Java 环境…");

    private async void Preflight_Click(object sender, RoutedEventArgs e) => await RunPreflightAsync();

    private async Task<bool> RunPreflightAsync()
    {
        var succeeded = false;
        await RunOperationAsync(async token =>
        {
            CheckResults.Clear();
            var service = new PreflightService(_fileScanner, _javaDetector);
            var results = (await service.CheckAsync(Project, token))
                .ToList();
            try
            {
                results.Add(new(
                    CheckSeverity.Info,
                    "输出",
                    $"构建输出目录：{ResolveOutputRoot()}"));
            }
            catch (Exception exception)
                when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                results.Add(new(
                    CheckSeverity.Error,
                    "输出",
                    $"输出目录无效：{exception.Message}"));
            }

            foreach (var result in results) CheckResults.Add(result);
            var errors = results.Count(result => result.Severity == CheckSeverity.Error);
            var warnings = results.Count(result => result.Severity == CheckSeverity.Warning);
            Log($"生成前检查完成：错误 {errors}，警告 {warnings}。");
            succeeded = errors == 0;
        }, "正在执行生成前检查…");
        return succeeded;
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (!await RunPreflightAsync()) return;
        await RunOperationAsync(async token =>
        {
            var outputRoot = ResolveOutputRoot();
            Directory.CreateDirectory(outputRoot);
            var buildLog = new BuildLogWriter(outputRoot, Project);
            var buildSucceeded = false;
            var installerSource = Path.Combine(outputRoot, "InstallerSource");
            Directory.CreateDirectory(installerSource);
            var scriptPath = Path.Combine(installerSource, "setup.iss");
            var payloadPath = Path.Combine(outputRoot, "ClientPayload");
            var bootstrapPayloadPath = Path.Combine(
                outputRoot,
                "BootstrapPayload");
            var stagingPayload = Path.Combine(outputRoot, $".ClientPayload.{Guid.NewGuid():N}.tmp");

            void Report(string message)
            {
                OperationActivity = message;
                Log(message);
                buildLog.Info(message);
            }

            try
            {
                try
                {
                    var copy = await _clientPayload.CopyClientAsync(
                        Project.Client,
                        stagingPayload,
                        cancellationToken: token);
                    Report(
                        $"客户端 Payload 已复制：{copy.FileCount} 个文件，" +
                        $"{copy.TotalBytes / 1024d / 1024d:F2} MB，" +
                        $"排除 {copy.ExcludedFileCount} 个文件。");

                    if (Project.Launch.CustomizeForgeMcpBranding &&
                        !Project.Client.DownloadMinecraftAndForgeFromOfficialSources)
                    {
                        var brandedJar = await _forgeBranding.ApplyAsync(
                            Project,
                            stagingPayload,
                            token);
                        Report(
                            $"已将打包副本的整行 Forge 标识改为" +
                            $"“{Project.Launch.ForgeMcpBrandingText}”：{brandedJar}");
                    }
                    else if (Project.Launch.CustomizeForgeMcpBranding)
                    {
                        Report("Forge 标识将在客户端完成官方下载后，仅修改本机副本。");
                    }

                    var inspection = await _javaArchive.StageAsync(
                        Project.Java,
                        stagingPayload,
                        token);
                    Report(
                        $"JRE 已解压并规范化为 JAVA（Java " +
                        $"{inspection.MajorVersion}，{inspection.Architecture}）。");
                    buildLog.Info(
                        $"Java 配置：JAVA\\bin\\java.exe，版本 " +
                        $"{inspection.MajorVersion}，架构 {inspection.Architecture}，" +
                        "禁止系统 Java 回退。");

                    var launcherConfigDirectory = Path.Combine(
                        stagingPayload,
                        "LauncherConfig");
                    Directory.CreateDirectory(launcherConfigDirectory);
                    if (Project.Client.DownloadMinecraftAndForgeFromOfficialSources)
                    {
                        var gameConfigDirectory = Path.Combine(
                            launcherConfigDirectory,
                            "Game");
                        Directory.CreateDirectory(gameConfigDirectory);
                        File.Copy(
                            MinecraftLaunchProfileService.ResolveManifest(Project),
                            Path.Combine(gameConfigDirectory, "version.json"),
                            true);
                        Report(
                            "已保存版本运行清单；Minecraft、资源库、资源文件和 Forge 运行库将在客户端从 Mojang/Forge 官方地址下载。");
                    }
                    var packagedBackgroundPath =
                        LauncherConfigGenerator.GetPackagedBackgroundImagePath(
                            Project.Basic.LauncherBackgroundImagePath);
                    if (!string.IsNullOrEmpty(packagedBackgroundPath))
                    {
                        var backgroundDestination = Path.Combine(
                            stagingPayload,
                            packagedBackgroundPath);
                        Directory.CreateDirectory(
                            Path.GetDirectoryName(backgroundDestination)!);
                        File.Copy(
                            Project.Basic.LauncherBackgroundImagePath,
                            backgroundDestination,
                            true);
                        Report(
                            $"已复制启动器背景图片：{packagedBackgroundPath}");
                    }

                    var launcherConfigPath = Path.Combine(
                        launcherConfigDirectory,
                        "launcher.json");
                    await File.WriteAllTextAsync(
                        launcherConfigPath,
                        _launcherConfigGenerator.Generate(Project),
                        token);
                    Report("已生成强制内置 Java 启动配置。");

                    var launcherDestination = Path.Combine(
                        stagingPayload,
                        "Launcher.exe");
                    _ = await _launcherPublisher.PublishAsync(
                        launcherDestination,
                        Project.Basic.LauncherVersion,
                        Project.Basic.ApplicationIconPath,
                        cancellationToken: token);
                    Report(string.IsNullOrWhiteSpace(
                        Project.Basic.ApplicationIconPath)
                        ? "已发布强制内置 JRE 启动器。"
                        : "已在发布阶段嵌入自定义图标并生成 Launcher.exe。");

                    var unifiedPassport = Project.LoginProviders.FirstOrDefault(
                        provider =>
                            provider.Type == LoginProviderType.UnifiedPassport);
                    if (unifiedPassport is not null)
                    {
                        var authenticationDirectory = Path.Combine(
                            stagingPayload,
                            "LauncherConfig",
                            "Auth");
                        Directory.CreateDirectory(authenticationDirectory);
                        File.Copy(
                            unifiedPassport.AuthenticationAgentPath,
                            Path.Combine(
                                authenticationDirectory,
                                "nide8auth.jar"),
                            true);
                        Report("已加入统一通行证 Agent。");
                    }

                    var authlibInjector = Project.LoginProviders.FirstOrDefault(
                        provider =>
                            provider.Type ==
                            LoginProviderType.CustomAuthenticationServer);
                    if (authlibInjector is not null)
                    {
                        var authenticationDirectory = Path.Combine(
                            stagingPayload,
                            "LauncherConfig",
                            "Auth");
                        Directory.CreateDirectory(authenticationDirectory);
                        File.Copy(
                            authlibInjector.AuthenticationAgentPath,
                            Path.Combine(
                                authenticationDirectory,
                                "authlib-injector.jar"),
                            true);
                        Report("已加入标准 Authlib Injector Agent。");
                    }

                    var resourceManifest = await _resourcePackages.StageAsync(
                        Project.Client,
                        launcherConfigDirectory,
                        stagingPayload,
                        _curseForgeApiKey,
                        token);
                    Report(resourceManifest.Provider == "CustomServer"
                        ? "模组、资源包和光影包将随指定更新服务器发布。"
                        : $"已生成 {resourceManifest.Provider} 官方下载清单：{resourceManifest.Files.Count} 个文件；平台 API Key 未写入项目或启动器。");

                    await _minecraftLaunchProfile.GenerateAsync(
                        Project,
                        launcherConfigDirectory,
                        token);
                    Report("已根据版本 JSON 自动生成便携 BAT 和 Java 参数配置。");

                    var manifest =
                        await _buildArtifacts.GeneratePayloadManifestAsync(
                            stagingPayload,
                            token);
                    Report(
                        $"已生成客户端文件清单：{manifest.FileCount} 个文件，" +
                        $"{manifest.TotalBytes / 1024d / 1024d:F2} MB。");

                    ClientPayloadService.Publish(stagingPayload, payloadPath);
                    Report($"完整 ClientPayload 已发布：{payloadPath}");
                }
                finally
                {
                    if (Directory.Exists(stagingPayload))
                    {
                        Directory.Delete(stagingPayload, true);
                    }
                }

                var releaseDirectory = Path.Combine(
                    outputRoot,
                    "ServerRelease");
                Directory.CreateDirectory(releaseDirectory);
                var releaseArchive = Path.Combine(
                    releaseDirectory,
                    $"release-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                var release = await _releaseBundles.CreateAsync(
                    payloadPath,
                    releaseArchive,
                    Project.Update.ProductId,
                    Project.Basic.ClientVersion,
                    token);
                LatestReleaseArchivePath = release.ArchivePath;
                Report(
                    $"服务器更新包已生成：{release.ArchivePath}；" +
                    $"ReleaseId={release.Manifest.ReleaseId}；" +
                    $"SHA-256={release.Sha256}");

                var stagingBootstrap = Path.Combine(
                    outputRoot,
                    $".BootstrapPayload.{Guid.NewGuid():N}.tmp");
                try
                {
                    Directory.CreateDirectory(Path.Combine(
                        stagingBootstrap,
                        "LauncherConfig"));
                    File.Copy(
                        Path.Combine(payloadPath, "Launcher.exe"),
                        Path.Combine(stagingBootstrap, "Launcher.exe"),
                        true);
                    var bootstrap = new UpdateBootstrapConfig
                    {
                        ServerBaseUrl = Project.Update.ServerBaseUrl,
                        ProductId = ReleaseBundleService.NormalizeProductId(
                            Project.Update.ProductId),
                        LauncherVersion = Project.Basic.LauncherVersion,
                        DownloadConcurrency =
                            Project.Update.DownloadConcurrency,
                        RequireSuccessfulCheck = true,
                        RequireAdministrator =
                            Project.Installation.RunLauncherAsAdministrator
                    };
                    await File.WriteAllTextAsync(
                        Path.Combine(
                            stagingBootstrap,
                            "LauncherConfig",
                            "update.json"),
                        JsonSerializer.Serialize(
                            bootstrap,
                            new JsonSerializerOptions
                            {
                                WriteIndented = true,
                                PropertyNamingPolicy =
                                    JsonNamingPolicy.CamelCase
                            }),
                        token);
                    ClientPayloadService.Publish(
                        stagingBootstrap,
                        bootstrapPayloadPath);
                }
                finally
                {
                    if (Directory.Exists(stagingBootstrap))
                    {
                        Directory.Delete(stagingBootstrap, true);
                    }
                }
                Report(
                    "轻量安装内容已生成，仅包含 Launcher.exe 和强制更新引导配置；" +
                    "Minecraft、JRE 与启动参数不再进入安装包。");

                _buildArtifacts.PublishLauncherConfig(
                    payloadPath,
                    outputRoot);
                Report(
                    $"已发布独立 LauncherConfig：" +
                    $"{Path.Combine(outputRoot, "LauncherConfig")}");

                var script = _scriptGenerator.Generate(
                    Project,
                    bootstrapPayloadPath,
                    outputRoot);
                await File.WriteAllTextAsync(scriptPath, script, token);
                Report($"已生成安装脚本：{scriptPath}");

                var compilerPath = _innoSetupLocator.FindCompiler(
                    Project.Output.InnoCompilerPath)
                    ?? throw new FileNotFoundException(
                        "未找到 Inno Setup 6 的 ISCC.exe；" +
                        "请在“安装包编译器”中手动选择。");
                Project.Output.InnoCompilerPath = compilerPath;
                OnPropertyChanged(nameof(Project));
                Report($"使用 Inno Setup 编译器：{compilerPath}");

                var installerStaging = Path.Combine(
                    outputRoot,
                    $".Installer.{Guid.NewGuid():N}.tmp");
                var compiler = new InnoCompiler();
                var installerBaseName =
                    InnoScriptGenerator.NormalizeOutputBaseName(
                        Project.Basic.OutputFileName);
                var finalInstallerPath = Path.Combine(
                    outputRoot,
                    installerBaseName + ".exe");
                try
                {
                    var result = await compiler.CompileAsync(
                        compilerPath,
                        scriptPath,
                        installerStaging,
                        token);
                    buildLog.Info(
                        $"Inno Setup 编译退出代码 {result.ExitCode}：" +
                        $"{Environment.NewLine}{result.Output}");
                    if (!result.Success)
                    {
                        throw new InvalidOperationException(
                            $"Inno Setup 编译失败，退出代码 {result.ExitCode}。" +
                            $"{Environment.NewLine}{result.Output}");
                    }

                    var stagedInstallerPath = Path.Combine(
                        installerStaging,
                        installerBaseName + ".exe");
                    if (!File.Exists(stagedInstallerPath))
                    {
                        throw new FileNotFoundException(
                            "Inno Setup 已返回成功，但没有找到生成的安装包。",
                            stagedInstallerPath);
                    }

                    File.Move(
                        stagedInstallerPath,
                        finalInstallerPath,
                        true);
                }
                finally
                {
                    if (Directory.Exists(installerStaging))
                    {
                        Directory.Delete(installerStaging, true);
                    }
                }

                var installerHash =
                    await _buildArtifacts.WriteSha256FileAsync(
                        finalInstallerPath,
                        token);
                Report($"最终安装包：{finalInstallerPath}");
                Report($"最终安装包 SHA-256：{installerHash}");
                LatestInstallerPath = finalInstallerPath;
                buildSucceeded = true;
            }
            catch (OperationCanceledException)
            {
                buildLog.Warning("构建已取消；未发布临时安装包。");
                throw;
            }
            catch (Exception exception)
            {
                buildLog.Error(exception);
                throw;
            }
            finally
            {
                buildLog.Complete(buildSucceeded);
                Log($"构建日志已写入：{buildLog.FilePath}");
            }
        }, "正在构建客户端、Launcher 和安装包…");
    }

    private async void PublishServerUpdate_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RunOperationAsync(async token =>
        {
            if (!Uri.TryCreate(
                    Project.Update.ServerBaseUrl,
                    UriKind.Absolute,
                    out var server))
            {
                throw new InvalidDataException("更新服务器地址无效。");
            }

            UpdatePublisherService.ValidateServerUri(server);
            OperationActivity = "正在测试更新服务器 HTTPS 连接…";
            await _updatePublisher.CheckServerHealthAsync(
                server,
                token);
            Log("更新服务器 HTTPS 健康检查通过。");
            var productId = ReleaseBundleService.NormalizeProductId(
                Project.Update.ProductId);
            OperationActivity = "正在读取服务器现有版本…";
            var published =
                await _updatePublisher.GetPublishedVersionsAsync(
                    server,
                    productId,
                    token);
            var publishPlan = UpdatePublisherService.CreatePublishPlan(
                Project.Basic.ClientVersion,
                Project.Basic.LauncherVersion,
                published);
            Log(
                $"版本对比：MC 本地 {Project.Basic.ClientVersion} / " +
                $"服务器 {FormatPublishedVersion(published.ClientVersion)}；" +
                $"Launcher 本地 {Project.Basic.LauncherVersion} / " +
                $"服务器 {FormatPublishedVersion(published.LauncherVersion)}。");
            if (!publishPlan.PublishLauncher)
            {
                Log(
                    $"Launcher {Project.Basic.LauncherVersion} 与服务器相同，" +
                    "跳过 Launcher 上传。");
            }

            if (!publishPlan.PublishClient)
            {
                Log(
                    $"MC {Project.Basic.ClientVersion} 与服务器相同，" +
                    "跳过 MC 更新包上传。");
            }

            if (!publishPlan.HasChanges)
            {
                OperationActivity = "Launcher 和 MC 版本均相同，无需上传。";
                Log("Launcher 和 MC 均为服务器现有版本，本次没有上传任何文件。");
                return;
            }

            _ = UpdatePublisherService.ReadKeyFile(PublisherKeyPath);
            var outputRoot = ResolveOutputRoot();
            if (publishPlan.PublishLauncher)
            {
                var installer = LatestInstallerPath;
                if (string.IsNullOrWhiteSpace(installer) ||
                    !File.Exists(installer))
                {
                    installer = Path.Combine(
                        outputRoot,
                        InnoScriptGenerator.NormalizeOutputBaseName(
                            Project.Basic.OutputFileName) + ".exe");
                }

                if (!File.Exists(installer))
                {
                    throw new FileNotFoundException(
                        "没有可发布的启动器安装包，请先点击“开始打包”。",
                        installer);
                }

                UpdatePublisherService.ValidateBuiltLauncherPackage(
                    installer,
                    Path.Combine(
                        outputRoot,
                        "BootstrapPayload",
                        "LauncherConfig",
                        "update.json"),
                    Path.Combine(
                        outputRoot,
                        "InstallerSource",
                        "setup.iss"),
                    Project.Basic.LauncherVersion);
                Log(
                    $"Launcher 发布版本一致性检查通过：" +
                    $"{Project.Basic.LauncherVersion}");

                Log(
                    $"正在通过 HTTPS 发布 Launcher " +
                    $"{Project.Basic.LauncherVersion}：{installer}");
                var launcherProgress = new Progress<PublishProgress>(
                    value => UpdatePublishProgress(
                        "Launcher 安装包",
                        value));
                var launcherResponse =
                    await _updatePublisher.PublishLauncherAsync(
                        server,
                        productId,
                        installer,
                        Project.Basic.LauncherVersion,
                        PublisherKeyPath,
                        token,
                        launcherProgress);
                LatestInstallerPath = installer;
                Log($"服务器启动器发布成功：{launcherResponse}");
            }

            if (publishPlan.PublishClient)
            {
                var archive = LatestReleaseArchivePath;
                if (string.IsNullOrWhiteSpace(archive) ||
                    !File.Exists(archive))
                {
                    var releaseDirectory = Path.GetFullPath(Path.Combine(
                        outputRoot,
                        "ServerRelease"));
                    archive = Directory.Exists(releaseDirectory)
                        ? Directory.EnumerateFiles(
                                releaseDirectory,
                                "*.zip")
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .FirstOrDefault()
                        : null;
                }

                if (string.IsNullOrWhiteSpace(archive))
                {
                    throw new FileNotFoundException(
                        "没有可发布的更新包，请先点击“开始打包”。");
                }

                Log($"正在通过 HTTPS 发布更新包：{archive}");
                var clientProgress = new Progress<PublishProgress>(
                    value => UpdatePublishProgress(
                        "MC 更新包",
                        value));
                var response = await _updatePublisher.PublishAsync(
                    server,
                    archive,
                    PublisherKeyPath,
                    token,
                    clientProgress);
                LatestReleaseArchivePath = archive;
                Log($"服务器更新发布成功：{response}");
            }
        }, "正在校验待发布文件…");
    }

    private async void PublishTestServerUpdate_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RunOperationAsync(async token =>
        {
            if (!Uri.TryCreate(
                    Project.Update.ServerBaseUrl,
                    UriKind.Absolute,
                    out var server))
            {
                throw new InvalidDataException("更新服务器地址无效。");
            }

            UpdatePublisherService.ValidateServerUri(server);
            OperationActivity = "正在测试更新服务器 HTTPS 连接…";
            await _updatePublisher.CheckServerHealthAsync(server, token);
            Log("测试通道：更新服务器 HTTPS 健康检查通过。");

            var productionProductId = ReleaseBundleService.NormalizeProductId(
                Project.Update.ProductId);
            var testProductId =
                LauncherChannelService.CreateTestProductId(
                    productionProductId);
            OperationActivity = "正在读取测试通道现有版本…";
            var published =
                await _updatePublisher.GetPublishedVersionsAsync(
                    server,
                    testProductId,
                    token);
            var clientVersion = (Project.Basic.ClientVersion ?? "").Trim();
            Log(
                $"测试通道版本对比：MC 本地 {clientVersion} / " +
                $"服务器 {FormatPublishedVersion(published.ClientVersion)}；" +
                $"产品标识 {testProductId}。");
            if (string.Equals(
                    clientVersion,
                    published.ClientVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                OperationActivity = "测试通道 MC 版本相同，无需上传。";
                Log(
                    $"测试通道 MC {clientVersion} 与服务器相同，" +
                    "本次没有上传任何文件。");
                return;
            }

            _ = UpdatePublisherService.ReadKeyFile(PublisherKeyPath);
            var outputRoot = ResolveOutputRoot();
            var payloadPath = Path.Combine(outputRoot, "ClientPayload");
            if (!Directory.Exists(payloadPath) ||
                !Directory.EnumerateFiles(
                    payloadPath,
                    "*",
                    SearchOption.AllDirectories).Any())
            {
                throw new DirectoryNotFoundException(
                    "没有可发布的客户端内容，请先点击“开始打包”。");
            }

            var testReleaseDirectory = Path.Combine(
                outputRoot,
                "ServerReleaseTest");
            Directory.CreateDirectory(testReleaseDirectory);
            var archive = Path.Combine(
                testReleaseDirectory,
                "test-release.zip");
            OperationActivity = "正在生成独立测试更新包…";
            Log(
                $"正在生成测试更新包：{archive}；" +
                $"不会修改正式通道 {productionProductId}。");
            var release = await _releaseBundles.CreateAsync(
                payloadPath,
                archive,
                testProductId,
                clientVersion,
                token);
            LatestTestReleaseArchivePath = release.ArchivePath;
            Log(
                $"测试更新包已生成：{release.ArchivePath}；" +
                $"ReleaseId={release.Manifest.ReleaseId}；" +
                $"SHA-256={release.Sha256}");

            OperationActivity = "正在发布测试通道 MC 更新…";
            var progress = new Progress<PublishProgress>(
                value => UpdatePublishProgress("测试 MC 更新包", value));
            var response = await _updatePublisher.PublishAsync(
                server,
                release.ArchivePath,
                PublisherKeyPath,
                token,
                progress);
            Log(
                $"测试通道发布成功（{testProductId}）：{response}");
        }, "正在校验测试通道待发布文件…");
    }

    private async void PublishServerPolicy_Click(
        object sender,
        RoutedEventArgs e)
    {
        await PublishServerPolicyAsync(useTestChannel: false);
    }

    private async void PublishTestServerPolicy_Click(
        object sender,
        RoutedEventArgs e)
    {
        await PublishServerPolicyAsync(useTestChannel: true);
    }

    private async Task PublishServerPolicyAsync(bool useTestChannel)
    {
        var channelName = useTestChannel ? "测试" : "正式";
        await RunOperationAsync(async token =>
        {
            if (!Uri.TryCreate(
                    Project.Update.ServerBaseUrl,
                    UriKind.Absolute,
                    out var server))
            {
                throw new InvalidDataException("更新服务器地址无效。");
            }

            UpdatePublisherService.ValidateServerUri(server);
            _ = UpdatePublisherService.ReadKeyFile(PublisherKeyPath);
            OperationActivity = "正在测试更新服务器 HTTPS 连接…";
            await _updatePublisher.CheckServerHealthAsync(
                server,
                token);
            OperationActivity = "正在上传服务器公告与启动策略…";
            var showMessage = Project.Update.ShowServerNotice;
            var blockLaunch = Project.Update.BlockGameLaunch;
            var policyIsActive = showMessage || blockLaunch;
            var policy = new ClientLaunchPolicy
            {
                ShowMessage = showMessage,
                Title = policyIsActive
                    ? (Project.Update.ServerNoticeTitle ?? "").Trim()
                    : "",
                Message = policyIsActive
                    ? (Project.Update.ServerNoticeMessage ?? "").Trim()
                    : "",
                BlockLaunch = blockLaunch
            };
            var response = await _updatePublisher.PublishPolicyAsync(
                server,
                useTestChannel
                    ? LauncherChannelService.CreateTestProductId(
                        Project.Update.ProductId)
                    : Project.Update.ProductId,
                PublisherKeyPath,
                policy,
                token);
            Log($"{channelName}通道公告与启动策略更新成功：{response}");
        }, $"正在准备{channelName}通道公告与启动策略…");
    }

    private void ApplyDetectedInnoCompiler(ProjectConfig project)
    {
        var detected = _innoSetupLocator.FindCompiler(
            project.Output.InnoCompilerPath);
        if (detected is not null)
        {
            project.Output.InnoCompilerPath = detected;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    private async Task RunOperationAsync(
        Func<CancellationToken, Task> operation,
        string initialActivity = "正在处理…")
    {
        if (_operationCancellation is not null) return;
        _operationCancellation = new();
        StatusText = "处理中...";
        OperationActivity = initialActivity;
        OperationProgress = 0;
        IsOperationProgressIndeterminate = true;
        try
        {
            await operation(_operationCancellation.Token);
            StatusText = "完成";
            OperationActivity = "操作已完成";
            IsOperationProgressIndeterminate = false;
            OperationProgress = 100;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            OperationActivity = "操作已取消";
            IsOperationProgressIndeterminate = false;
            OperationProgress = 0;
            Log("操作已取消。");
        }
        catch (Exception exception)
        {
            StatusText = "失败";
            var detail = GetDetailedExceptionMessage(exception);
            OperationActivity = "操作失败，请查看错误信息";
            IsOperationProgressIndeterminate = false;
            Log($"错误：{detail.Replace(
                Environment.NewLine,
                " | ",
                StringComparison.Ordinal)}");
            MessageBox.Show(
                this,
                detail,
                "操作失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private void UpdatePublishProgress(
        string contentName,
        PublishProgress progress)
    {
        var total = Math.Max(0, progress.TotalBytes);
        var processed = Math.Clamp(
            progress.ProcessedBytes,
            0,
            total);
        IsOperationProgressIndeterminate = total == 0;
        OperationProgress = total == 0
            ? 0
            : processed * 100d / total;
        var stage = progress.Stage switch
        {
            "Hashing" => "正在计算 SHA-256",
            "Uploading" => "正在上传",
            "Uploaded" => "上传完成，等待服务器校验",
            _ => progress.Stage
        };
        OperationActivity = total == 0
            ? $"{contentName}：{stage}"
            : $"{contentName}：{stage} " +
              $"{processed / 1024d / 1024d:F2} / " +
              $"{total / 1024d / 1024d:F2} MB " +
               $"({OperationProgress:F1}%)";
    }

    private static string FormatPublishedVersion(string version) =>
        string.IsNullOrWhiteSpace(version) ? "未发布" : version;

    private static string GetDetailedExceptionMessage(
        Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception;
             current is not null;
             current = current.InnerException)
        {
            var message = current.Message?.Trim();
            if (!string.IsNullOrWhiteSpace(message) &&
                !messages.Contains(
                    message,
                    StringComparer.Ordinal))
            {
                messages.Add(message);
            }
        }

        return string.Join(Environment.NewLine, messages);
    }

    private void Log(string message) =>
        LogText += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";

    private static List<string> SplitLines(string value) =>
        value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private bool HasLoginProvider(LoginProviderType type) =>
        Project.LoginProviders.Any(provider => provider.Type == type);

    private LoginProviderOptions? GetLoginProvider(LoginProviderType type) =>
        Project.LoginProviders.FirstOrDefault(provider => provider.Type == type);

    private LoginProviderOptions GetOrCreateThirdParty()
    {
        var provider = GetLoginProvider(LoginProviderType.UnifiedPassport);
        if (provider is not null)
        {
            return provider;
        }

        provider = new()
        {
            Type = LoginProviderType.UnifiedPassport,
            DisplayName = "统一通行证",
            ServerUrl = new Uri("https://auth.mc-user.com:233/")
        };
        Project.LoginProviders.Add(provider);
        OnPropertyChanged(nameof(ThirdPartyLoginEnabled));
        return provider;
    }

    private LoginProviderOptions GetOrCreateAuthlib()
    {
        var provider = GetLoginProvider(LoginProviderType.CustomAuthenticationServer);
        if (provider is not null)
        {
            return provider;
        }

        provider = new()
        {
            Type = LoginProviderType.CustomAuthenticationServer,
            DisplayName = "Authlib Injector"
        };
        Project.LoginProviders.Add(provider);
        OnPropertyChanged(nameof(AuthlibLoginEnabled));
        return provider;
    }

    private void SetLoginProviderEnabled(LoginProviderType type, bool enabled, string displayName)
    {
        var existing = GetLoginProvider(type);
        if (enabled && existing is null)
        {
            Project.LoginProviders.Add(new()
            {
                Type = type,
                DisplayName = displayName,
                IsDefault = Project.LoginProviders.Count == 0
            });
        }
        else if (!enabled && existing is not null)
        {
            Project.LoginProviders.Remove(existing);
            if (existing.IsDefault && Project.LoginProviders.Count > 0)
            {
                Project.LoginProviders[0].IsDefault = true;
            }
        }

        OnPropertyChanged(type switch
        {
            LoginProviderType.Microsoft => nameof(MicrosoftLoginEnabled),
            LoginProviderType.Offline => nameof(OfflineLoginEnabled),
            LoginProviderType.CustomAuthenticationServer => nameof(AuthlibLoginEnabled),
            _ => nameof(ThirdPartyLoginEnabled)
        });
    }

    private void SetResourceDelivery(ResourceDeliveryMode mode)
    {
        Project.Client.ResourceDelivery = mode;
        OnPropertyChanged(nameof(IsModrinthDelivery));
        OnPropertyChanged(nameof(IsCurseForgeDelivery));
        OnPropertyChanged(nameof(IsCustomServerDelivery));
    }

    private static Uri? ParseAbsoluteUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private string ResolveOutputRoot() =>
        OutputPathResolver.Resolve(
            Project.Output.OutputDirectory,
            GetExecutableDirectory());

    private static string GetExecutableDirectory()
    {
        var executablePath = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(executablePath)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(executablePath)
              ?? AppContext.BaseDirectory;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }
}
