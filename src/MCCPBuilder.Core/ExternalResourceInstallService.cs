using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace MCCPBuilder.Core;

public sealed class ExternalResourceInstallService
{
    private readonly HttpClient _httpClient;

    public ExternalResourceInstallService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            MaxConnectionsPerServer = 32,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        }) { Timeout = TimeSpan.FromMinutes(15) };
    }

    public async Task EnsureInstalledAsync(
        string applicationDirectory,
        string manifestRelativePath,
        int concurrency,
        IProgress<OfficialGameInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var appRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        var manifestPath = ResolveInside(appRoot, manifestRelativePath);
        if (!File.Exists(manifestPath)) return;
        var manifestHash = await ComputeSha256Async(manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<ResourceDownloadManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("资源下载清单为空。");
        if (manifest.Provider.Equals("CustomServer", StringComparison.OrdinalIgnoreCase)) return;
        var markerPath = Path.Combine(
            appRoot,
            ".minecraft",
            $".mccp-{manifest.Provider.ToLowerInvariant()}-resources.json");
        if (await MarkerIsCurrentAsync(markerPath, manifestHash, cancellationToken)) return;

        var completed = 0;
        var failures = new ConcurrentQueue<Exception>();
        await Parallel.ForEachAsync(
            manifest.Files,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(concurrency, 1, 32)
            },
            async (file, token) =>
            {
                try
                {
                    await EnsureFileAsync(appRoot, manifest.Provider, file, token);
                    var count = Interlocked.Increment(ref completed);
                    progress?.Report(new($"正在安装 {manifest.Provider} 内容：{file.Path}", count, manifest.Files.Count));
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            });
        if (!failures.IsEmpty)
        {
            throw new AggregateException($"{manifest.Provider} 内容下载失败。", failures);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        await File.WriteAllTextAsync(
            markerPath,
            JsonSerializer.Serialize(new ResourceMarker(manifestHash, DateTimeOffset.UtcNow)),
            cancellationToken);
    }

    private async Task EnsureFileAsync(
        string appRoot,
        string provider,
        ResourceDownloadEntry file,
        CancellationToken cancellationToken)
    {
        var target = ResolveInside(appRoot, Path.Combine(".minecraft", file.Path));
        if (await MatchesAsync(target, file, cancellationToken)) return;
        var uri = file.Downloads
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var candidate) ? candidate : null)
            .FirstOrDefault(candidate => candidate is not null && IsAllowedProviderUri(provider, candidate))
            ?? throw new InvalidDataException($"{provider} 文件没有可信的 HTTPS 下载地址：{file.Path}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            await destination.DisposeAsync();
            if (!await MatchesAsync(temporary, file, cancellationToken))
                throw new InvalidDataException($"资源文件校验失败：{file.Path}");
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static bool IsAllowedProviderUri(string provider, Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return provider.Equals("Modrinth", StringComparison.OrdinalIgnoreCase)
            ? uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)
            : provider.Equals("CurseForge", StringComparison.OrdinalIgnoreCase) &&
              (uri.Host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> MatchesAsync(
        string path,
        ResourceDownloadEntry file,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (file.Size > 0 && info.Length != file.Size)) return false;
        await using var stream = info.OpenRead();
        if (!string.IsNullOrWhiteSpace(file.Sha512))
        {
            var hash = Convert.ToHexString(await SHA512.HashDataAsync(stream, cancellationToken));
            return hash.Equals(file.Sha512, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrWhiteSpace(file.Sha1))
        {
            var hash = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken));
            return hash.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase);
        }
        return file.Size > 0;
    }

    private static string ResolveInside(string root, string relative)
    {
        if (Path.IsPathRooted(relative) ||
            relative.Replace('\\', '/').Split('/').Any(segment => segment == ".."))
            throw new InvalidDataException($"资源路径无效：{relative}");
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"资源路径超出安装目录：{relative}");
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
        CancellationToken cancellationToken)
    {
        if (!File.Exists(markerPath)) return false;
        try
        {
            var marker = JsonSerializer.Deserialize<ResourceMarker>(
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

    private sealed record ResourceMarker(
        string ManifestSha256,
        DateTimeOffset InstalledAt);
}
