namespace MCCPBuilder.Core;

public sealed record MinecraftCleanupResult(
    int DeletedDirectoryCount,
    int DeletedFileCount,
    IReadOnlyList<string> Warnings);

public sealed class MinecraftCleanupService
{
    private static readonly HashSet<string> CacheDirectoryNames = new(
        [".cache", "cache", "caches", "modcache", "mod_cache", "mod-cache"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> LogDirectoryNames = new(
        ["logs", "crash-reports"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> RootLogFileNames = new(
        ["debug.log", "launcher_log.txt", "launcher_log0.txt"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ProtectedTopLevelDirectoryNames = new(
        ["saves", "backups", "resourcepacks", "shaderpacks", "screenshots"],
        StringComparer.OrdinalIgnoreCase);

    public MinecraftCleanupResult Clean(
        string minecraftDirectory,
        bool cleanCaches,
        bool cleanLogs)
    {
        if (string.IsNullOrWhiteSpace(minecraftDirectory))
        {
            throw new ArgumentException("Minecraft 目录不能为空。", nameof(minecraftDirectory));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(minecraftDirectory));
        if (!Directory.Exists(root) || (!cleanCaches && !cleanLogs))
        {
            return new MinecraftCleanupResult(0, 0, []);
        }

        var warnings = new List<string>();
        var deletedDirectories = 0;
        var deletedFiles = 0;
        var directoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cleanCaches)
        {
            directoryNames.UnionWith(CacheDirectoryNames);
        }
        if (cleanLogs)
        {
            directoryNames.UnionWith(LogDirectoryNames);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        var targets = Directory
            .EnumerateDirectories(root, "*", options)
            .Where(path => directoryNames.Contains(Path.GetFileName(path)))
            .Where(path => IsInside(root, path))
            .Where(path => !IsInsideProtectedDirectory(root, path))
            .OrderByDescending(path => path.Length)
            .ToArray();

        foreach (var target in targets)
        {
            if (!Directory.Exists(target))
            {
                continue;
            }

            try
            {
                Directory.Delete(target, true);
                deletedDirectories++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"无法清理目录 {Path.GetRelativePath(root, target)}：{exception.Message}");
            }
        }

        if (cleanLogs)
        {
            foreach (var fileName in RootLogFileNames)
            {
                var target = Path.Combine(root, fileName);
                if (!File.Exists(target) || !IsInside(root, target))
                {
                    continue;
                }

                try
                {
                    File.Delete(target);
                    deletedFiles++;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"无法清理文件 {fileName}：{exception.Message}");
                }
            }
        }

        return new MinecraftCleanupResult(deletedDirectories, deletedFiles, warnings);
    }

    private static bool IsInside(string root, string candidate)
    {
        var fullPath = Path.GetFullPath(candidate);
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideProtectedDirectory(string root, string candidate)
    {
        var relativePath = Path.GetRelativePath(root, candidate);
        var firstSeparator = relativePath.IndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var topLevelName = firstSeparator < 0
            ? relativePath
            : relativePath[..firstSeparator];
        return ProtectedTopLevelDirectoryNames.Contains(topLevelName);
    }
}
