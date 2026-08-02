using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MCCPBuilder.Core;

public sealed class ForgeBrandingService
{
    private const string McVersionArgument = "--fml.mcVersion";
    private const string ForgeVersionArgument = "--fml.forgeVersion";
    private const string McpManifestSection = "Name: net/minecraftforge/versions/mcp/";
    private const string ImplementationVersionPrefix = "Implementation-Version: ";
    private const string BrandingControlClass =
        "net/minecraftforge/internal/BrandingControl.class";
    private static readonly byte[] McpBrandingRecipe = "MCP \u0001"u8.ToArray();
    private static readonly byte[] CustomBrandingRecipe = [0x01];

    public async Task<string> ApplyAsync(
        Models.ProjectConfig project,
        string stagedPayloadRoot,
        CancellationToken cancellationToken = default)
    {
        if (!InputValidator.IsValidForgeMcpBrandingText(project.Launch.ForgeMcpBrandingText))
        {
            throw new InvalidDataException(
                "自定义 Forge 标识不能为空、不能包含控制字符，且最多 48 个字符。");
        }

        var relativePath = ResolveForgeUniversalRelativePath(project);
        var payloadRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedPayloadRoot));
        var jarPath = Path.GetFullPath(Path.Combine(payloadRoot, ".minecraft", relativePath));
        if (!InputValidator.IsPathInside(payloadRoot, jarPath))
        {
            throw new InvalidDataException("Forge JAR 路径超出暂存 Payload。");
        }
        if (!File.Exists(jarPath))
        {
            throw new FileNotFoundException("打包副本中缺少 Forge Universal JAR。", jarPath);
        }

        await RewriteForgeJarAsync(
            jarPath,
            project.Launch.ForgeMcpBrandingText.Trim(),
            cancellationToken);
        return Path.Combine(".minecraft", relativePath);
    }

    public static string ResolveForgeUniversalRelativePath(Models.ProjectConfig project)
    {
        var manifestPath = MinecraftLaunchProfileService.ResolveManifest(project);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("arguments", out var arguments) ||
            !arguments.TryGetProperty("game", out var gameArguments))
        {
            throw new InvalidDataException("所选版本不是受支持的现代 Forge 客户端：缺少 Forge 游戏参数。");
        }

        var values = gameArguments
            .EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString() ?? "")
            .ToArray();
        var minecraftVersion = ReadArgumentValue(values, McVersionArgument);
        var forgeVersion = ReadArgumentValue(values, ForgeVersionArgument);
        if (string.IsNullOrWhiteSpace(minecraftVersion) ||
            string.IsNullOrWhiteSpace(forgeVersion) ||
            minecraftVersion.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            forgeVersion.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("无法从版本 JSON 识别 Forge 和 Minecraft 版本。");
        }

        var combinedVersion = $"{minecraftVersion}-{forgeVersion}";
        return Path.Combine(
            "libraries",
            "net",
            "minecraftforge",
            "forge",
            combinedVersion,
            $"forge-{combinedVersion}-universal.jar");
    }

    private static string ReadArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return "";
    }

    private static async Task RewriteForgeJarAsync(
        string jarPath,
        string displayText,
        CancellationToken cancellationToken)
    {
        var temporaryPath = jarPath + $".mccbranding.{Guid.NewGuid():N}.tmp";
        var manifestFound = false;
        var brandingClassFound = false;
        try
        {
            await using (var sourceStream = new FileStream(
                             jarPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var sourceArchive = new ZipArchive(
                       sourceStream,
                       ZipArchiveMode.Read,
                       leaveOpen: false))
            await using (var destinationStream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            using (var destinationArchive = new ZipArchive(
                       destinationStream,
                       ZipArchiveMode.Create,
                       leaveOpen: false))
            {
                foreach (var sourceEntry in sourceArchive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destinationEntry = destinationArchive.CreateEntry(
                        sourceEntry.FullName,
                        CompressionLevel.Optimal);
                    destinationEntry.LastWriteTime = sourceEntry.LastWriteTime;
                    destinationEntry.ExternalAttributes = sourceEntry.ExternalAttributes;
                    if (string.IsNullOrEmpty(sourceEntry.Name))
                    {
                        continue;
                    }

                    await using var sourceEntryStream = sourceEntry.Open();
                    await using var destinationEntryStream = destinationEntry.Open();
                    if (sourceEntry.FullName.Equals(
                            "META-INF/MANIFEST.MF",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        using var reader = new StreamReader(
                            sourceEntryStream,
                            Encoding.UTF8,
                            detectEncodingFromByteOrderMarks: true,
                            leaveOpen: true);
                        var manifest = await reader.ReadToEndAsync(cancellationToken);
                        var rewritten = RewriteManifest(manifest, displayText);
                        var bytes = new UTF8Encoding(false).GetBytes(rewritten);
                        await destinationEntryStream.WriteAsync(bytes, cancellationToken);
                        manifestFound = true;
                    }
                    else if (sourceEntry.FullName.Equals(
                                 BrandingControlClass,
                                 StringComparison.Ordinal))
                    {
                        using var memory = new MemoryStream();
                        await sourceEntryStream.CopyToAsync(memory, cancellationToken);
                        var patched = RemoveHardCodedMcpPrefix(memory.ToArray());
                        await destinationEntryStream.WriteAsync(patched, cancellationToken);
                        brandingClassFound = true;
                    }
                    else
                    {
                        await sourceEntryStream.CopyToAsync(destinationEntryStream, cancellationToken);
                    }
                }
            }

            if (!manifestFound)
            {
                throw new InvalidDataException("Forge Universal JAR 缺少 META-INF/MANIFEST.MF。");
            }
            if (!brandingClassFound)
            {
                throw new InvalidDataException(
                    "Forge Universal JAR 缺少 BrandingControl.class，无法移除 MCP 前缀。");
            }

            File.Move(temporaryPath, jarPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static byte[] RemoveHardCodedMcpPrefix(byte[] classFile)
    {
        if (classFile.Length < 10 ||
            BinaryPrimitives.ReadUInt32BigEndian(classFile.AsSpan(0, 4)) != 0xCAFEBABE)
        {
            throw new InvalidDataException("Forge BrandingControl.class 格式无效。");
        }

        var constantPoolCount = BinaryPrimitives.ReadUInt16BigEndian(classFile.AsSpan(8, 2));
        var offset = 10;
        for (var index = 1; index < constantPoolCount; index++)
        {
            EnsureAvailable(classFile, offset, 1);
            var tag = classFile[offset++];
            switch (tag)
            {
                case 1:
                {
                    EnsureAvailable(classFile, offset, 2);
                    var lengthOffset = offset;
                    var length = BinaryPrimitives.ReadUInt16BigEndian(classFile.AsSpan(offset, 2));
                    offset += 2;
                    EnsureAvailable(classFile, offset, length);
                    if (classFile.AsSpan(offset, length).SequenceEqual(McpBrandingRecipe))
                    {
                        var patched = new byte[
                            classFile.Length - length + CustomBrandingRecipe.Length];
                        classFile.AsSpan(0, lengthOffset).CopyTo(patched);
                        BinaryPrimitives.WriteUInt16BigEndian(
                            patched.AsSpan(lengthOffset, 2),
                            checked((ushort)CustomBrandingRecipe.Length));
                        CustomBrandingRecipe.CopyTo(patched, lengthOffset + 2);
                        classFile.AsSpan(offset + length).CopyTo(
                            patched.AsSpan(lengthOffset + 2 + CustomBrandingRecipe.Length));
                        return patched;
                    }
                    offset += length;
                    break;
                }
                case 3:
                case 4:
                case 9:
                case 10:
                case 11:
                case 12:
                case 17:
                case 18:
                    offset += 4;
                    break;
                case 5:
                case 6:
                    offset += 8;
                    index++;
                    break;
                case 7:
                case 8:
                case 16:
                case 19:
                case 20:
                    offset += 2;
                    break;
                case 15:
                    offset += 3;
                    break;
                default:
                    throw new InvalidDataException(
                        $"Forge BrandingControl.class 包含不支持的常量池标记：{tag}。");
            }

            EnsureAvailable(classFile, offset, 0);
        }

        throw new InvalidDataException(
            "当前 Forge BrandingControl.class 中未找到硬编码的 MCP 前缀。");
    }

    private static void EnsureAvailable(byte[] bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
        {
            throw new InvalidDataException("Forge BrandingControl.class 常量池不完整。");
        }
    }

    private static string RewriteManifest(string manifest, string displayText)
    {
        var normalized = manifest.Replace("\r\n", "\n", StringComparison.Ordinal);
        var sections = normalized.Split("\n\n", StringSplitOptions.None);
        var sectionIndex = Array.FindIndex(
            sections,
            section => section.StartsWith(McpManifestSection, StringComparison.Ordinal));
        if (sectionIndex < 0)
        {
            throw new InvalidDataException("Forge JAR 清单中缺少 MCP 版本区段。");
        }

        var lines = sections[sectionIndex].Split('\n').ToList();
        var versionIndex = lines.FindIndex(
            line => line.StartsWith(ImplementationVersionPrefix, StringComparison.Ordinal));
        if (versionIndex < 0)
        {
            throw new InvalidDataException("Forge JAR 清单中缺少 MCP Implementation-Version。");
        }

        var removeIndex = versionIndex + 1;
        while (removeIndex < lines.Count && lines[removeIndex].StartsWith(' '))
        {
            lines.RemoveAt(removeIndex);
        }
        lines[versionIndex] = FoldManifestAttribute(ImplementationVersionPrefix, displayText);
        sections[sectionIndex] = string.Join("\n", lines);
        return string.Join("\r\n\r\n", sections).TrimEnd('\r', '\n') + "\r\n";
    }

    private static string FoldManifestAttribute(string prefix, string value)
    {
        const int maximumLineBytes = 70;
        var lines = new List<string>();
        var current = new StringBuilder(prefix);
        var currentBytes = Encoding.UTF8.GetByteCount(prefix);
        foreach (var rune in value.EnumerateRunes())
        {
            var runeText = rune.ToString();
            var runeBytes = Encoding.UTF8.GetByteCount(runeText);
            if (currentBytes + runeBytes > maximumLineBytes)
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(' ');
                currentBytes = 1;
            }

            current.Append(runeText);
            currentBytes += runeBytes;
        }
        lines.Add(current.ToString());
        return string.Join("\n", lines);
    }
}
