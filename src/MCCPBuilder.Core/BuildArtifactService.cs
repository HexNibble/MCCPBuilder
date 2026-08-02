using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCCPBuilder.Core;

public sealed record PayloadManifestEntry(
    string RelativePath,
    long Size,
    string Sha256);

public sealed record PayloadManifest(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<PayloadManifestEntry> Files);

public sealed class BuildArtifactService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

    public async Task<PayloadManifest> GeneratePayloadManifestAsync(
        string payloadDirectory,
        CancellationToken cancellationToken = default)
    {
        var payloadRoot = GetExistingDirectory(payloadDirectory);
        var manifestPath = Path.Combine(
            payloadRoot,
            "LauncherConfig",
            "client-files.json");
        var files = Directory
            .EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).Equals(
                manifestPath,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var entries = new List<PayloadManifestEntry>(files.Length);
        long totalBytes = 0;

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(filePath);
            await using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken));
            entries.Add(new(
                Path.GetRelativePath(payloadRoot, file.FullName)
                    .Replace('\\', '/'),
                file.Length,
                hash));
            totalBytes += file.Length;
        }

        var manifest = new PayloadManifest(
            "1.0",
            DateTimeOffset.UtcNow,
            entries.Count,
            totalBytes,
            entries);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var temporaryPath =
            manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(manifest, JsonOptions),
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(temporaryPath, manifestPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return manifest;
    }

    public void PublishLauncherConfig(
        string payloadDirectory,
        string outputDirectory)
    {
        var payloadRoot = GetExistingDirectory(payloadDirectory);
        var source = Path.Combine(payloadRoot, "LauncherConfig");
        var outputRoot = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                $"Payload 中缺少 LauncherConfig：{source}");
        }

        Directory.CreateDirectory(outputRoot);
        var staging = Path.Combine(
            outputRoot,
            $".LauncherConfig.{Guid.NewGuid():N}.tmp");
        var destination = Path.Combine(outputRoot, "LauncherConfig");
        try
        {
            CopyDirectory(source, staging);
            ClientPayloadService.Publish(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    public async Task<string> WriteSha256FileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        var checksumPath = fullPath + ".sha256";
        var temporaryPath =
            checksumPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                $"{hash} *{Path.GetFileName(fullPath)}{Environment.NewLine}",
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(temporaryPath, checksumPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return hash;
    }

    private static string GetExistingDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        return Directory.Exists(fullPath)
            ? Path.TrimEndingDirectorySeparator(fullPath)
            : throw new DirectoryNotFoundException(
                $"目录不存在：{fullPath}");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            Directory.CreateDirectory(
                Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var target = Path.Combine(
                destination,
                Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
