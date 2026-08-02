using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MCCPBuilder.Core;

public sealed record ReleaseBundleResult(
    string ArchivePath,
    UpdateManifest Manifest,
    string Sha256);

public sealed class ReleaseBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HashSet<string> ExcludedPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Launcher.exe",
            "LauncherConfig/update.json",
            "LauncherConfig/client-files.json"
        };

    public async Task<ReleaseBundleResult> CreateAsync(
        string payloadDirectory,
        string destinationArchive,
        string productId,
        string version,
        CancellationToken cancellationToken = default)
    {
        var payloadRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(payloadDirectory));
        if (!Directory.Exists(payloadRoot))
        {
            throw new DirectoryNotFoundException(
                $"更新源目录不存在：{payloadRoot}");
        }

        var normalizedProductId = NormalizeProductId(productId);
        var normalizedVersion = (version ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalizedVersion))
        {
            throw new InvalidDataException("发布版本号不能为空。");
        }

        var files = Directory.EnumerateFiles(
                payloadRoot,
                "*",
                SearchOption.AllDirectories)
            .Select(path => new
            {
                FullPath = path,
                RelativePath = Path.GetRelativePath(payloadRoot, path)
                    .Replace('\\', '/')
            })
            .Where(file => !ExcludedPaths.Contains(file.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException("更新源目录中没有可发布的文件。");
        }

        var entries = new List<UpdateManifestEntry>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeRelativePath(file.RelativePath);
            var info = new FileInfo(file.FullPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"更新文件不能是重解析点：{file.RelativePath}");
            }

            await using var stream = new FileStream(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            entries.Add(new()
            {
                Path = file.RelativePath,
                Size = info.Length,
                Sha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken)),
                PreserveExisting =
                    UserDataPathPolicy.IsProtected(file.RelativePath)
            });
        }

        var manifest = new UpdateManifest
        {
            ProductId = normalizedProductId,
            ReleaseId = CreateReleaseId(normalizedVersion),
            Version = normalizedVersion,
            PublishedAt = DateTimeOffset.UtcNow,
            Files = entries
        };

        var archivePath = Path.GetFullPath(destinationArchive);
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        var temporaryPath =
            archivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous))
            {
                using var archive = new ZipArchive(
                    output,
                    ZipArchiveMode.Create,
                    leaveOpen: true,
                    Encoding.UTF8);
                var manifestEntry = archive.CreateEntry(
                    "manifest.json",
                    CompressionLevel.Optimal);
                await using (var manifestStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(
                        manifestStream,
                        manifest,
                        JsonOptions,
                        cancellationToken);
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(
                        "payload/" + file.RelativePath,
                        CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await using var source = new FileStream(
                        file.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(
                        entryStream,
                        1024 * 1024,
                        cancellationToken);
                }
            }

            File.Move(temporaryPath, archivePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        await using var archiveForHash = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var archiveHash = Convert.ToHexString(
            await SHA256.HashDataAsync(archiveForHash, cancellationToken));
        return new(archivePath, manifest, archiveHash);
    }

    public static string NormalizeProductId(string value)
    {
        var normalized = Regex.Replace(
            (value ?? "").Trim().ToLowerInvariant(),
            @"[^a-z0-9._-]+",
            "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new InvalidDataException(
                "更新产品标识只能包含字母、数字、点、下划线或短横线。")
            : normalized;
    }

    public static void EnsureSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.Contains('\\') ||
            path.Contains('\0') ||
            path.Split('/').Any(part =>
                string.IsNullOrWhiteSpace(part) || part is "." or ".."))
        {
            throw new InvalidDataException($"不安全的更新相对路径：{path}");
        }
    }

    private static string CreateReleaseId(string version)
    {
        var safeVersion = Regex.Replace(version, @"[^A-Za-z0-9._-]+", "-")
            .Trim('-');
        if (string.IsNullOrWhiteSpace(safeVersion))
        {
            safeVersion = "release";
        }

        return $"{safeVersion}-{DateTime.UtcNow:yyyyMMddHHmmss}-" +
               Guid.NewGuid().ToString("N")[..8];
    }
}
