using System.Collections.Concurrent;
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
    string ForgeBrandingText = "");

public sealed record OfficialGameInstallProgress(
    string Activity,
    int CompletedFiles,
    int TotalFiles);

public sealed class OfficialGameInstallService
{
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
        if (await MarkerIsCurrentAsync(
                markerPath,
                installationStateHash,
                clientJar,
                cancellationToken))
        {
            progress?.Report(new("官方 Minecraft/Forge 文件已就绪。", 1, 1));
            return;
        }

        await using var manifestStream = File.OpenRead(manifestPath);
        using var manifest = await JsonDocument.ParseAsync(
            manifestStream,
            cancellationToken: cancellationToken);
        var root = manifest.RootElement;
        Directory.CreateDirectory(minecraftRoot);
        Directory.CreateDirectory(versionDirectory);

        var downloads = BuildDownloads(root, minecraftRoot, clientJar);
        if (options.CustomizeForgeBranding)
        {
            var brandingJar = ResolveInside(appRoot, options.ForgeBrandingJar);
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

        await InstallAssetsAsync(
            root,
            minecraftRoot,
            Math.Clamp(options.DownloadConcurrency, 1, 32),
            progress,
            cancellationToken);
        InstallNatives(root, minecraftRoot, versionDirectory);
        if (options.CustomizeForgeBranding)
        {
            var brandingJar = ResolveInside(appRoot, options.ForgeBrandingJar);
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
            JsonSerializer.Serialize(new InstallMarker(installationStateHash, DateTimeOffset.UtcNow)),
            cancellationToken);
        progress?.Report(new("官方 Minecraft/Forge 文件安装完成。", 1, 1));
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
                    progress?.Report(new($"正在下载资源：{asset.DisplayName}", count, assets.Count));
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

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<bool> MarkerIsCurrentAsync(
        string markerPath,
        string manifestHash,
        string clientJar,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(markerPath) || !File.Exists(clientJar)) return false;
        try
        {
            var marker = JsonSerializer.Deserialize<InstallMarker>(
                await File.ReadAllTextAsync(markerPath, cancellationToken));
            return marker?.ManifestSha256.Equals(
                manifestHash,
                StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record OfficialDownload(
        Uri Uri,
        string TargetPath,
        string Sha1,
        long Size,
        string DisplayName);

    private sealed record InstallMarker(
        string ManifestSha256,
        DateTimeOffset InstalledAt);
}
