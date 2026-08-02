using MCCPBuilder.Models;
using System.Text.Json;

namespace MCCPBuilder.Core;

public sealed record MinecraftLayoutResult(
    bool IsRecognized,
    string MinecraftRootDirectory,
    string? VersionDirectory,
    IReadOnlyList<string> AvailableVersionDirectories,
    string Diagnostic);

public sealed class MinecraftLayoutService
{
    public MinecraftLayoutResult Detect(string selectedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedDirectory);
        var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedDirectory));
        if (!Directory.Exists(selected))
        {
            return new(false, "", null, [], $"目录不存在：{selected}");
        }

        string? minecraftRoot = null;
        string? selectedVersion = null;
        var selectedInfo = new DirectoryInfo(selected);

        if (selectedInfo.Name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
        {
            minecraftRoot = selected;
        }
        else if (selectedInfo.Name.Equals("versions", StringComparison.OrdinalIgnoreCase) &&
                 selectedInfo.Parent is not null)
        {
            minecraftRoot = selectedInfo.Parent.FullName;
        }
        else if (selectedInfo.Parent?.Name.Equals("versions", StringComparison.OrdinalIgnoreCase) == true &&
                 selectedInfo.Parent.Parent is not null)
        {
            minecraftRoot = selectedInfo.Parent.Parent.FullName;
            selectedVersion = selected;
        }
        else
        {
            var childMinecraft = Path.Combine(selected, ".minecraft");
            if (Directory.Exists(childMinecraft))
            {
                minecraftRoot = childMinecraft;
            }
        }

        if (minecraftRoot is null)
        {
            return new(false, "", null, [], "无法从所选目录识别 .minecraft、versions 或版本隔离目录。");
        }

        var versionsDirectory = Path.Combine(minecraftRoot, "versions");
        var versions = Directory.Exists(versionsDirectory)
            ? Directory.EnumerateDirectories(versionsDirectory)
                .Where(IsVersionDirectory)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        if (selectedVersion is null && versions.Length == 1)
        {
            selectedVersion = versions[0];
        }

        var diagnostic = selectedVersion is not null
            ? $"已识别 .minecraft 根目录和版本隔离目录：{Path.GetFileName(selectedVersion)}"
            : $"已识别 .minecraft 根目录；发现 {versions.Length} 个版本，请明确选择版本隔离目录。";
        return new(true, minecraftRoot, selectedVersion, versions, diagnostic);
    }

    public static void Apply(ClientContentOptions options, MinecraftLayoutResult layout)
    {
        if (!layout.IsRecognized)
        {
            throw new InvalidOperationException(layout.Diagnostic);
        }

        options.MinecraftRootDirectory = layout.MinecraftRootDirectory;
        options.SourceDirectory = layout.MinecraftRootDirectory;
        if (layout.VersionDirectory is not null)
        {
            options.VersionDirectory = layout.VersionDirectory;
            var manifestPath = FindVersionManifest(layout.VersionDirectory);
            if (manifestPath is not null)
            {
                options.VersionManifestPath = Path.GetRelativePath(
                    layout.MinecraftRootDirectory,
                    manifestPath);
            }

            var preferredJar = manifestPath is null
                ? null
                : Path.ChangeExtension(manifestPath, ".jar");
            var directoryJar = Path.Combine(
                layout.VersionDirectory,
                Path.GetFileName(layout.VersionDirectory) + ".jar");
            var jarPath = File.Exists(directoryJar)
                ? directoryJar
                : preferredJar;
            if (File.Exists(jarPath))
            {
                options.LaunchEntryPath = Path.GetRelativePath(layout.MinecraftRootDirectory, jarPath);
            }
        }
    }

    private static bool IsVersionDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        return File.Exists(Path.Combine(directory, name + ".json")) ||
               File.Exists(Path.Combine(directory, name + ".jar"));
    }

    private static string? FindVersionManifest(string versionDirectory)
    {
        var directoryName = Path.GetFileName(versionDirectory);
        var conventional = Path.Combine(versionDirectory, directoryName + ".json");
        if (HasMainClass(conventional))
        {
            return conventional;
        }

        return Directory.EnumerateFiles(versionDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(HasMainClass);
    }

    private static bool HasMainClass(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("mainClass", out var mainClass) &&
                   !string.IsNullOrWhiteSpace(mainClass.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
