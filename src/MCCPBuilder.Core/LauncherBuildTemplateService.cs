using System.Reflection;

namespace MCCPBuilder.Core;

public static class LauncherBuildTemplateService
{
    internal const string ResourcePrefix =
        "MCCPBuilder.LauncherBuildTemplate/";

    public static IReadOnlyList<string> GetEmbeddedFilePaths()
    {
        var assembly = typeof(LauncherBuildTemplateService).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(
                ResourcePrefix,
                StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public static async Task ExtractAsync(
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException(
                "Launcher 构建模板解压目录不能为空。",
                nameof(destinationDirectory));
        }

        var destinationRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(destinationDirectory));
        Directory.CreateDirectory(destinationRoot);
        var resources = GetEmbeddedFilePaths();
        if (resources.Count == 0)
        {
            throw new InvalidDataException(
                "打包器中没有嵌入 Launcher 构建模板。");
        }

        var assembly = typeof(LauncherBuildTemplateService).Assembly;
        foreach (var resourcePath in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = resourcePath.Replace(
                '/',
                Path.DirectorySeparatorChar);
            EnsureSafeRelativePath(relativePath);
            var destinationPath = Path.GetFullPath(Path.Combine(
                destinationRoot,
                relativePath));
            if (!destinationPath.StartsWith(
                    destinationRoot +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Launcher 构建模板路径越界：{resourcePath}");
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationPath)!);
            var resourceName = ResourcePrefix + resourcePath;
            await using var source =
                assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException(
                    $"无法读取嵌入的 Launcher 构建模板：{resourcePath}");
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 64,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken);
        }

        ValidateExtractedTemplate(destinationRoot);
    }

    public static void ValidateExtractedTemplate(string templateRoot)
    {
        var root = Path.GetFullPath(templateRoot);
        var requiredFiles = new[]
        {
            "Directory.Build.props",
            Path.Combine(
                "MCCPBuilder.Launcher",
                "MCCPBuilder.Launcher.csproj"),
            Path.Combine(
                "MCCPBuilder.Core",
                "MCCPBuilder.Core.csproj"),
            Path.Combine(
                "MCCPBuilder.Models",
                "MCCPBuilder.Models.csproj"),
            Path.Combine(
                "MCCPBuilder.Launcher",
                "Program.cs"),
            Path.Combine(
                "MCCPBuilder.Launcher",
                "LoginWindow.xaml")
        };
        var missing = requiredFiles
            .Where(path => !File.Exists(Path.Combine(root, path)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                "Launcher 构建模板不完整，缺少：" +
                string.Join("、", missing));
        }
    }

    private static void EnsureSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.Split(
                    Path.DirectorySeparatorChar,
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part is "." or ".."))
        {
            throw new InvalidDataException(
                $"不安全的 Launcher 构建模板路径：{path}");
        }
    }
}
