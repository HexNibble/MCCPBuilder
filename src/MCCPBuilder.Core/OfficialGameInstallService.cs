using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MCCPBuilder.Core;

public sealed record OfficialGameInstallOptions(
    string ApplicationDirectory,
    string MinecraftRoot,
    string VersionDirectory,
    string ClientJar,
    string VersionManifest,
    int DownloadConcurrency,
    bool CustomizeForgeBranding = false,
    string ForgeBrandingJar = "",
    string ForgeBrandingText = "",
    string JavaExecutable = "");

public sealed record OfficialGameInstallProgress(
    string Activity,
    int CompletedFiles,
    int TotalFiles,
    bool IsIndeterminate = false);

public sealed class OfficialGameInstallService
{
    private const int InstallMarkerSchemaVersion = 2;
    private static readonly TimeSpan ForgeInstallerTimeout = TimeSpan.FromMinutes(15);

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "piston-data.mojang.com",
        "piston-meta.mojang.com",
        "libraries.minecraft.net",
        "resources.download.minecraft.net",
        "maven.minecraftforge.net"
    };

    private readonly HttpClient _httpClient;

    public OfficialGameInstallService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            MaxConnectionsPerServer = 32,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
    }

    public async Task EnsureInstalledAsync(
        OfficialGameInstallOptions options,
        IProgress<OfficialGameInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var appRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.ApplicationDirectory));
        var minecraftRoot = ResolveInside(appRoot, options.MinecraftRoot);
        var versionDirectory = ResolveInside(appRoot, options.VersionDirectory);
        var clientJar = ResolveInside(appRoot, options.ClientJar);
        var manifestPath = ResolveInside(appRoot, options.VersionManifest);
        var javaExecutable = ResolveJavaExecutable(appRoot, options.JavaExecutable);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("缺少官方游戏下载清单。", manifestPath);
        }

        var manifestHash = await ComputeSha256Async(manifestPath, cancellationToken);
        var installationStateHash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                manifestHash + "|" +
                options.CustomizeForgeBranding + "|" +
                options.ForgeBrandingJar + "|" +
                 options.ForgeBrandingText)));
        var markerPath = Path.Combine(versionDirectory, ".mccp-official-install.json");

        await using var manifestStream = File.OpenRead(manifestPath);
        using var manifest = await JsonDocument.ParseAsync(
            manifestStream,
            cancellationToken: cancellationToken);
        var root = manifest.RootElement;
        Directory.CreateDirectory(minecraftRoot);
        Directory.CreateDirectory(versionDirectory);

        var downloads = BuildDownloads(root, minecraftRoot, clientJar);
        var forgeInstallerPlan = CreateForgeInstallerPlan(root, minecraftRoot);
        var brandingJar = options.CustomizeForgeBranding
            ? ResolveInside(appRoot, options.ForgeBrandingJar)
            : "";
        var marker = await ReadMarkerAsync(markerPath, cancellationToken);
        if (await MarkerIsCurrentAsync(
                marker,
                installationStateHash,
                downloads,
                forgeInstallerPlan,
                brandingJar,
                cancellationToken))
        {
            progress?.Report(new("官方 Minecraft/Forge 文件已就绪。", 0, 0, true));
            return;
        }

        var mustInstallAssets = !HasCurrentInstallationState(
            marker,
            installationStateHash);
        if (options.CustomizeForgeBranding)
        {
            var brandingUri = CreateForgeUniversalUri(minecraftRoot, brandingJar);
            var brandingSha1 = await ReadOfficialSha1Async(
                brandingUri,
                cancellationToken);
            downloads.Add(new(
                brandingUri,
                brandingJar,
                brandingSha1,
                0,
                Path.GetFileName(brandingJar)));
        }
        var total = downloads.Count;
        var completed = 0;
        var failures = new ConcurrentQueue<Exception>();
        progress?.Report(new("正在从 Mojang 与 Forge 官方服务器补齐游戏文件…", 0, total));
        await Parallel.ForEachAsync(
            downloads,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(options.DownloadConcurrency, 1, 32)
            },
            async (download, token) =>
            {
                try
                {
                    await DownloadVerifiedAsync(download, token);
                    var count = Interlocked.Increment(ref completed);
                    progress?.Report(new($"正在下载：{download.DisplayName}", count, total));
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            });
        if (!failures.IsEmpty)
        {
            throw new AggregateException("官方游戏文件下载失败。", failures);
        }

        IReadOnlyList<GeneratedLibraryMarker> generatedLibraries = [];
        if (forgeInstallerPlan is not null)
        {
            if (!await ForgeRuntimeFilesAreCurrentAsync(
                    forgeInstallerPlan,
                    marker?.ForgeRuntimeLibraries,
                    cancellationToken))
            {
                progress?.Report(new(
                    "正在通过 Forge 官方安装器补全运行库…",
                    0,
                    0,
                    true));
                await InstallForgeRuntimeLibrariesAsync(
                    forgeInstallerPlan,
                    javaExecutable,
                    minecraftRoot,
                    versionDirectory,
                    cancellationToken);
            }

            generatedLibraries = await CaptureForgeRuntimeLibrariesAsync(
                forgeInstallerPlan,
                cancellationToken);
            progress?.Report(new(
                "Forge 官方运行库已就绪，正在准备下载 Minecraft 资源…",
                0,
                0,
                true));
        }

        if (mustInstallAssets)
        {
            await InstallAssetsAsync(
                root,
                minecraftRoot,
                Math.Clamp(options.DownloadConcurrency, 1, 32),
                progress,
                cancellationToken);
        }
        InstallNatives(root, minecraftRoot, versionDirectory);
        if (options.CustomizeForgeBranding)
        {
            await new ForgeBrandingService().ApplyToJarAsync(
                brandingJar,
                options.ForgeBrandingText,
                cancellationToken);
        }

        var versionJsonPath = Path.Combine(
            versionDirectory,
            Path.GetFileName(versionDirectory) + ".json");
        File.Copy(manifestPath, versionJsonPath, true);
        await File.WriteAllTextAsync(
            markerPath,
            JsonSerializer.Serialize(new InstallMarker(
                InstallMarkerSchemaVersion,
                installationStateHash,
                DateTimeOffset.UtcNow,
                generatedLibraries)),
            cancellationToken);
        progress?.Report(new("官方 Minecraft/Forge 文件安装完成。", 0, 0, true));
    }

    private static List<OfficialDownload> BuildDownloads(
        JsonElement root,
        string minecraftRoot,
        string clientJar)
    {
        var downloads = new List<OfficialDownload>();
        if (!root.TryGetProperty("downloads", out var rootDownloads) ||
            !rootDownloads.TryGetProperty("client", out var client))
        {
            throw new InvalidDataException("版本运行清单缺少 Mojang 客户端下载信息。");
        }
        downloads.Add(ReadDownload(client, clientJar, "Minecraft 客户端"));

        if (root.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateArray())
            {
                if (!RulesAllowWindows(library) ||
                    !library.TryGetProperty("downloads", out var libraryDownloads))
                {
                    continue;
                }

                if (libraryDownloads.TryGetProperty("artifact", out var artifact) &&
                    artifact.TryGetProperty("path", out var artifactPath))
                {
                    var relative = ValidateRelativePath(artifactPath.GetString() ?? "");
                    downloads.Add(ReadDownload(
                        artifact,
                        Path.Combine(minecraftRoot, "libraries", relative),
                        relative));
                }

                var nativeKey = GetWindowsNativeClassifier(library);
                if (nativeKey is not null &&
                    libraryDownloads.TryGetProperty("classifiers", out var classifiers) &&
                    classifiers.TryGetProperty(nativeKey, out var native) &&
                    native.TryGetProperty("path", out var nativePath))
                {
                    var relative = ValidateRelativePath(nativePath.GetString() ?? "");
                    downloads.Add(ReadDownload(
                        native,
                        Path.Combine(minecraftRoot, "libraries", relative),
                        relative));
                }
            }
        }

        if (root.TryGetProperty("logging", out var logging) &&
            logging.TryGetProperty("client", out var loggingClient) &&
            loggingClient.TryGetProperty("file", out var loggingFile) &&
            loggingFile.TryGetProperty("id", out var logId))
        {
            var fileName = Path.GetFileName(logId.GetString());
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new InvalidDataException("日志配置文件名无效。");
            }
            downloads.Add(ReadDownload(
                loggingFile,
                Path.Combine(minecraftRoot, "assets", "log_configs", fileName),
                fileName));
        }
        return downloads
            .GroupBy(item => item.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    internal static ForgeInstallerPlan? CreateForgeInstallerPlan(
        JsonElement root,
        string minecraftRoot)
    {
        var gameArguments = ReadGameArgumentValues(root);
        var launchTarget = ReadArgumentValue(gameArguments, "--launchTarget");
        if (!launchTarget.Equals("forgeclient", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var minecraftVersion = ReadArgumentValue(gameArguments, "--fml.mcVersion");
        var forgeVersion = ReadArgumentValue(gameArguments, "--fml.forgeVersion");
        var mcpVersion = ReadArgumentValue(gameArguments, "--fml.mcpVersion");
        ValidateForgeVersionPart(minecraftVersion, "Minecraft");
        ValidateForgeVersionPart(forgeVersion, "Forge");
        ValidateForgeVersionPart(mcpVersion, "MCP");

        var minecraftMcpVersion = $"{minecraftVersion}-{mcpVersion}";
        var forgeCombinedVersion = $"{minecraftVersion}-{forgeVersion}";
        var requiredFiles = new[]
        {
            CreateForgeRuntimeFile(
                minecraftRoot,
                $"libraries/net/minecraft/client/{minecraftMcpVersion}/" +
                $"client-{minecraftMcpVersion}-slim.jar"),
            CreateForgeRuntimeFile(
                minecraftRoot,
                $"libraries/net/minecraft/client/{minecraftMcpVersion}/" +
                $"client-{minecraftMcpVersion}-extra.jar"),
            CreateForgeRuntimeFile(
                minecraftRoot,
                $"libraries/net/minecraft/client/{minecraftMcpVersion}/" +
                $"client-{minecraftMcpVersion}-srg.jar"),
            CreateForgeRuntimeFile(
                minecraftRoot,
                $"libraries/net/minecraftforge/forge/{forgeCombinedVersion}/" +
                $"forge-{forgeCombinedVersion}-client.jar")
        };
        return new(
            minecraftVersion,
            forgeVersion,
            mcpVersion,
            CreateForgeInstallerUri(minecraftVersion, forgeVersion),
            requiredFiles);
    }

    internal static Uri CreateForgeInstallerUri(
        string minecraftVersion,
        string forgeVersion)
    {
        ValidateForgeVersionPart(minecraftVersion, "Minecraft");
        ValidateForgeVersionPart(forgeVersion, "Forge");
        var version = $"{minecraftVersion}-{forgeVersion}";
        return new Uri(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/" +
            $"{version}/forge-{version}-installer.jar");
    }

    private static ForgeRuntimeFile CreateForgeRuntimeFile(
        string minecraftRoot,
        string relativePath)
    {
        var normalizedRelative = ValidateRelativePath(relativePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(minecraftRoot));
        var targetPath = Path.GetFullPath(Path.Combine(
            root,
            normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!targetPath.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Forge 运行库路径超出 .minecraft 目录。");
        }
        return new ForgeRuntimeFile(
            normalizedRelative,
            targetPath,
            Path.GetFileName(targetPath));
    }

    private static IReadOnlyList<string> ReadGameArgumentValues(JsonElement root)
    {
        if (!root.TryGetProperty("arguments", out var arguments) ||
            !arguments.TryGetProperty("game", out var gameArguments) ||
            gameArguments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var argument in gameArguments.EnumerateArray())
        {
            if (argument.ValueKind == JsonValueKind.String)
            {
                values.Add(argument.GetString() ?? "");
                continue;
            }
            if (argument.ValueKind != JsonValueKind.Object ||
                !argument.TryGetProperty("value", out var value))
            {
                continue;
            }
            if (value.ValueKind == JsonValueKind.String)
            {
                values.Add(value.GetString() ?? "");
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                values.AddRange(value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? ""));
            }
        }
        return values;
    }

    private static string ReadArgumentValue(
        IReadOnlyList<string> arguments,
        string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }
        return "";
    }

    private static void ValidateForgeVersionPart(string value, string displayName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                                   character is not '.' and not '-' and not '_' and not '+'))
        {
            throw new InvalidDataException($"Forge {displayName} 版本标识无效。");
        }
    }

    private async Task InstallAssetsAsync(
        JsonElement root,
        string minecraftRoot,
        int concurrency,
        IProgress<OfficialGameInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("assetIndex", out var assetIndex) ||
            !assetIndex.TryGetProperty("id", out var idProperty))
        {
            throw new InvalidDataException("版本运行清单缺少 Mojang 资源索引。");
        }
        var indexId = Path.GetFileName(idProperty.GetString());
        if (string.IsNullOrWhiteSpace(indexId))
        {
            throw new InvalidDataException("Mojang 资源索引标识无效。");
        }
        var indexPath = Path.Combine(minecraftRoot, "assets", "indexes", indexId + ".json");
        progress?.Report(new("正在读取 Mojang 官方资源清单…", 0, 0));
        await DownloadVerifiedAsync(ReadDownload(assetIndex, indexPath, indexId + ".json"), cancellationToken);
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken));
        if (!index.RootElement.TryGetProperty("objects", out var objects))
        {
            throw new InvalidDataException("Mojang 资源索引缺少 objects。");
        }

        var assets = new List<OfficialDownload>();
        foreach (var entry in objects.EnumerateObject())
        {
            var hash = entry.Value.GetProperty("hash").GetString() ?? "";
            if (hash.Length != 40 || hash.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException($"资源 {entry.Name} 的 SHA-1 无效。");
            }
            var size = entry.Value.GetProperty("size").GetInt64();
            var url = new Uri($"https://resources.download.minecraft.net/{hash[..2]}/{hash}");
            var target = Path.Combine(minecraftRoot, "assets", "objects", hash[..2], hash);
            assets.Add(new(url, target, hash, size, entry.Name));
        }

        var totalBytes = assets.Sum(asset => asset.Size);
        progress?.Report(new(
            $"正在下载 Mojang 官方资源：共 {assets.Count} 个文件，约 {FormatByteSize(totalBytes)}",
            0,
            assets.Count));
        var completed = 0;
        var failures = new ConcurrentQueue<Exception>();
        await Parallel.ForEachAsync(
            assets,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = concurrency
            },
            async (asset, token) =>
            {
                try
                {
                    await DownloadVerifiedAsync(asset, token);
                    var count = Interlocked.Increment(ref completed);
                    progress?.Report(new(
                        $"正在下载 Mojang 官方资源：{asset.DisplayName}",
                        count,
                        assets.Count));
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            });
        if (!failures.IsEmpty)
        {
            throw new AggregateException("Mojang 资源文件下载失败。", failures);
        }
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024L * 1024L)
        {
            return $"{bytes / 1024d:F1} KB";
        }
        if (bytes < 1024L * 1024L * 1024L)
        {
            return $"{bytes / 1024d / 1024d:F1} MB";
        }
        return $"{bytes / 1024d / 1024d / 1024d:F2} GB";
    }

    private static void InstallNatives(
        JsonElement root,
        string minecraftRoot,
        string versionDirectory)
    {
        var nativesDirectory = Path.Combine(
            versionDirectory,
            Path.GetFileName(versionDirectory) + "-natives");
        Directory.CreateDirectory(nativesDirectory);
        if (!root.TryGetProperty("libraries", out var libraries))
        {
            return;
        }
        foreach (var library in libraries.EnumerateArray())
        {
            if (!RulesAllowWindows(library) ||
                !library.TryGetProperty("downloads", out var downloads))
            {
                continue;
            }
            var nativeKey = GetWindowsNativeClassifier(library);
            if (nativeKey is null ||
                !downloads.TryGetProperty("classifiers", out var classifiers) ||
                !classifiers.TryGetProperty(nativeKey, out var native) ||
                !native.TryGetProperty("path", out var pathProperty))
            {
                continue;
            }
            var relative = ValidateRelativePath(pathProperty.GetString() ?? "");
            var archivePath = Path.Combine(minecraftRoot, "libraries", relative);
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) ||
                    entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var target = Path.GetFullPath(Path.Combine(nativesDirectory, entry.Name));
                if (!target.StartsWith(
                        Path.TrimEndingDirectorySeparator(nativesDirectory) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Native 文件路径越界。");
                }
                entry.ExtractToFile(target, true);
            }
        }
    }

    private async Task InstallForgeRuntimeLibrariesAsync(
        ForgeInstallerPlan plan,
        string javaExecutable,
        string minecraftRoot,
        string selectedVersionDirectory,
        CancellationToken cancellationToken)
    {
        ValidateOfficialUri(plan.InstallerUri);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "MCCPBuilder",
            "ForgeInstaller",
            Guid.NewGuid().ToString("N"));
        var installerPath = Path.Combine(temporaryRoot, "forge-installer.jar");
        var launcherProfilePath = Path.Combine(minecraftRoot, "launcher_profiles.json");
        var generatedVersionDirectories = GetForgeInstallerVersionDirectories(
            minecraftRoot,
            selectedVersionDirectory,
            plan);
        var createdLauncherProfile = false;
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var installerSha1 = await ReadOfficialSha1Async(
                plan.InstallerUri,
                cancellationToken);
            await DownloadVerifiedAsync(new(
                plan.InstallerUri,
                installerPath,
                installerSha1,
                0,
                $"Forge {plan.MinecraftVersion}-{plan.ForgeVersion} 官方安装器"),
                cancellationToken);
            createdLauncherProfile = await EnsureForgeInstallerProfileAsync(
                launcherProfilePath,
                cancellationToken);
            await RunForgeInstallerAsync(
                javaExecutable,
                installerPath,
                minecraftRoot,
                temporaryRoot,
                cancellationToken);
            _ = await CaptureForgeRuntimeLibrariesAsync(plan, cancellationToken);
            CleanupForgeInstallerVersionDirectories(generatedVersionDirectories);
        }
        finally
        {
            if (createdLauncherProfile)
            {
                try
                {
                    if (File.Exists(launcherProfilePath))
                    {
                        File.Delete(launcherProfilePath);
                    }
                }
                catch
                {
                    // 只删除本次创建的空配置；清理失败不影响已安装的游戏文件。
                }
            }
            try
            {
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, true);
                }
            }
            catch
            {
                // 临时安装器目录无法清理不影响已校验的游戏文件；下次会使用新目录。
            }
        }
    }

    private static IReadOnlyList<TemporaryVersionDirectory>
        GetForgeInstallerVersionDirectories(
            string minecraftRoot,
            string selectedVersionDirectory,
            ForgeInstallerPlan plan)
    {
        var versionsRoot = Path.GetFullPath(Path.Combine(minecraftRoot, "versions"));
        var selected = Path.GetFullPath(selectedVersionDirectory);
        var candidates = new[]
        {
            Path.Combine(versionsRoot, plan.MinecraftVersion),
            Path.Combine(
                versionsRoot,
                $"{plan.MinecraftVersion}-forge-{plan.ForgeVersion}")
        }
        .Select(Path.GetFullPath)
        .Where(path => !path.Equals(selected, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => new TemporaryVersionDirectory(path, Directory.Exists(path)))
        .ToArray();

        foreach (var candidate in candidates)
        {
            if (!candidate.Path.StartsWith(
                    Path.TrimEndingDirectorySeparator(versionsRoot) +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Forge 安装器临时版本目录越界。");
            }
        }
        return candidates;
    }

    private static void CleanupForgeInstallerVersionDirectories(
        IReadOnlyList<TemporaryVersionDirectory> directories)
    {
        foreach (var directory in directories.Where(item => !item.ExistedBefore))
        {
            try
            {
                if (Directory.Exists(directory.Path))
                {
                    Directory.Delete(directory.Path, true);
                }
            }
            catch
            {
                // 只清理本次新建的标准 Forge 临时版本目录；失败时保留以免误删。
            }
        }
    }

    private static async Task<bool> EnsureForgeInstallerProfileAsync(
        string launcherProfilePath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(launcherProfilePath))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(launcherProfilePath)!);
        try
        {
            await using var stream = new FileStream(
                launcherProfilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.WriteAsync("{\"profiles\":{}}"u8.ToArray(), cancellationToken);
            return true;
        }
        catch (IOException) when (File.Exists(launcherProfilePath))
        {
            return false;
        }
    }

    private static async Task RunForgeInstallerAsync(
        string javaExecutable,
        string installerPath,
        string minecraftRoot,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = javaExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("--installClient");
        startInfo.ArgumentList.Add(minecraftRoot);

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("无法启动 Forge 官方安装器。");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ForgeInstallerTimeout);
        using var installerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await process.WaitForExitAsync(installerCancellation.Token);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryTerminateForgeInstaller(process);
            throw new TimeoutException(
                $"Forge 官方安装器在 {ForgeInstallerTimeout.TotalMinutes:0} 分钟内未完成。" +
                "请检查网络后重新启动启动器。");
        }
        catch (OperationCanceledException)
        {
            TryTerminateForgeInstaller(process);
            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Forge 官方安装器执行失败（退出代码 {process.ExitCode}）。" +
                SummarizeInstallerOutput(output, error));
        }
    }

    private static void TryTerminateForgeInstaller(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            // 取消或超时时尽力终止安装器；保持原始失败信息。
        }
    }

    private static string SummarizeInstallerOutput(string output, string error)
    {
        var text = string.Join(Environment.NewLine, [output, error])
            .Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        const int maximumLength = 1600;
        return Environment.NewLine +
               (text.Length <= maximumLength ? text : text[^maximumLength..]);
    }

    private static async Task<IReadOnlyList<GeneratedLibraryMarker>>
        CaptureForgeRuntimeLibrariesAsync(
            ForgeInstallerPlan plan,
            CancellationToken cancellationToken)
    {
        var libraries = new List<GeneratedLibraryMarker>(plan.RequiredFiles.Count);
        foreach (var requiredFile in plan.RequiredFiles)
        {
            var file = new FileInfo(requiredFile.TargetPath);
            if (!file.Exists || file.Length <= 0)
            {
                throw new FileNotFoundException(
                    $"Forge 官方安装器未生成必需运行库：{requiredFile.RelativePath}",
                    requiredFile.TargetPath);
            }
            libraries.Add(new(
                requiredFile.RelativePath,
                await ComputeSha256Async(requiredFile.TargetPath, cancellationToken),
                file.Length));
        }
        return libraries;
    }

    private static async Task<bool> ForgeRuntimeFilesAreCurrentAsync(
        ForgeInstallerPlan plan,
        IReadOnlyList<GeneratedLibraryMarker>? recordedLibraries,
        CancellationToken cancellationToken)
    {
        if (recordedLibraries is null ||
            recordedLibraries.Count != plan.RequiredFiles.Count)
        {
            return false;
        }

        foreach (var requiredFile in plan.RequiredFiles)
        {
            var recorded = recordedLibraries.FirstOrDefault(item =>
                string.Equals(
                    item.RelativePath,
                    requiredFile.RelativePath,
                    StringComparison.OrdinalIgnoreCase));
            if (recorded is null)
            {
                return false;
            }
            var file = new FileInfo(requiredFile.TargetPath);
            if (!file.Exists || file.Length != recorded.Size ||
                string.IsNullOrWhiteSpace(recorded.Sha256))
            {
                return false;
            }
            var actualHash = await ComputeSha256Async(
                requiredFile.TargetPath,
                cancellationToken);
            if (!actualHash.Equals(recorded.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private async Task DownloadVerifiedAsync(
        OfficialDownload download,
        CancellationToken cancellationToken)
    {
        ValidateOfficialUri(download.Uri);
        if (await FileMatchesAsync(download, cancellationToken))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(download.TargetPath)!);
        var temporary = download.TargetPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using var response = await _httpClient.GetAsync(
                download.Uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            await destination.DisposeAsync();
            if (!await FileMatchesAsync(download with { TargetPath = temporary }, cancellationToken))
            {
                throw new InvalidDataException($"官方文件校验失败：{download.DisplayName}");
            }
            File.Move(temporary, download.TargetPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<bool> FileMatchesAsync(
        OfficialDownload download,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(download.TargetPath);
        if (!file.Exists || (download.Size > 0 && file.Length != download.Size))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(download.Sha1))
        {
            return true;
        }
        await using var stream = file.OpenRead();
        var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken));
        return actual.Equals(download.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private static OfficialDownload ReadDownload(
        JsonElement element,
        string target,
        string displayName)
    {
        var url = element.GetProperty("url").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException($"官方文件 URL 无效：{displayName}");
        }
        var sha1 = element.TryGetProperty("sha1", out var hash) ? hash.GetString() ?? "" : "";
        var size = element.TryGetProperty("size", out var sizeProperty) ? sizeProperty.GetInt64() : 0;
        return new(uri, target, sha1, size, displayName);
    }

    internal static void ValidateOfficialUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
        {
            throw new InvalidDataException($"拒绝非 Mojang/Forge 官方下载地址：{uri}");
        }
    }

    internal static Uri CreateForgeUniversalUri(
        string minecraftRoot,
        string brandingJarPath)
    {
        var librariesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Path.Combine(minecraftRoot, "libraries")));
        var jarPath = Path.GetFullPath(brandingJarPath);
        if (!jarPath.StartsWith(
                librariesRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Forge Universal JAR 必须位于 .minecraft\\libraries 中。");
        }

        var relative = Path.GetRelativePath(librariesRoot, jarPath)
            .Replace('\\', '/');
        if (!relative.StartsWith(
                "net/minecraftforge/forge/",
                StringComparison.OrdinalIgnoreCase) ||
            !relative.EndsWith("-universal.jar", StringComparison.OrdinalIgnoreCase) ||
            relative.Split('/').Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment == ".."))
        {
            throw new InvalidDataException(
                "Forge Universal JAR 的 Maven 相对路径无效。");
        }

        return new Uri("https://maven.minecraftforge.net/" + relative);
    }

    private async Task<string> ReadOfficialSha1Async(
        Uri artifactUri,
        CancellationToken cancellationToken)
    {
        ValidateOfficialUri(artifactUri);
        var sha1Uri = new Uri(artifactUri.AbsoluteUri + ".sha1");
        using var response = await _httpClient.GetAsync(
            sha1Uri,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var value = (await response.Content.ReadAsStringAsync(cancellationToken))
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        if (value.Length != 40 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                $"Forge 官方 Maven 返回了无效的 SHA-1：{sha1Uri}");
        }

        return value;
    }

    private static string? GetWindowsNativeClassifier(JsonElement library)
    {
        if (!library.TryGetProperty("natives", out var natives) ||
            !natives.TryGetProperty("windows", out var windows))
        {
            return null;
        }
        return (windows.GetString() ?? "").Replace("${arch}", "64", StringComparison.Ordinal);
    }

    private static bool RulesAllowWindows(JsonElement element)
    {
        if (!element.TryGetProperty("rules", out var rules)) return true;
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var matches = true;
            if (rule.TryGetProperty("os", out var os) &&
                os.TryGetProperty("name", out var name))
            {
                matches = name.GetString() == "windows";
            }
            if (matches)
            {
                allowed = rule.TryGetProperty("action", out var action) &&
                          action.GetString() == "allow";
            }
        }
        return allowed;
    }

    private static string ValidateRelativePath(string path)
    {
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized) ||
            Path.IsPathRooted(normalized) ||
            normalized.Split(Path.DirectorySeparatorChar).Any(segment => segment == ".."))
        {
            throw new InvalidDataException($"下载目标相对路径无效：{path}");
        }
        return normalized;
    }

    private static string ResolveInside(string root, string relative)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, ValidateRelativePath(relative)));
        if (!candidate.StartsWith(
                Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"运行路径超出安装目录：{relative}");
        }
        return candidate;
    }

    private static string ResolveJavaExecutable(string applicationRoot, string javaExecutable)
    {
        if (string.IsNullOrWhiteSpace(javaExecutable))
        {
            throw new InvalidDataException("缺少用于 Forge 官方安装器的内置 JAVA\\bin\\java.exe。");
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationRoot));
        var candidate = Path.GetFullPath(javaExecutable);
        if (!candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Forge 官方安装器只能使用安装目录内置的 Java。");
        }
        if (!Path.GetFileName(candidate).Equals("java.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(candidate))
        {
            throw new FileNotFoundException("内置 JRE 不完整，缺少 JAVA\\bin\\java.exe。", candidate);
        }
        return candidate;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<InstallMarker?> ReadMarkerAsync(
        string markerPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(markerPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<InstallMarker>(
                await File.ReadAllTextAsync(markerPath, cancellationToken));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasCurrentInstallationState(
        InstallMarker? marker,
        string installationStateHash) =>
        marker is not null &&
        !string.IsNullOrWhiteSpace(marker.ManifestSha256) &&
        marker.ManifestSha256.Equals(
            installationStateHash,
            StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> MarkerIsCurrentAsync(
        InstallMarker? marker,
        string installationStateHash,
        IReadOnlyList<OfficialDownload> downloads,
        ForgeInstallerPlan? forgeInstallerPlan,
        string brandingJar,
        CancellationToken cancellationToken)
    {
        if (marker?.SchemaVersion != InstallMarkerSchemaVersion ||
            !HasCurrentInstallationState(marker, installationStateHash))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(brandingJar) && !File.Exists(brandingJar))
        {
            return false;
        }
        foreach (var download in downloads)
        {
            if (!await FileMatchesAsync(download, cancellationToken))
            {
                return false;
            }
        }
        return forgeInstallerPlan is null ||
               await ForgeRuntimeFilesAreCurrentAsync(
                   forgeInstallerPlan,
                   marker.ForgeRuntimeLibraries,
                   cancellationToken);
    }

    private sealed record OfficialDownload(
        Uri Uri,
        string TargetPath,
        string Sha1,
        long Size,
        string DisplayName);

    internal sealed record ForgeInstallerPlan(
        string MinecraftVersion,
        string ForgeVersion,
        string McpVersion,
        Uri InstallerUri,
        IReadOnlyList<ForgeRuntimeFile> RequiredFiles);

    internal sealed record ForgeRuntimeFile(
        string RelativePath,
        string TargetPath,
        string DisplayName);

    private sealed record GeneratedLibraryMarker(
        string RelativePath,
        string Sha256,
        long Size);

    private sealed record TemporaryVersionDirectory(
        string Path,
        bool ExistedBefore);

    private sealed record InstallMarker(
        int SchemaVersion,
        string ManifestSha256,
        DateTimeOffset InstalledAt,
        IReadOnlyList<GeneratedLibraryMarker> ForgeRuntimeLibraries);
}
