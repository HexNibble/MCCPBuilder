using MCCPBuilder.Models;

namespace MCCPBuilder.Core;

public sealed record ClientPayloadCopyResult(int FileCount, long TotalBytes, int ExcludedFileCount);

public sealed class ClientPayloadService(FileScanService scanner)
{
    public async Task<ClientPayloadCopyResult> CopyClientAsync(
        ClientContentOptions options,
        string stagingDirectory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.SourceDirectory));
        var stagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        if (InputValidator.IsPathInside(sourceRoot, stagingRoot) ||
            sourceRoot.Equals(stagingRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Payload 临时目录不能位于客户端源目录内。");
        }

        var destinationRoot = Path.Combine(stagingRoot, ".minecraft");
        Directory.CreateDirectory(destinationRoot);
        var scan = await scanner.ScanAsync(options, cancellationToken: cancellationToken);
        if (scan.Errors.Count > 0)
        {
            throw new IOException($"客户端扫描存在无法读取的文件：{scan.Errors[0]}");
        }

        CreateDirectoryStructure(options, sourceRoot, destinationRoot, cancellationToken);
        var filesToCopy = scan.IncludedFiles
            .Where(file => ShouldCopy(options, file.RelativePath))
            .ToArray();
        long copiedBytes = 0;
        for (var index = 0; index < filesToCopy.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = filesToCopy[index];
            var platformRelativePath = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var sourcePath = Path.GetFullPath(Path.Combine(sourceRoot, platformRelativePath));
            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, platformRelativePath));
            EnsureInside(sourceRoot, sourcePath, "源文件");
            EnsureInside(destinationRoot, destinationPath, "Payload 文件");

            var attributes = File.GetAttributes(sourcePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"禁止复制符号链接或重解析点：{file.RelativePath}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken);
            copiedBytes += file.Size;
            progress?.Report(filesToCopy.Length == 0
                ? 100
                : (index + 1) * 100 / filesToCopy.Length);
        }

        return new(
            filesToCopy.Length,
            copiedBytes,
            scan.ExcludedFiles.Count + scan.IncludedFiles.Count - filesToCopy.Length);
    }

    private static void CreateDirectoryStructure(
        ClientContentOptions options,
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourceRoot, directory).Replace('\\', '/');
                if (IsExcludedDirectory(options, relativePath))
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(
                    destinationRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                EnsureInside(destinationRoot, destination, "Payload 目录");
                Directory.CreateDirectory(destination);
                pending.Push(directory);
            }
        }
    }

    private static bool IsExcludedDirectory(ClientContentOptions options, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (options.DownloadMinecraftAndForgeFromOfficialSources &&
            IsOfficialManagedGameDirectory(options, normalized))
        {
            return true;
        }

        if (options.ResourceDelivery != ResourceDeliveryMode.CustomServer &&
            IsProviderManagedResource(normalized))
        {
            return true;
        }

        var topLevel = relativePath.Split('/', 2)[0];
        if ((!options.IncludeVersions && topLevel.Equals("versions", StringComparison.OrdinalIgnoreCase)) ||
            (!options.IncludeMods && topLevel.Equals("mods", StringComparison.OrdinalIgnoreCase)) ||
            (!options.IncludeConfigs && topLevel.Equals("config", StringComparison.OrdinalIgnoreCase)) ||
            (!options.IncludeResourcePacks && topLevel.Equals("resourcepacks", StringComparison.OrdinalIgnoreCase)) ||
            (!options.IncludeShaderPacks && topLevel.Equals("shaderpacks", StringComparison.OrdinalIgnoreCase)) ||
            (!options.IncludeSaves && topLevel.Equals("saves", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!FileScanService.IsAllowedBySelectedVersion(options, relativePath))
        {
            return true;
        }

        var descendantProbe = relativePath.TrimEnd('/') + "/placeholder";
        return options.ExcludeRules.Any(rule =>
            FileScanService.WildcardMatch(relativePath, rule) ||
            FileScanService.WildcardMatch(descendantProbe, rule));
    }

    internal static bool ShouldCopy(
        ClientContentOptions options,
        string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (options.DownloadMinecraftAndForgeFromOfficialSources &&
            IsOfficialManagedGameFile(options, normalized))
        {
            return false;
        }

        if (options.ResourceDelivery != ResourceDeliveryMode.CustomServer &&
            IsProviderManagedResource(normalized))
        {
            return false;
        }

        return true;
    }

    private static bool IsOfficialManagedGameFile(
        ClientContentOptions options,
        string relativePath)
    {
        if (relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var selectedVersion = GetSelectedVersionRelativePath(options);
        if (selectedVersion is null ||
            (!relativePath.Equals(selectedVersion, StringComparison.OrdinalIgnoreCase) &&
             !relativePath.StartsWith(selectedVersion + "/", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (IsSelectedVersionNativePath(options, relativePath))
        {
            return true;
        }

        var launchEntry = NormalizeRelativePath(options.LaunchEntryPath);
        if (!string.IsNullOrEmpty(launchEntry) &&
            relativePath.Equals(launchEntry, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var manifest = GetVersionManifestRelativePath(options);
        if (manifest is not null &&
            relativePath.Equals(manifest, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var versionName = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(options.VersionDirectory));
        return relativePath.Equals(
                   $"{selectedVersion}/{versionName}.jar",
                   StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals(
                   $"{selectedVersion}/{versionName}.json",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOfficialManagedGameDirectory(
        ClientContentOptions options,
        string relativePath)
    {
        if (relativePath.Equals("assets", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Equals("libraries", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsSelectedVersionNativePath(options, relativePath);
    }

    private static bool IsSelectedVersionNativePath(
        ClientContentOptions options,
        string relativePath)
    {
        var selectedVersion = GetSelectedVersionRelativePath(options);
        if (selectedVersion is null ||
            !relativePath.StartsWith(selectedVersion + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var versionName = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(options.VersionDirectory));
        var versionRelative = relativePath[(selectedVersion.Length + 1)..];
        var firstSegment = versionRelative.Split('/', 2)[0];
        return firstSegment.Equals("natives", StringComparison.OrdinalIgnoreCase) ||
               firstSegment.Equals(
                   versionName + "-natives",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSelectedVersionRelativePath(
        ClientContentOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SourceDirectory) ||
            string.IsNullOrWhiteSpace(options.VersionDirectory))
        {
            return null;
        }

        try
        {
            var sourceRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(options.SourceDirectory));
            var selectedVersion = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(options.VersionDirectory));
            if (!InputValidator.IsPathInside(sourceRoot, selectedVersion))
            {
                return null;
            }

            return NormalizeRelativePath(
                Path.GetRelativePath(sourceRoot, selectedVersion));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? GetVersionManifestRelativePath(
        ClientContentOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SourceDirectory) ||
            string.IsNullOrWhiteSpace(options.MinecraftRootDirectory) ||
            string.IsNullOrWhiteSpace(options.VersionManifestPath))
        {
            return null;
        }

        try
        {
            var sourceRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(options.SourceDirectory));
            var manifest = Path.GetFullPath(Path.Combine(
                options.MinecraftRootDirectory,
                options.VersionManifestPath));
            if (!InputValidator.IsPathInside(sourceRoot, manifest))
            {
                return null;
            }

            return NormalizeRelativePath(Path.GetRelativePath(sourceRoot, manifest));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeRelativePath(string? path) =>
        (path ?? "").Replace('\\', '/').Trim('/');

    private static bool IsProviderManagedResource(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            segment.Equals("mods", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("resourcepacks", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("shaderpacks", StringComparison.OrdinalIgnoreCase));
    }

    public static void Publish(string stagingDirectory, string finalDirectory)
    {
        var staging = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        var final = Path.TrimEndingDirectorySeparator(Path.GetFullPath(finalDirectory));
        var finalParent = Path.GetDirectoryName(final)
            ?? throw new InvalidOperationException("最终 Payload 目录缺少父目录。");
        EnsureInside(finalParent, staging, "Payload 临时目录");
        EnsureInside(finalParent, final, "最终 Payload 目录");

        var previous = Path.Combine(finalParent, $".ClientPayload.previous.{Guid.NewGuid():N}");
        var hadPrevious = Directory.Exists(final);
        try
        {
            if (hadPrevious)
            {
                Directory.Move(final, previous);
            }

            Directory.Move(staging, final);
            if (Directory.Exists(previous))
            {
                Directory.Delete(previous, true);
            }
        }
        catch
        {
            if (!Directory.Exists(final) && Directory.Exists(previous))
            {
                Directory.Move(previous, final);
            }

            throw;
        }
    }

    private static void EnsureInside(string rootDirectory, string candidatePath, string description)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var candidate = Path.GetFullPath(candidatePath);
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{description}路径越界：{candidatePath}");
        }
    }
}
