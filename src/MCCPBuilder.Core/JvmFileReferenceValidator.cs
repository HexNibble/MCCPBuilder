using MCCPBuilder.Models;

namespace MCCPBuilder.Core;

public static class JvmFileReferenceValidator
{
    private static readonly string[] InlinePrefixes =
    [
        "-javaagent:",
        "-javaagent=",
        "-java:",
        "-java="
    ];

    public static IReadOnlyList<string> Validate(
        ProjectConfig project,
        FileScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(scan);

        var diagnostics = new List<string>();
        var includedFiles = scan.IncludedFiles
            .Select(file => Normalize(file.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = FindReferences(project)
            .DistinctBy(
                reference => $"{reference.Option}\0{reference.ConfiguredPath}",
                StringComparer.OrdinalIgnoreCase);

        foreach (var reference in references)
        {
            ValidateReference(
                project,
                includedFiles,
                reference,
                diagnostics);
        }

        return diagnostics;
    }

    private static IEnumerable<JvmFileReference> FindReferences(
        ProjectConfig project)
    {
        var tokens = project.Java.Arguments
            .Concat(project.Launch.GcArguments)
            .Concat(project.Launch.JvmArguments)
            .SelectMany(MinecraftLaunchProfileService.TokenizeCommandLine)
            .ToArray();

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var prefix = InlinePrefixes.FirstOrDefault(candidate =>
                token.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (prefix is not null)
            {
                var option = prefix.StartsWith(
                    "-javaagent",
                    StringComparison.OrdinalIgnoreCase)
                    ? "-javaagent"
                    : "-java";
                yield return new(
                    option,
                    ExtractPath(token[prefix.Length..], option));
                continue;
            }

            if (!token.Equals("-javaagent", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("-java", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("-jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var configuredPath = index + 1 < tokens.Length
                ? tokens[++index]
                : "";
            var separateOption = token.Equals("-javaagent", StringComparison.OrdinalIgnoreCase)
                ? "-javaagent"
                : token.Equals("-jar", StringComparison.OrdinalIgnoreCase)
                    ? "-jar"
                    : "-java";
            yield return new(
                separateOption,
                ExtractPath(configuredPath, token));
        }
    }

    private static string ExtractPath(string value, string option)
    {
        var trimmed = value.Trim().Trim('"');
        if (option.Equals("-javaagent", StringComparison.OrdinalIgnoreCase))
        {
            var optionSeparator = trimmed.IndexOf('=');
            if (optionSeparator >= 0)
            {
                trimmed = trimmed[..optionSeparator];
            }
        }

        return trimmed.Trim().Trim('"');
    }

    private static void ValidateReference(
        ProjectConfig project,
        IReadOnlySet<string> includedFiles,
        JvmFileReference reference,
        ICollection<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(reference.ConfiguredPath))
        {
            diagnostics.Add($"JVM 参数 {reference.Option} 缺少文件路径。");
            return;
        }

        if (Path.IsPathRooted(reference.ConfiguredPath))
        {
            diagnostics.Add(
                $"JVM 参数 {reference.Option} 引用文件必须使用相对于所选版本目录的路径，" +
                $"不能使用打包电脑的绝对路径：{reference.ConfiguredPath}");
            return;
        }

        string sourceRoot;
        string sourcePath;
        try
        {
            sourceRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(project.Client.SourceDirectory));
            sourcePath = ResolveSourcePath(project, reference.ConfiguredPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Add(
                $"JVM 参数 {reference.Option} 文件路径无效：" +
                $"{reference.ConfiguredPath}（{exception.Message}）");
            return;
        }

        if (!InputValidator.IsPathInside(sourceRoot, sourcePath))
        {
            diagnostics.Add(
                $"JVM 参数 {reference.Option} 引用文件必须位于客户端源目录内：" +
                reference.ConfiguredPath);
            return;
        }

        if (!File.Exists(sourcePath))
        {
            diagnostics.Add(
                $"JVM 参数 {reference.Option} 引用文件不存在：" +
                $"{reference.ConfiguredPath}（解析为 {sourcePath}）");
            return;
        }

        var relativePath = Normalize(Path.GetRelativePath(sourceRoot, sourcePath));
        if (!includedFiles.Contains(relativePath))
        {
            diagnostics.Add(
                $"JVM 参数 {reference.Option} 引用文件不会进入最终 Payload：" +
                $"{relativePath}（请检查包含、排除和隐私规则）");
            return;
        }

        if (!ClientPayloadService.ShouldCopy(project.Client, relativePath))
        {
            diagnostics.Add(
                $"JVM 参数 {reference.Option} 引用文件不会进入最终 Payload：" +
                $"{relativePath}（被官方游戏或资源来源规则排除）");
        }
    }

    private static string ResolveSourcePath(
        ProjectConfig project,
        string configuredPath)
    {
        var normalized = configuredPath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var token in new[] { "${MCCP_GAME_ROOT}", "${MCC_GAME_ROOT}" })
        {
            if (!normalized.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = normalized[token.Length..]
                .TrimStart(Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(
                project.Client.MinecraftRootDirectory,
                suffix));
        }

        if (Path.IsPathRooted(normalized))
        {
            return Path.GetFullPath(normalized);
        }

        var workingDirectory = string.IsNullOrWhiteSpace(
            project.Client.VersionDirectory)
            ? project.Client.SourceDirectory
            : project.Client.VersionDirectory;
        return Path.GetFullPath(Path.Combine(workingDirectory, normalized));
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').Trim('/');

    private sealed record JvmFileReference(
        string Option,
        string ConfiguredPath);
}
