namespace MCCPBuilder.Core;

public sealed class UpdateBootstrapConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string ServerBaseUrl { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string LauncherVersion { get; set; } = "0.0.0";
    public int DownloadConcurrency { get; set; } =
        ClientUpdateService.MaxDownloadConcurrency;
    public bool RequireSuccessfulCheck { get; set; } = true;
    public bool RequireLauncherUpdateCheck { get; set; } = true;
    public bool RequireAdministrator { get; set; }
}

public sealed class UpdateManifest
{
    public string SchemaVersion { get; set; } = "1.0";
    public string ProductId { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public List<UpdateManifestEntry> Files { get; set; } = [];
    public ClientLaunchPolicy Policy { get; set; } = new();
    public LauncherPackageInfo? Launcher { get; set; }
    public StreamingBundleInfo? Bundle { get; set; }
}

public sealed class UpdateManifestEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public bool PreserveExisting { get; set; }
}

public sealed class LauncherPackageInfo
{
    public string Version { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class StreamingBundleInfo
{
    public string Format { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed record UpdateProgress(
    string Stage,
    string Message,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes);

public sealed record PublishProgress(
    string Stage,
    long ProcessedBytes,
    long TotalBytes);

public sealed record PublishedUpdateVersions(
    string ClientVersion,
    string LauncherVersion);

public sealed record UpdatePublishPlan(
    bool PublishClient,
    bool PublishLauncher)
{
    public bool HasChanges => PublishClient || PublishLauncher;
}

public sealed record UpdateResult(
    bool Updated,
    string ReleaseId,
    string Version,
    int DownloadedFiles,
    long DownloadedBytes,
    ClientLaunchPolicy Policy,
    LauncherPackageInfo? LauncherUpdate = null,
    string LauncherInstallerPath = "");

public sealed class ClientLaunchPolicy
{
    public bool ShowMessage { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public bool BlockLaunch { get; set; }
}
