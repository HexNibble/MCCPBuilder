using System.Text.Json;
using System.Text.Json.Serialization;
using MCCPBuilder.Models;

namespace MCCPBuilder.Core;

public sealed class ProjectFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(ProjectConfig project, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateProjectPath(filePath);
        var fullPath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        project.LastModifiedAt = DateTimeOffset.UtcNow;

        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, project, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<ProjectConfig> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ValidateProjectPath(filePath);
        await using var stream = new FileStream(Path.GetFullPath(filePath), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        var project = await JsonSerializer.DeserializeAsync<ProjectConfig>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("项目配置为空或格式无效。");
        if (project.FormatVersion != "1.0")
        {
            throw new NotSupportedException($"不支持配置格式版本 {project.FormatVersion}。");
        }

        project.Update ??= new();
        return project;
    }

    private static void ValidateProjectPath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var extension = Path.GetExtension(filePath);
        if (!string.Equals(
                extension,
                ".mccpproject",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "项目文件扩展名必须为 .mccpproject。",
                nameof(filePath));
        }
    }
}
