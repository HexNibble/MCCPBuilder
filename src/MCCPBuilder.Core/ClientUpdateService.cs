using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace MCCPBuilder.Core;

public sealed class ClientUpdateService
{
    public const int MinDownloadConcurrency = 1;
    public const int MaxDownloadConcurrency = 200;
    public const int DefaultDownloadAttempts = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string? _launcherDownloadRoot;
    private readonly TimeSpan _downloadInactivityTimeout;
    private readonly int _downloadAttempts;

    public ClientUpdateService(
        HttpClient? httpClient = null,
        string? launcherDownloadRoot = null,
        TimeSpan? downloadInactivityTimeout = null,
        int downloadAttempts = DefaultDownloadAttempts)
    {
        _httpClient = httpClient ?? new HttpClient(
            CreateHttpHandler())
        {
            Timeout = TimeSpan.FromHours(4)
        };
        _launcherDownloadRoot = string.IsNullOrWhiteSpace(
            launcherDownloadRoot)
            ? null
            : Path.GetFullPath(launcherDownloadRoot);
        _downloadInactivityTimeout =
            downloadInactivityTimeout ?? TimeSpan.FromSeconds(30);
        if (_downloadInactivityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(downloadInactivityTimeout),
                "下载无数据超时必须大于零。");
        }

        if (downloadAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(downloadAttempts),
                "下载尝试次数必须至少为 1。");
        }

        _downloadAttempts = downloadAttempts;
    }

    internal static SocketsHttpHandler CreateHttpHandler() =>
        new()
        {
            UseProxy = false,
            MaxConnectionsPerServer = MaxDownloadConcurrency,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

    public async Task<UpdateResult> CheckAndApplyAsync(
        string applicationDirectory,
        UpdateBootstrapConfig bootstrap,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default,
        DownloadPauseController? pauseController = null)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(applicationDirectory));
        var server = ValidateBootstrap(bootstrap);
        progress?.Report(new(
            "Checking", "正在检查服务器更新…", 0, 0, 0, 0));

        using var response = await _httpClient.GetAsync(
            new Uri(
                server,
                $"v1/products/{Uri.EscapeDataString(bootstrap.ProductId)}/manifest"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(
                cancellationToken);
            throw new HttpRequestException(
                $"更新检查失败（HTTP {(int)response.StatusCode}）：{detail}",
                null,
                response.StatusCode);
        }

        var manifest = await response.Content.ReadFromJsonAsync<UpdateManifest>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidDataException("服务器更新清单为空。");
        ValidateManifest(manifest, bootstrap);

        if (bootstrap.RequireLauncherUpdateCheck &&
            manifest.Launcher is not null &&
            IsNewerVersion(
                manifest.Launcher.Version,
                bootstrap.LauncherVersion))
        {
            progress?.Report(new(
                "LauncherUpdate",
                $"发现启动器新版 {manifest.Launcher.Version}，正在下载安装包…",
                0,
                1,
                0,
                manifest.Launcher.Size));
            var installerPath = await DownloadLauncherInstallerAsync(
                server,
                bootstrap.ProductId,
                manifest.Launcher,
                progress,
                cancellationToken,
                pauseController);
            return new(
                false,
                manifest.ReleaseId,
                manifest.Version,
                1,
                manifest.Launcher.Size,
                manifest.Policy,
                manifest.Launcher,
                installerPath);
        }

        var metadataRoot = ResolveMetadataRoot(root);
        var statePath = Path.Combine(metadataRoot, "state.json");
        var previousState = LoadState(statePath);
        var previousProductState =
            previousState is not null &&
            string.Equals(
                previousState.ProductId,
                manifest.ProductId,
                StringComparison.Ordinal)
                ? previousState
                : null;
        if (previousProductState is not null &&
            string.Equals(
                previousProductState.Version,
                manifest.Version,
                StringComparison.Ordinal))
        {
            progress?.Report(new(
                "Current",
                $"服务器版本号与本地一致：{manifest.Version}。",
                0,
                0,
                0,
                0));
            return new(
                false,
                manifest.ReleaseId,
                manifest.Version,
                0,
                0,
                manifest.Policy);
        }

        Directory.CreateDirectory(metadataRoot);
        var transactionId = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(
            metadataRoot,
            $"staging-{transactionId}");
        var backupRoot = Path.Combine(
            metadataRoot,
            $"backup-{transactionId}");
        Directory.CreateDirectory(stagingRoot);
        var changed = new List<UpdateManifestEntry>();
        DownloadProgressReporter? downloadProgress = null;
        long totalBytes = 0;
        UpdateResult? updateResult = null;
        var previousFiles = new Dictionary<string, UpdateManifestEntry>(
            StringComparer.OrdinalIgnoreCase);
        if (previousProductState is not null)
        {
            foreach (var file in previousProductState.Files ?? [])
            {
                if (!string.IsNullOrWhiteSpace(file.Path))
                {
                    previousFiles[file.Path] = file;
                }
            }
        }

        try
        {
            progress?.Report(new(
                "Preparing",
                "检测到新的服务器版本，正在计算更新内容…",
                0,
                0,
                0,
                0));
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveInside(root, file.Path);
                EnsureNoReparsePoints(root, destination);
                if (ShouldPreserveExisting(file) &&
                    File.Exists(destination))
                {
                    continue;
                }

                if (previousFiles.TryGetValue(
                        file.Path,
                        out var previousFile) &&
                    EntriesHaveSameContent(previousFile, file) &&
                    File.Exists(destination))
                {
                    continue;
                }

                changed.Add(file);
            }

            totalBytes = changed.Sum(file => file.Size);
            downloadProgress = new DownloadProgressReporter(
                progress,
                "Downloading",
                changed.Count,
                totalBytes);
            downloadProgress.Report(
                changed.Count == 0
                    ? "版本号已更新，没有需要下载的文件。"
                    : $"准备以 {bootstrap.DownloadConcurrency} 路" +
                      $"并行下载 {changed.Count} 个文件。",
                true);
            using var downloadCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            Exception? firstDownloadFailure = null;
            try
            {
                await Parallel.ForEachAsync(
                    changed,
                    new ParallelOptions
                    {
                        CancellationToken = downloadCancellation.Token,
                        MaxDegreeOfParallelism =
                            bootstrap.DownloadConcurrency
                    },
                    async (file, token) =>
                    {
                        try
                        {
                            if (pauseController is not null)
                            {
                                await pauseController
                                    .WaitWhilePausedAsync(token);
                            }

                            var staged = ResolveInside(
                                stagingRoot,
                                file.Path);
                            Directory.CreateDirectory(
                                Path.GetDirectoryName(staged)!);
                            var fileUri = CreateFileUri(
                                server,
                                manifest.ProductId,
                                manifest.ReleaseId,
                                file.Path);
                            downloadProgress.Report(
                                $"正在下载：{file.Path}",
                                false);
                            await DownloadAndVerifyAsync(
                                fileUri,
                                staged,
                                file,
                                token,
                                pauseController,
                                count => downloadProgress.AddBytes(
                                    count,
                                    $"正在下载：{file.Path}"),
                                message => downloadProgress.Report(
                                    message,
                                    true));
                            downloadProgress.CompleteFile(
                                $"下载完成：{file.Path}");
                        }
                        catch (Exception exception)
                        {
                            Interlocked.CompareExchange(
                                ref firstDownloadFailure,
                                exception,
                                null);
                            await downloadCancellation.CancelAsync();
                            throw;
                        }
                    });
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested &&
                      firstDownloadFailure is not null)
            {
                ExceptionDispatchInfo
                    .Capture(firstDownloadFailure)
                    .Throw();
                throw;
            }

            progress?.Report(new(
                "Applying",
                "下载与校验完成，正在应用更新…",
                changed.Count,
                changed.Count,
                downloadProgress.CompletedBytes,
                totalBytes));
            await Task.Run(() => ApplyTransaction(
                    root,
                    stagingRoot,
                    backupRoot,
                    changed,
                    previousProductState,
                    manifest),
                CancellationToken.None);
            WriteStateAtomically(statePath, manifest);
            updateResult = new(
                true,
                manifest.ReleaseId,
                manifest.Version,
                changed.Count,
                downloadProgress.CompletedBytes,
                manifest.Policy);
        }
        finally
        {
            progress?.Report(new(
                "Cleaning",
                "正在清理更新暂存文件…",
                changed.Count,
                changed.Count,
                downloadProgress?.CompletedBytes ?? 0,
                totalBytes));
            await Task.Run(() =>
            {
                DeleteDirectoryBestEffort(stagingRoot);
                DeleteDirectoryBestEffort(backupRoot);
            }, CancellationToken.None);
        }

        progress?.Report(new(
            "Complete",
            $"更新完成：{manifest.Version}",
            changed.Count,
            changed.Count,
            downloadProgress?.CompletedBytes ?? 0,
            totalBytes));
        return updateResult
               ?? throw new InvalidOperationException("更新事务未生成结果。");
    }

    private static string ResolveMetadataRoot(string root)
        => ResolveInside(root, ".mccp-update");

    public static UpdateBootstrapConfig LoadBootstrap(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "缺少 LauncherConfig\\update.json，无法检查更新。",
                path);
        }

        return JsonSerializer.Deserialize<UpdateBootstrapConfig>(
                   File.ReadAllText(path),
                   JsonOptions)
               ?? throw new InvalidDataException("更新引导配置为空或格式无效。");
    }

    private static Uri ValidateBootstrap(UpdateBootstrapConfig bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (!bootstrap.RequireSuccessfulCheck)
        {
            throw new InvalidDataException("客户端必须启用强制更新检查。");
        }

        if (!Uri.TryCreate(
                bootstrap.ServerBaseUrl,
                UriKind.Absolute,
                out var server))
        {
            throw new InvalidDataException("更新服务器地址无效。");
        }

        UpdatePublisherService.ValidateServerUri(server);
        _ = ReleaseBundleService.NormalizeProductId(bootstrap.ProductId);
        _ = ParseVersion(
            bootstrap.LauncherVersion,
            "本地启动器版本");
        if (bootstrap.DownloadConcurrency is
            < MinDownloadConcurrency or
            > MaxDownloadConcurrency)
        {
            throw new InvalidDataException(
                $"下载线程数必须在 {MinDownloadConcurrency} 到 " +
                $"{MaxDownloadConcurrency} 之间。");
        }

        return server;
    }

    private static void ValidateManifest(
        UpdateManifest manifest,
        UpdateBootstrapConfig bootstrap)
    {
        if (manifest.SchemaVersion != "1.0" ||
            manifest.ProductId != bootstrap.ProductId ||
            string.IsNullOrWhiteSpace(manifest.ReleaseId) ||
            !InputValidator.IsValidVersion(manifest.Version) ||
            manifest.Files is null ||
            manifest.Files.Count == 0)
        {
            throw new InvalidDataException(
                "服务器更新清单的版本、产品标识或内容无效。");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ReleaseBundleService.EnsureSafeRelativePath(file.Path);
            if (!paths.Add(file.Path) ||
                file.Size < 0 ||
                file.Sha256.Length != 64 ||
                !file.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException(
                    $"服务器更新文件条目无效：{file.Path}");
            }
        }

        if (bootstrap.RequireLauncherUpdateCheck &&
            manifest.Launcher is null)
        {
            throw new InvalidDataException(
                "服务器没有提供启动器版本信息，已阻止启动。");
        }

        if (manifest.Launcher is not null)
        {
            _ = ParseVersion(
                manifest.Launcher.Version,
                "服务器启动器版本");
            if (manifest.Launcher.Size <= 0 ||
                manifest.Launcher.Sha256.Length != 64 ||
                !manifest.Launcher.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException(
                    "服务器启动器安装包信息无效。");
            }
        }

        manifest.Policy ??= new();
        manifest.Policy.Title ??= "";
        manifest.Policy.Message ??= "";
        if (manifest.Policy.Title.Length > 128 ||
            manifest.Policy.Message.Length > 4000 ||
            ((manifest.Policy.ShowMessage || manifest.Policy.BlockLaunch) &&
             string.IsNullOrWhiteSpace(manifest.Policy.Message)))
        {
            throw new InvalidDataException("服务器公告或启动控制策略无效。");
        }
    }

    private async Task<string> DownloadLauncherInstallerAsync(
        Uri server,
        string productId,
        LauncherPackageInfo launcher,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken,
        DownloadPauseController? pauseController)
    {
        var localRoot = _launcherDownloadRoot;
        if (string.IsNullOrWhiteSpace(localRoot))
        {
            localRoot = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localRoot))
            {
                localRoot = Path.GetTempPath();
            }

            localRoot = Path.Combine(
                localRoot,
                "MCCPBuilder",
                "LauncherUpdates");
        }

        var version = ParseVersion(
            launcher.Version,
            "服务器启动器版本").ToString(3);
        var destinationDirectory = Path.Combine(
            localRoot,
            ReleaseBundleService.NormalizeProductId(productId),
            version);
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(
            destinationDirectory,
            "LauncherSetup.exe");
        var expected = new UpdateManifestEntry
        {
            Path = "LauncherSetup.exe",
            Size = launcher.Size,
            Sha256 = launcher.Sha256
        };
        if (await FileMatchesAsync(
                destination,
                expected,
                cancellationToken))
        {
            progress?.Report(new(
                "LauncherUpdate",
                $"启动器安装包已就绪：{launcher.Version}",
                1,
                1,
                launcher.Size,
                launcher.Size));
            return destination;
        }

        var temporary =
            destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var downloadProgress = new DownloadProgressReporter(
                progress,
                "LauncherUpdate",
                1,
                launcher.Size);
            downloadProgress.Report(
                $"正在下载启动器新版 {launcher.Version}…",
                true);
            await DownloadAndVerifyAsync(
                CreateLauncherInstallerUri(
                    server,
                    productId,
                    launcher.Version),
                temporary,
                expected,
                cancellationToken,
                 pauseController,
                 count => downloadProgress.AddBytes(
                     count,
                     $"正在下载启动器新版 {launcher.Version}…"),
                 message => downloadProgress.Report(message, true));
            File.Move(temporary, destination, true);
            downloadProgress.CompleteFile(
                $"启动器新版 {launcher.Version} 下载完成，准备原位升级…",
                true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<bool> FileMatchesAsync(
        string path,
        UpdateManifestEntry expected,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expected.Size)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return hash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadAndVerifyAsync(
        Uri uri,
        string destination,
        UpdateManifestEntry expected,
        CancellationToken cancellationToken,
        DownloadPauseController? pauseController = null,
        Action<long>? bytesDownloaded = null,
        Action<string>? statusChanged = null)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= _downloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            long attemptBytes = 0;
            try
            {
                await DownloadAndVerifyAttemptAsync(
                    uri,
                    destination,
                    expected,
                    cancellationToken,
                    pauseController,
                    count =>
                    {
                        attemptBytes += count;
                        bytesDownloaded?.Invoke(count);
                    });
                return;
            }
            catch (Exception exception)
                when (IsRetryableDownloadFailure(
                          exception,
                          cancellationToken) &&
                      attempt < _downloadAttempts)
            {
                lastFailure = exception;
                if (attemptBytes > 0)
                {
                    bytesDownloaded?.Invoke(-attemptBytes);
                }

                statusChanged?.Invoke(
                    $"下载连接停滞或中断，正在进行第 {attempt + 1} / " +
                    $"{_downloadAttempts} 次尝试：{expected.Path}");
                await Task.Delay(
                    TimeSpan.FromMilliseconds(500 * attempt),
                    cancellationToken);
            }
        }

        throw lastFailure
              ?? new IOException($"下载失败：{expected.Path}");
    }

    private async Task DownloadAndVerifyAttemptAsync(
        Uri uri,
        string destination,
        UpdateManifestEntry expected,
        CancellationToken cancellationToken,
        DownloadPauseController? pauseController,
        Action<long>? bytesDownloaded)
    {
        if (pauseController is not null)
        {
            await pauseController.WaitWhilePausedAsync(cancellationToken);
        }

        using var response = await WithInactivityTimeoutAsync(
            token => _httpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                token),
            cancellationToken,
            expected.Path);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length &&
            length != expected.Size)
        {
            throw new InvalidDataException(
                $"服务器文件大小不匹配：{expected.Path}");
        }

        await using var source = await WithInactivityTimeoutAsync(
            token => response.Content.ReadAsStreamAsync(token),
            cancellationToken,
            expected.Path);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        long total = 0;
        while (total < expected.Size)
        {
            if (pauseController is not null)
            {
                await pauseController.WaitWhilePausedAsync(
                    cancellationToken);
            }

            var remaining = expected.Size - total;
            var read = await WithInactivityTimeoutAsync(
                token => source.ReadAsync(
                        buffer.AsMemory(
                            0,
                            (int)Math.Min(buffer.Length, remaining)),
                        token)
                    .AsTask(),
                cancellationToken,
                expected.Path);
            if (read == 0)
            {
                break;
            }

            if (pauseController is not null)
            {
                await pauseController.WaitWhilePausedAsync(
                    cancellationToken);
            }

            hasher.AppendData(buffer, 0, read);
            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
            total += read;
            bytesDownloaded?.Invoke(read);
        }

        await output.FlushAsync(cancellationToken);
        var hash = Convert.ToHexString(hasher.GetHashAndReset());
        if (total != expected.Size ||
            !hash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"服务器文件完整性校验失败：{expected.Path}");
        }
    }

    private async Task<T> WithInactivityTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        string relativePath)
    {
        using var inactivityCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        inactivityCancellation.CancelAfter(_downloadInactivityTimeout);
        try
        {
            return await operation(inactivityCancellation.Token);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested &&
                  inactivityCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"服务器文件连续 {_downloadInactivityTimeout.TotalSeconds:0} " +
                $"秒没有收到数据：{relativePath}",
                exception);
        }
    }

    private static bool IsRetryableDownloadFailure(
        Exception exception,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is HttpRequestException
            or IOException
            or TimeoutException
            or OperationCanceledException
            or InvalidDataException;

    private static void ApplyTransaction(
        string root,
        string stagingRoot,
        string backupRoot,
        IReadOnlyList<UpdateManifestEntry> changed,
        UpdateManifest? previous,
        UpdateManifest current)
    {
        var installedNew = new List<string>();
        var movedBackups = new List<(string Backup, string Original)>();
        try
        {
            var removed = previous?.Files
                .Where(old => current.Files.All(currentFile =>
                    !currentFile.Path.Equals(
                        old.Path,
                        StringComparison.OrdinalIgnoreCase)))
                .Where(old => !ShouldPreserveExisting(old))
                .Select(old => old.Path)
                .ToArray() ?? [];
            foreach (var relative in changed.Select(file => file.Path)
                         .Concat(removed)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var destination = ResolveInside(root, relative);
                EnsureNoReparsePoints(root, destination);
                if (!File.Exists(destination))
                {
                    continue;
                }

                var backup = ResolveInside(backupRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(destination, backup);
                movedBackups.Add((backup, destination));
            }

            foreach (var file in changed)
            {
                var staged = ResolveInside(stagingRoot, file.Path);
                var destination = ResolveInside(root, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(staged, destination);
                installedNew.Add(destination);
            }
        }
        catch
        {
            foreach (var path in installedNew.AsEnumerable().Reverse())
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            foreach (var item in movedBackups.AsEnumerable().Reverse())
            {
                if (File.Exists(item.Backup))
                {
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(item.Original)!);
                    File.Move(item.Backup, item.Original, true);
                }
            }

            throw;
        }
    }

    private static UpdateManifest? LoadState(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteStateAtomically(
        string statePath,
        UpdateManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temporary =
            statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(manifest, JsonOptions),
                new UTF8Encoding(false));
            File.Move(temporary, statePath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static Uri CreateFileUri(
        Uri server,
        string productId,
        string releaseId,
        string relativePath)
    {
        var encodedPath = string.Join(
            "/",
            relativePath.Split('/').Select(Uri.EscapeDataString));
        return new Uri(
            server,
            $"v1/files/{Uri.EscapeDataString(productId)}/" +
            $"{Uri.EscapeDataString(releaseId)}/{encodedPath}");
    }

    private static Uri CreateLauncherInstallerUri(
        Uri server,
        string productId,
        string version) =>
        new(
            server,
            $"v1/launchers/{Uri.EscapeDataString(productId)}/" +
            $"{Uri.EscapeDataString(version)}/setup.exe");

    private static bool ShouldPreserveExisting(
        UpdateManifestEntry file) =>
        file.PreserveExisting ||
        UserDataPathPolicy.IsProtected(file.Path);

    private static bool EntriesHaveSameContent(
        UpdateManifestEntry previous,
        UpdateManifestEntry current) =>
        previous.Size == current.Size &&
        string.Equals(
            previous.Sha256,
            current.Sha256,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsNewerVersion(
        string available,
        string current) =>
        ParseVersion(available, "服务器启动器版本") >
        ParseVersion(current, "本地启动器版本");

    private static Version ParseVersion(
        string value,
        string fieldName)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Split('.').Length != 3 ||
            !Version.TryParse(normalized, out var version) ||
            version.Major < 0 ||
            version.Minor < 0 ||
            version.Build < 0 ||
            version.Revision >= 0)
        {
            throw new InvalidDataException(
                $"{fieldName}必须使用 x.y.z 格式。");
        }

        return version;
    }

    private static string ResolveInside(string root, string relativePath)
    {
        ReleaseBundleService.EnsureSafeRelativePath(
            relativePath.Replace('\\', '/'));
        var normalizedRoot =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(
            Path.Combine(normalizedRoot, relativePath.Replace('/', '\\')));
        if (!path.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"更新路径越过安装目录：{relativePath}");
        }

        return path;
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        var rootInfo = new DirectoryInfo(root);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("安装根目录不能是重解析点。");
        }

        var current = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (current.FullName.StartsWith(
                   rootInfo.FullName,
                   StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists &&
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"更新路径包含重解析点：{current.FullName}");
            }

            if (current.FullName.Equals(
                    rootInfo.FullName,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = current.Parent
                ?? throw new InvalidDataException("无法验证更新路径。");
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // A stale transaction directory is ignored and never treated as a
            // complete update. A later run uses a new unique directory.
        }
    }

    private sealed class DownloadProgressReporter(
        IProgress<UpdateProgress>? progress,
        string stage,
        int totalFiles,
        long totalBytes)
    {
        private static readonly long ReportIntervalTicks =
            Math.Max(1, Stopwatch.Frequency / 5);

        private readonly object _gate = new();
        private long _completedBytes;
        private int _completedFiles;
        private long _lastReportTimestamp;

        public long CompletedBytes => Interlocked.Read(
            ref _completedBytes);

        public void AddBytes(long count, string message)
        {
            var completed = Interlocked.Add(
                ref _completedBytes,
                count);
            Report(message, completed == count);
        }

        public void CompleteFile(
            string message,
            bool force = false)
        {
            var completed = Interlocked.Increment(ref _completedFiles);
            Report(message, force || completed == totalFiles);
        }

        public void Report(string message, bool force)
        {
            if (progress is null)
            {
                return;
            }

            UpdateProgress? snapshot = null;
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                if (!force &&
                    _lastReportTimestamp != 0 &&
                    now - _lastReportTimestamp < ReportIntervalTicks)
                {
                    return;
                }

                _lastReportTimestamp = now;
                snapshot = new(
                    stage,
                    message,
                    Volatile.Read(ref _completedFiles),
                    totalFiles,
                    Interlocked.Read(ref _completedBytes),
                    totalBytes);
            }

            progress.Report(snapshot);
        }
    }
}
