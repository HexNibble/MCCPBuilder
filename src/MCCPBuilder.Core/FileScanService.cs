using System.Text.RegularExpressions;
using MCCPBuilder.Models;

namespace MCCPBuilder.Core;

public sealed record ScannedFile(string RelativePath, long Size);

public sealed record FileScanResult(
    IReadOnlyList<ScannedFile> IncludedFiles,
    IReadOnlyList<string> ExcludedFiles,
    IReadOnlyList<string> Errors);

public sealed class FileScanService
{
    public static IReadOnlyList<string> MandatorySensitiveExclusionPatterns { get; } =
    [
        "**/launcher_accounts.json",
        "**/launcher_msa_credentials.bin",
        "**/accounts.json",
        "**/launcher_profiles.json",
        "**/usercache.json",
        "**/nide8auth.cache",
        "**/PCL.ini",
        "**/PCL/**",
        "**/screenshots/**",
        "**/cookies*",
        "**/*token*",
        "**/*credential*",
        "**/*password*"
    ];

    public async Task<FileScanResult> ScanAsync(
        ClientContentOptions options,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(options.SourceDirectory))
        {
            throw new DirectoryNotFoundException($"客户端源目录不存在：{options.SourceDirectory}");
        }

        return await Task.Run(() =>
        {
            var included = new List<ScannedFile>();
            var excluded = new List<string>();
            var errors = new List<string>();
            var root = Path.GetFullPath(options.SourceDirectory);
            var files = EnumerateFilesSafe(root, errors).ToList();

            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[index];
                var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!InputValidator.IsPathInside(root, file))
                {
                    errors.Add($"检测到越界路径：{file}");
                }
                else if (IsMandatorySensitiveFile(relativePath))
                {
                    excluded.Add(relativePath);
                }
                else if (!IsAllowedBySelectedVersion(options, relativePath))
                {
                    excluded.Add(relativePath);
                }
                else if (ShouldInclude(relativePath, options.IncludeRules, options.ExcludeRules))
                {
                    try
                    {
                        included.Add(new ScannedFile(relativePath, new FileInfo(file).Length));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        errors.Add($"{relativePath}: {exception.Message}");
                    }
                }
                else
                {
                    excluded.Add(relativePath);
                }

                progress?.Report(files.Count == 0 ? 100 : (index + 1) * 100 / files.Count);
            }

            return new FileScanResult(included, excluded, errors);
        }, cancellationToken);
    }

    public static bool ShouldInclude(string relativePath, IEnumerable<string> includeRules, IEnumerable<string> excludeRules)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var includes = includeRules.Where(rule => !string.IsNullOrWhiteSpace(rule)).ToArray();
        var included = includes.Length == 0 || includes.Any(rule => WildcardMatch(normalized, rule));
        return included && !excludeRules.Where(rule => !string.IsNullOrWhiteSpace(rule)).Any(rule => WildcardMatch(normalized, rule));
    }

    public static bool IsMandatorySensitiveFile(string relativePath) =>
        MandatorySensitiveExclusionPatterns.Any(pattern =>
            WildcardMatch(relativePath, pattern));

    public static bool IsAllowedBySelectedVersion(
        ClientContentOptions options,
        string relativePath)
    {
        if (string.IsNullOrWhiteSpace(options.SourceDirectory) ||
            string.IsNullOrWhiteSpace(options.VersionDirectory))
        {
            return true;
        }

        string selectedVersionRelativePath;
        try
        {
            var sourceRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(options.SourceDirectory));
            var selectedVersion = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(options.VersionDirectory));
            if (!InputValidator.IsPathInside(sourceRoot, selectedVersion))
            {
                return true;
            }

            selectedVersionRelativePath = Path.GetRelativePath(
                    sourceRoot,
                    selectedVersion)
                .Replace('\\', '/')
                .Trim('/');
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        var selectedParts = selectedVersionRelativePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (selectedParts.Length != 2 ||
            !selectedParts[0].Equals("versions", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (!normalized.StartsWith("versions/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.Equals(
                   selectedVersionRelativePath,
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   selectedVersionRelativePath + "/",
                   StringComparison.OrdinalIgnoreCase);
    }

    public static bool WildcardMatch(string relativePath, string pattern)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        var normalizedPattern = pattern.Replace('\\', '/').TrimStart('/');
        var regex = "^" + Regex.Escape(normalizedPattern)
            .Replace(@"\*\*/", @"(?:.*/)?")
            .Replace(@"\*\*", @".*")
            .Replace(@"\*", @"[^/]*")
            .Replace(@"\?", @"[^/]") + "$";
        return Regex.IsMatch(normalizedPath, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, ICollection<string> errors)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(directory).ToArray();
                directories = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{directory}: {exception.Message}");
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var childDirectory in directories)
            {
                try
                {
                    if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(childDirectory);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{childDirectory}: {exception.Message}");
                }
            }
        }
    }
}
