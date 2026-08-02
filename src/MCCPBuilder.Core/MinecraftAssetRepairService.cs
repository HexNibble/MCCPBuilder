using System.Security.Cryptography;
using System.Text.Json;

namespace MCCPBuilder.Core;

public sealed record MinecraftAssetRepairResult(
    bool LanguageConfigured,
    bool Downloaded,
    string LanguageCode,
    string AssetIndexId,
    string ObjectPath,
    string Diagnostic);

public sealed class MinecraftAssetRepairService
{
    private static readonly Uri DefaultAssetRoot = new(
        "https://resources.download.minecraft.net/");

    private readonly HttpClient _httpClient;
    private readonly Uri _assetRoot;

    public MinecraftAssetRepairService(
        HttpClient? httpClient = null,
        Uri? assetRoot = null)
    {
        _httpClient = httpClient ?? new HttpClient(CreateHttpHandler())
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        _assetRoot = assetRoot ?? DefaultAssetRoot;
        if (!_assetRoot.IsAbsoluteUri ||
            _assetRoot.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "Minecraft 资源服务器必须使用绝对 HTTPS 地址。",
                nameof(assetRoot));
        }
    }

    internal static SocketsHttpHandler CreateHttpHandler() =>
        new()
        {
            UseProxy = false,
            MaxConnectionsPerServer = 4,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

    public async Task<MinecraftAssetRepairResult> EnsureSelectedLanguageAsync(
        string minecraftDirectory,
        string gameDirectory,
        string? assetIndexId = null,
        CancellationToken cancellationToken = default)
    {
        var minecraftRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(minecraftDirectory));
        var gameRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(gameDirectory));
        if (!Directory.Exists(minecraftRoot))
        {
            throw new DirectoryNotFoundException(
                $"Minecraft 目录不存在：{minecraftRoot}");
        }

        var language = ReadSelectedLanguage(gameRoot, minecraftRoot);
        if (string.IsNullOrEmpty(language) ||
            language.Equals("en_us", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                false,
                false,
                language,
                "",
                "",
                "未配置非英语语言，无需检查外部语言资源。");
        }

        var logicalPath = $"minecraft/lang/{language}.json";
        var asset = ResolveAsset(
            minecraftRoot,
            assetIndexId,
            logicalPath);
        var objectDirectory = Path.Combine(
            minecraftRoot,
            "assets",
            "objects",
            asset.Hash[..2]);
        var objectPath = Path.Combine(objectDirectory, asset.Hash);
        if (await IsValidObjectAsync(
                objectPath,
                asset.Hash,
                asset.Size,
                cancellationToken))
        {
            return new(
                true,
                false,
                language,
                asset.IndexId,
                objectPath,
                $"语言资源 {logicalPath} 已存在且校验正确。");
        }

        Directory.CreateDirectory(objectDirectory);
        var temporaryPath = Path.Combine(
            objectDirectory,
            $".{asset.Hash}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var response = await _httpClient.GetAsync(
                new Uri(_assetRoot, $"{asset.Hash[..2]}/{asset.Hash}"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength &&
                contentLength != asset.Size)
            {
                throw new InvalidDataException(
                    $"语言资源长度不正确：应为 {asset.Size} 字节，服务器返回 {contentLength} 字节。");
            }

            await using var source = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA1);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (total < asset.Size)
            {
                var requested = (int)Math.Min(
                    buffer.Length,
                    asset.Size - total);
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, requested),
                    cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"语言资源下载提前结束：已接收 {total} / {asset.Size} 字节。");
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
                hasher.AppendData(buffer, 0, read);
                total += read;
            }

            await destination.FlushAsync(cancellationToken);
            await destination.DisposeAsync();
            var actualHash = Convert.ToHexString(
                hasher.GetHashAndReset()).ToLowerInvariant();
            if (!actualHash.Equals(
                    asset.Hash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"语言资源 SHA-1 校验失败：应为 {asset.Hash}，实际为 {actualHash}。");
            }

            File.Move(temporaryPath, objectPath, true);
            return new(
                true,
                true,
                language,
                asset.IndexId,
                objectPath,
                $"已从 Mojang 官方资源服务器补齐语言资源 {logicalPath}。");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string ReadSelectedLanguage(
        string gameDirectory,
        string minecraftDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(gameDirectory, "options.txt"),
            Path.Combine(minecraftDirectory, "options.txt")
        };
        foreach (var path in candidates.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith("lang:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var language = line[5..].Trim().ToLowerInvariant();
                if (language.Length is < 2 or > 32 ||
                    language.Any(character =>
                        !char.IsAsciiLetterOrDigit(character) &&
                        character is not '_' and not '-'))
                {
                    throw new InvalidDataException(
                        $"options.txt 中的语言代码不合法：{language}");
                }

                return language;
            }
        }

        return "";
    }

    private static AssetReference ResolveAsset(
        string minecraftDirectory,
        string? requestedIndexId,
        string logicalPath)
    {
        var indexesDirectory = Path.Combine(
            minecraftDirectory,
            "assets",
            "indexes");
        if (!Directory.Exists(indexesDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Minecraft 资源索引目录不存在：{indexesDirectory}");
        }

        IEnumerable<string> indexPaths;
        if (!string.IsNullOrWhiteSpace(requestedIndexId))
        {
            var normalized = requestedIndexId.Trim();
            if (normalized.Length > 64 ||
                normalized.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) &&
                    character is not '.' and not '_' and not '-'))
            {
                throw new InvalidDataException(
                    $"资源索引标识不合法：{requestedIndexId}");
            }

            var requestedPath = Path.Combine(
                indexesDirectory,
                normalized + ".json");
            if (!File.Exists(requestedPath))
            {
                throw new FileNotFoundException(
                    $"Minecraft 资源索引不存在：{normalized}.json",
                    requestedPath);
            }

            indexPaths = [requestedPath];
        }
        else
        {
            indexPaths = Directory.EnumerateFiles(
                    indexesDirectory,
                    "*.json",
                    SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var indexPath in indexPaths)
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(indexPath));
            if (!document.RootElement.TryGetProperty(
                    "objects",
                    out var objects) ||
                objects.ValueKind != JsonValueKind.Object ||
                !objects.TryGetProperty(logicalPath, out var entry) ||
                !entry.TryGetProperty("hash", out var hashProperty) ||
                !entry.TryGetProperty("size", out var sizeProperty))
            {
                continue;
            }

            var hash = (hashProperty.GetString() ?? "").ToLowerInvariant();
            if (hash.Length != 40 ||
                hash.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException(
                    $"资源索引中的 SHA-1 无效：{hash}");
            }

            if (!sizeProperty.TryGetInt64(out var size) ||
                size < 1 ||
                size > 64L * 1024 * 1024)
            {
                throw new InvalidDataException(
                    $"资源索引中的语言文件大小无效：{sizeProperty}");
            }

            return new(
                Path.GetFileNameWithoutExtension(indexPath),
                hash,
                size);
        }

        throw new InvalidDataException(
            $"Minecraft 资源索引中找不到语言文件：{logicalPath}");
    }

    private static async Task<bool> IsValidObjectAsync(
        string path,
        string expectedHash,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != expectedSize)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        var hash = Convert.ToHexString(
            await SHA1.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        return hash.Equals(expectedHash, StringComparison.Ordinal);
    }

    private sealed record AssetReference(
        string IndexId,
        string Hash,
        long Size);
}
