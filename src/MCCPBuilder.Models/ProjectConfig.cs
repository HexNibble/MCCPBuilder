namespace MCCPBuilder.Models;

public sealed class ProjectConfig
{
    public string FormatVersion { get; set; } = "1.0";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ApplicationVersion { get; set; } = "0.1.2";
    public BasicInfo Basic { get; set; } = new();
    public ClientContentOptions Client { get; set; } = new();
    public LaunchOptions Launch { get; set; } = new();
    public List<LoginProviderOptions> LoginProviders { get; set; } = [new() { Type = LoginProviderType.Microsoft, DisplayName = "Microsoft 正版登录", IsDefault = true }];
    public JavaOptions Java { get; set; } = new();
    public InstallationOptions Installation { get; set; } = new();
    public UpdateOptions Update { get; set; } = new();
    public OutputOptions Output { get; set; } = new();
    public ThemeOptions Theme { get; set; } = new();
}

public sealed class BasicInfo
{
    public string ClientName { get; set; } = "";
    public string ClientVersion { get; set; } = "1.0.0";
    public string LauncherVersion { get; set; } = "1.0.0";
    public string MinecraftVersion { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string LauncherTitle { get; set; } = "";
    public string LauncherBackgroundImagePath { get; set; } = "";
    public string ApplicationIconPath { get; set; } = "";
    public string InstallerIconPath { get; set; } = "";
    public string OutputFileName { get; set; } = "MinecraftClientSetup";
}

public sealed class ClientContentOptions
{
    public string SourceDirectory { get; set; } = "";
    public string MinecraftRootDirectory { get; set; } = "";
    public string VersionDirectory { get; set; } = "";
    public string VersionManifestPath { get; set; } = "";
    public string LaunchEntryPath { get; set; } = "";
    public List<string> IncludeRules { get; set; } = ["**/*"];
    public List<string> ExcludeRules { get; set; } =
    [
        "**/logs/**", "**/crash-reports/**", "**/cache/**", "**/*cache*/**",
        "**/screenshots/**", "**/*.cache", "**/*.log", "**/*.tmp",
        "**/launcher_accounts.json", "**/launcher_msa_credentials.bin", "**/accounts.json",
        "**/launcher_profiles.json", "**/usercache.json", "**/PCL.ini", "**/PCL/**",
        "**/cookies*", "**/token*", "**/saves/**"
    ];
    public bool IncludeMinecraftDirectory { get; set; } = true;
    public bool IncludeVersions { get; set; } = true;
    public bool IncludeMods { get; set; } = true;
    public bool IncludeConfigs { get; set; } = true;
    public bool IncludeResourcePacks { get; set; } = true;
    public bool IncludeShaderPacks { get; set; } = true;
    public bool IncludeSaves { get; set; }
    public bool DownloadMinecraftAndForgeFromOfficialSources { get; set; } = true;
    public ResourceDeliveryMode ResourceDelivery { get; set; } = ResourceDeliveryMode.CustomServer;
    public string ResourcePackagePath { get; set; } = "";
}

public enum ResourceDeliveryMode
{
    Modrinth,
    CurseForge,
    CustomServer
}

public sealed class LaunchOptions
{
    public bool UseBatchFile { get; set; }
    public string BatchFilePath { get; set; } = "";
    public string PackagedBatchRelativePath { get; set; } = @"LauncherConfig\launch.bat";
    public List<string> GameArguments { get; set; } = [];
    public List<string> LauncherArguments { get; set; } = [];
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];
    public string WorkingDirectory { get; set; } = ".";
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
    public bool FullScreen { get; set; }
    public string GameWindowTitle { get; set; } = "";
    public bool AutoJoinServer { get; set; }
    public string ServerAddress { get; set; } = "";
    public bool CleanCachesBeforeLaunch { get; set; }
    public bool CleanLogsBeforeLaunch { get; set; }
    public bool CustomizeForgeMcpBranding { get; set; }
    public string ForgeMcpBrandingText { get; set; } = "";
    public bool AllowUserArguments { get; set; } = true;
    public int MinimumMemoryMb { get; set; } = 1024;
    public int MaximumMemoryMb { get; set; } = 4096;
    public List<string> GcArguments { get; set; } = [];
    public List<string> JvmArguments { get; set; } = [];
    public bool UsePcl2JvmPreset { get; set; }
    public bool AllowUserMemoryChange { get; set; } = true;
    public bool SuggestMemoryAutomatically { get; set; } = true;
}

public enum LoginProviderType { Microsoft, Offline, CustomAuthenticationServer, ThirdPartyPassport, UnifiedPassport }

public sealed class LoginProviderOptions
{
    public LoginProviderType Type { get; set; }
    public string DisplayName { get; set; } = "";
    public Uri? ServerUrl { get; set; }
    public Uri? ApiUrl { get; set; }
    public string ClientId { get; set; } = "";
    public Uri? CallbackUrl { get; set; }
    public bool IsDefault { get; set; }
    public bool IsRequired { get; set; }
    public string IconPath { get; set; } = "";
    public string AuthenticationAgentPath { get; set; } = "";
    public string ServerIdentifier { get; set; } = "";
    public Dictionary<string, string> SecretPlaceholders { get; set; } = [];
}

public enum JavaMode { Bundled, SpecifiedDirectory, AutoDetect, BlockWhenMissing }

public sealed class JavaOptions
{
    public JavaMode Mode { get; set; } = JavaMode.Bundled;
    public string JavaArchivePath { get; set; } = "";
    public string JavaHome { get; set; } = "";
    public int PreferredMajorVersion { get; set; } = 17;
    public int MinimumMajorVersion { get; set; } = 17;
    public int MaximumMajorVersion { get; set; } = 21;
    public bool EnforceVersion { get; set; } = true;
    public bool BundleJava { get; set; } = true;
    public string JavaExecutableRelativePath { get; set; } = @"bin\java.exe";
    public List<string> Arguments { get; set; } = [];
    public string RequiredArchitecture { get; set; } = "x64";
    public bool ForceConfiguredJava { get; set; } = true;
}

public sealed class InstallationOptions
{
    public bool AllowInstallDirectorySelection { get; set; } = true;
    public string DefaultInstallDirectory { get; set; } = @"{localappdata}\Programs\{clientName}";
    public bool AllowCurrentUserInstall { get; set; } = true;
    public bool AllowAllUsersInstall { get; set; }
    public bool RunLauncherAsAdministrator { get; set; }
    public bool LaunchAfterInstall { get; set; } = true;
    public bool AllowOverwrite { get; set; } = true;
    public bool SupportUpgrade { get; set; } = true;
    public bool PreserveUserConfiguration { get; set; }
    public bool AskToPreserveUserDataOnUninstall { get; set; }
}

public sealed class UpdateOptions
{
    public string ServerBaseUrl { get; set; } = "";
    public string ProductId { get; set; } = "";
    public int DownloadConcurrency { get; set; } = 200;
    public bool RequireSuccessfulCheck { get; set; } = true;
    public bool ShowServerNotice { get; set; }
    public string ServerNoticeTitle { get; set; } = "";
    public string ServerNoticeMessage { get; set; } = "";
    public bool BlockGameLaunch { get; set; }
}

public sealed class OutputOptions
{
    public string OutputDirectory { get; set; } = "output";
    public string InnoCompilerPath { get; set; } = "";
}

public sealed class ThemeOptions
{
    public string InstallerTheme { get; set; } = "System";
    public int CornerRadius { get; set; } = 8;
}
