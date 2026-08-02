using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using MCCPBuilder.Models;

namespace MCCPBuilder.Core;

public sealed record ResourceDownloadEntry(
    string Path,
    IReadOnlyList<string> Downloads,
    string Sha1,
    string Sha512,
    long Size);

public sealed record ResourceDownloadManifest(
    int SchemaVersion,
    string Provider,
    IReadOnlyList<ResourceDownloadEntry> Files);

public sealed class ResourcePackageService(HttpClient? httpClient = null)
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        MaxConnectionsPerServer = 8
    }) { Timeout = TimeSpan.FromMinutes(3) };

    public async Task<ResourceDownloadManifest> StageAsync(
        ClientContentOptions options,
        string launcherConfigDirectory,
        string payloadDirectory,
        string curseForgeApiKey,
        CancellationToken cancellationToken = default)
    {
        ResourceDownloadManifest manifest;
        switch (options.ResourceDelivery)
        {
            case ResourceDeliveryMode.CustomServer:
                manifest = new(1, "CustomServer", []);
                break;
            case ResourceDeliveryMode.Modrinth:
                manifest = await StageModrinthAsync(
                    options.ResourcePackagePath,
                    payloadDirectory,
                    cancellationToken);
                break;
            case ResourceDeliveryMode.CurseForge:
                manifest = await StageCurseForgeAsync(
                    options.ResourcePackagePath,
                    payloadDirectory,
                    curseForgeApiKey,
                    cancellationToken);
                break;
            default:
                throw new InvalidDataException("未知的资源下载方式。");
        }

        var destinationDirectory = Path.Combine(launcherConfigDirectory, "Resources");
        Directory.CreateDirectory(destinationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "downloads.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return manifest;
    }

    private static async Task<ResourceDownloadManifest> StageModrinthAsync(
        string packagePath,
        string payloadDirectory,
        CancellationToken cancellationToken)
    {
        using var package = OpenPackage(packagePath, ".mrpack");
        var indexEntry = package.GetEntry("modrinth.index.json")
            ?? throw new InvalidDataException("Modrinth 整合包缺少 modrinth.index.json。");
        using var indexStream = indexEntry.Open();
        using var index = await JsonDocument.ParseAsync(indexStream, cancellationToken: cancellationToken);
        var files = new List<ResourceDownloadEntry>();
        foreach (var item in index.RootElement.GetProperty("files").EnumerateArray())
        {
            if (item.TryGetProperty("env", out var env) &&
                env.TryGetProperty("client", out var client) &&
                client.GetString() == "unsupported")
            {
                continue;
            }
            var path = ValidateGameRelativePath(item.GetProperty("path").GetString() ?? "");
            var hashes = item.GetProperty("hashes");
            var downloads = item.GetProperty("downloads")
                .EnumerateArray()
                .Select(value => value.GetString() ?? "")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            if (downloads.Length == 0) throw new InvalidDataException($"Modrinth 文件没有下载地址：{path}");
            files.Add(new(
                path.Replace('\\', '/'),
                downloads,
                hashes.TryGetProperty("sha1", out var sha1) ? sha1.GetString() ?? "" : "",
                hashes.TryGetProperty("sha512", out var sha512) ? sha512.GetString() ?? "" : "",
                item.TryGetProperty("fileSize", out var size) ? size.GetInt64() : 0));
        }
        ExtractOverrides(package, payloadDirectory, "overrides/");
        ExtractOverrides(package, payloadDirectory, "client-overrides/");
        return new(1, "Modrinth", files);
    }

    private async Task<ResourceDownloadManifest> StageCurseForgeAsync(
        string packagePath,
        string payloadDirectory,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidDataException("CurseForge 下载方式需要填写仅本次构建使用的官方 API Key。");
        }
        using var package = OpenPackage(packagePath, ".zip");
        var manifestEntry = package.GetEntry("manifest.json")
            ?? throw new InvalidDataException("CurseForge 整合包缺少 manifest.json。");
        using var manifestStream = manifestEntry.Open();
        using var sourceManifest = await JsonDocument.ParseAsync(
            manifestStream,
            cancellationToken: cancellationToken);
        var files = new List<ResourceDownloadEntry>();
        foreach (var item in sourceManifest.RootElement.GetProperty("files").EnumerateArray())
        {
            if (item.TryGetProperty("required", out var required) && !required.GetBoolean()) continue;
            var projectId = item.GetProperty("projectID").GetInt32();
            var fileId = item.GetProperty("fileID").GetInt32();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.curseforge.com/v1/mods/{projectId}/files/{fileId}");
            request.Headers.Add("x-api-key", apiKey.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var data = responseJson.RootElement.GetProperty("data");
            var fileName = Path.GetFileName(data.GetProperty("fileName").GetString());
            var downloadUrl = data.TryGetProperty("downloadUrl", out var urlElement)
                ? urlElement.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                downloadUrl = await ResolveCurseForgeDownloadUrlAsync(projectId, fileId, apiKey, cancellationToken);
            }
            var sha1 = "";
            if (data.TryGetProperty("hashes", out var hashes))
            {
                foreach (var hash in hashes.EnumerateArray())
                {
                    if (hash.GetProperty("algo").GetInt32() == 1)
                    {
                        sha1 = hash.GetProperty("value").GetString() ?? "";
                    }
                }
            }
            files.Add(new(
                "mods/" + fileName,
                [downloadUrl],
                sha1,
                "",
                data.TryGetProperty("fileLength", out var length) ? length.GetInt64() : 0));
        }
        var overrides = sourceManifest.RootElement.TryGetProperty("overrides", out var overridesElement)
            ? (overridesElement.GetString() ?? "overrides").TrimEnd('/') + "/"
            : "overrides/";
        ExtractOverrides(package, payloadDirectory, overrides);
        return new(1, "CurseForge", files);
    }

    private async Task<string> ResolveCurseForgeDownloadUrlAsync(
        int projectId,
        int fileId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.curseforge.com/v1/mods/{projectId}/files/{fileId}/download-url");
        request.Headers.Add("x-api-key", apiKey.Trim());
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("data").GetString()
               ?? throw new InvalidDataException("CurseForge 未返回文件下载地址。");
    }

    private static ZipArchive OpenPackage(string path, string expectedExtension)
    {
        if (!File.Exists(path) ||
            !Path.GetExtension(path).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException($"资源包不存在或扩展名不是 {expectedExtension}。", path);
        }
        return ZipFile.OpenRead(path);
    }

    private static void ExtractOverrides(
        ZipArchive archive,
        string payloadDirectory,
        string prefix)
    {
        foreach (var entry in archive.Entries.Where(entry =>
                     entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrEmpty(entry.Name)))
        {
            var relative = ValidateGameRelativePath(entry.FullName[prefix.Length..]);
            var target = Path.GetFullPath(Path.Combine(payloadDirectory, ".minecraft", relative));
            var root = Path.GetFullPath(Path.Combine(payloadDirectory, ".minecraft"));
            if (!target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("资源包 overrides 路径越界。");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static string ValidateGameRelativePath(string path)
    {
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) ||
            normalized.Split(Path.DirectorySeparatorChar).Any(segment => segment == ".."))
        {
            throw new InvalidDataException($"资源文件路径无效：{path}");
        }
        return normalized;
    }
}
