using System.IO.Compression;
using System.Text.RegularExpressions;
using MCCPBuilder.Models;

namespace MCCPBuilder.Core;

public sealed record JavaArchiveInspection(
    bool IsValid,
    string RootPrefix,
    int? MajorVersion,
    string Architecture,
    int FileCount,
    long ExpandedSize,
    IReadOnlyList<string> Errors);

public sealed partial class JavaArchiveService
{
    private const long MaximumExpandedSize = 4L * 1024 * 1024 * 1024;

    public Task<JavaArchiveInspection> InspectAsync(
        JavaOptions options,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(options, cancellationToken), cancellationToken);

    public async Task<JavaArchiveInspection> StageAsync(
        JavaOptions options,
        string payloadDirectory,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(options, cancellationToken);
        if (!inspection.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, inspection.Errors));
        }

        var payloadRoot = Path.GetFullPath(payloadDirectory);
        Directory.CreateDirectory(payloadRoot);
        var finalJavaDirectory = Path.Combine(payloadRoot, "JAVA");
        var temporaryJavaDirectory = Path.Combine(payloadRoot, $".JAVA.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryJavaDirectory);

        try
        {
            using var archive = ZipFile.OpenRead(Path.GetFullPath(options.JavaArchivePath));
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedName = NormalizeEntryName(entry.FullName);
                if (!normalizedName.StartsWith(inspection.RootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativeName = normalizedName[inspection.RootPrefix.Length..];
                if (string.IsNullOrEmpty(relativeName))
                {
                    continue;
                }

                EnsureSafeEntry(entry, relativeName);
                var destinationPath = Path.GetFullPath(Path.Combine(
                    temporaryJavaDirectory,
                    relativeName.Replace('/', Path.DirectorySeparatorChar)));
                EnsureInsideDirectory(temporaryJavaDirectory, destinationPath);

                if (normalizedName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                entry.ExtractToFile(destinationPath, false);
            }

            var stagedJava = Path.Combine(temporaryJavaDirectory, "bin", "java.exe");
            if (!File.Exists(stagedJava))
            {
                throw new InvalidDataException("JRE 解压后缺少 bin\\java.exe。");
            }

            if (Directory.Exists(finalJavaDirectory))
            {
                Directory.Delete(finalJavaDirectory, true);
            }

            Directory.Move(temporaryJavaDirectory, finalJavaDirectory);
            return inspection;
        }
        catch
        {
            if (Directory.Exists(temporaryJavaDirectory))
            {
                Directory.Delete(temporaryJavaDirectory, true);
            }

            throw;
        }
    }

    private static JavaArchiveInspection Inspect(JavaOptions options, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.JavaArchivePath))
        {
            errors.Add("请选择 JRE ZIP 压缩文件。");
            return Invalid(errors);
        }

        var archivePath = Path.GetFullPath(options.JavaArchivePath);
        if (!File.Exists(archivePath))
        {
            errors.Add($"JRE ZIP 不存在：{archivePath}");
            return Invalid(errors);
        }

        if (!string.Equals(Path.GetExtension(archivePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("JRE 文件必须是 .zip 压缩文件。");
            return Invalid(errors);
        }

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var safeEntries = new List<(ZipArchiveEntry Entry, string Name)>();
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = NormalizeEntryName(entry.FullName);
                try
                {
                    EnsureSafeEntry(entry, name);
                    safeEntries.Add((entry, name));
                }
                catch (InvalidDataException exception)
                {
                    errors.Add(exception.Message);
                }
            }

            var roots = safeEntries
                .Where(item => item.Name.Equals("bin/java.exe", StringComparison.OrdinalIgnoreCase) ||
                               item.Name.EndsWith("/bin/java.exe", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Name[..^"bin/java.exe".Length])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (roots.Length != 1)
            {
                errors.Add(roots.Length == 0
                    ? "ZIP 中未找到唯一的 bin\\java.exe。"
                    : "ZIP 中包含多个 JRE 根目录，无法确定要打包的 JRE。");
                return Invalid(errors);
            }

            var root = roots[0];
            var jreEntries = safeEntries
                .Where(item => item.Name.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var expandedSize = jreEntries.Sum(item => item.Entry.Length);
            if (expandedSize > MaximumExpandedSize)
            {
                errors.Add($"JRE 解压大小超过安全上限 {MaximumExpandedSize / 1024 / 1024} MB。");
            }

            if (!jreEntries.Any(item => item.Name.Equals(root + "bin/javaw.exe", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("JRE 不完整：缺少 bin\\javaw.exe。");
            }

            if (!jreEntries.Any(item => item.Name.Equals(root + "bin/server/jvm.dll", StringComparison.OrdinalIgnoreCase) ||
                                        item.Name.Equals(root + "bin/client/jvm.dll", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("JRE 不完整：缺少 JVM 动态库 bin\\server\\jvm.dll。");
            }

            var releaseEntry = jreEntries.FirstOrDefault(item =>
                item.Name.Equals(root + "release", StringComparison.OrdinalIgnoreCase)).Entry;
            int? majorVersion = null;
            var architecture = "";
            if (releaseEntry is null)
            {
                errors.Add("JRE 不完整：缺少 release 版本信息文件。");
            }
            else
            {
                using var reader = new StreamReader(releaseEntry.Open());
                var release = reader.ReadToEnd();
                majorVersion = ParseReleaseMajorVersion(release);
                architecture = ParseReleaseArchitecture(release);
                if (majorVersion is null)
                {
                    errors.Add("无法从 JRE release 文件读取 Java 版本。");
                }
                else if (options.EnforceVersion &&
                         (majorVersion < options.MinimumMajorVersion || majorVersion > options.MaximumMajorVersion))
                {
                    errors.Add($"JRE 版本 {majorVersion} 不在要求范围 {options.MinimumMajorVersion}-{options.MaximumMajorVersion}。");
                }

                if (!string.IsNullOrWhiteSpace(options.RequiredArchitecture) &&
                    !architecture.Equals(options.RequiredArchitecture, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"JRE 架构为 {architecture}，要求为 {options.RequiredArchitecture}。");
                }
            }

            return new(
                errors.Count == 0,
                root,
                majorVersion,
                architecture,
                jreEntries.Count(item => !item.Name.EndsWith('/')),
                expandedSize,
                errors);
        }
        catch (InvalidDataException exception)
        {
            errors.Add($"JRE ZIP 无效或已损坏：{exception.Message}");
            return Invalid(errors);
        }
        catch (IOException exception)
        {
            errors.Add($"无法读取 JRE ZIP：{exception.Message}");
            return Invalid(errors);
        }
    }

    public static int? ParseReleaseMajorVersion(string releaseContent)
    {
        var match = JavaVersionRegex().Match(releaseContent);
        if (!match.Success)
        {
            return null;
        }

        var first = int.Parse(match.Groups[1].Value);
        return first == 1 && match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : first;
    }

    public static string ParseReleaseArchitecture(string releaseContent)
    {
        var match = ArchitectureRegex().Match(releaseContent);
        if (!match.Success)
        {
            return "";
        }

        return match.Groups[1].Value.ToLowerInvariant() switch
        {
            "amd64" or "x86_64" or "x64" => "x64",
            "aarch64" or "arm64" => "arm64",
            _ => "x86"
        };
    }

    private static void EnsureSafeEntry(ZipArchiveEntry entry, string normalizedName)
    {
        if (string.IsNullOrEmpty(normalizedName))
        {
            return;
        }

        var segments = normalizedName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalizedName.StartsWith('/') ||
            Path.IsPathRooted(normalizedName) ||
            segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
        {
            throw new InvalidDataException($"ZIP 包含不安全路径：{entry.FullName}");
        }

        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixFileType == 0xA000)
        {
            throw new InvalidDataException($"ZIP 包含不允许的符号链接：{entry.FullName}");
        }
    }

    private static void EnsureInsideDirectory(string rootDirectory, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)) + Path.DirectorySeparatorChar;
        if (!candidatePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"ZIP 解压路径越界：{candidatePath}");
        }
    }

    private static string NormalizeEntryName(string name) => name.Replace('\\', '/');

    private static JavaArchiveInspection Invalid(IReadOnlyList<string> errors) =>
        new(false, "", null, "", 0, 0, errors);

    [GeneratedRegex(@"(?m)^JAVA_VERSION=""(\d+)(?:\.(\d+))?")]
    private static partial Regex JavaVersionRegex();

    [GeneratedRegex(@"(?m)^OS_ARCH=""([^""]+)""")]
    private static partial Regex ArchitectureRegex();
}
