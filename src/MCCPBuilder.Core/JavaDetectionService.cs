using System.Diagnostics;
using System.Text.RegularExpressions;
using MCCPBuilder.Models;

namespace MCCPBuilder.Core;

public sealed record JavaDetectionResult(
    bool IsValid,
    string ExecutablePath,
    int? MajorVersion,
    string Architecture,
    string Diagnostic);

public sealed partial class JavaDetectionService
{
    public async Task<JavaDetectionResult> ValidateAsync(JavaOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Mode == JavaMode.Bundled)
        {
            var inspection = await new JavaArchiveService().InspectAsync(options, cancellationToken);
            return new(
                inspection.IsValid,
                $"{options.JavaArchivePath}!/{inspection.RootPrefix}bin/java.exe",
                inspection.MajorVersion,
                inspection.Architecture,
                inspection.IsValid
                    ? $"JRE ZIP 有效：Java {inspection.MajorVersion}，{inspection.Architecture}，{inspection.FileCount} 个文件"
                    : string.Join("；", inspection.Errors));
        }

        var executable = ResolveExecutable(options);
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return new(false, executable ?? "", null, "", "未找到 java.exe。");
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    ArgumentList = { "-XshowSettings:properties", "-version" },
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await standardErrorTask) + Environment.NewLine + (await standardOutputTask);
            var major = ParseMajorVersion(output);
            var architecture = ParseArchitecture(output);
            var versionValid = major is not null &&
                (!options.EnforceVersion || major >= options.MinimumMajorVersion && major <= options.MaximumMajorVersion);
            var architectureValid = string.IsNullOrWhiteSpace(options.RequiredArchitecture) ||
                architecture.Equals(options.RequiredArchitecture, StringComparison.OrdinalIgnoreCase);

            return new(
                process.ExitCode == 0 && versionValid && architectureValid,
                executable,
                major,
                architecture,
                process.ExitCode == 0
                    ? $"Java {major?.ToString() ?? "未知"}，{architecture}"
                    : $"Java 进程退出代码：{process.ExitCode}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(false, executable, null, "", exception.Message);
        }
    }

    public static int? ParseMajorVersion(string output)
    {
        var match = VersionRegex().Match(output);
        if (!match.Success)
        {
            return null;
        }

        var first = int.Parse(match.Groups[1].Value);
        return first == 1 && match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : first;
    }

    public static string ParseArchitecture(string output)
    {
        if (output.Contains("amd64", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("x86_64", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase))
        {
            return "x64";
        }

        return output.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x86";
    }

    private static string? ResolveExecutable(JavaOptions options)
    {
        if (options.Mode == JavaMode.SpecifiedDirectory)
        {
            return Path.GetFullPath(Path.Combine(options.JavaHome, options.JavaExecutableRelativePath));
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        return path?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, "java.exe"))
            .FirstOrDefault(File.Exists);
    }

    [GeneratedRegex(@"(?:java|openjdk) version ""(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
