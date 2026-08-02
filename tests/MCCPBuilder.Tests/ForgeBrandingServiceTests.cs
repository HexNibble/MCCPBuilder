using System.IO.Compression;
using System.Text;
using MCCPBuilder.Core;
using MCCPBuilder.Models;

namespace MCCPBuilder.Tests;

public sealed class ForgeBrandingServiceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "MCCPBuilderForgeBrandingTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ApplyAsync_RewritesStagedBrandingWithoutMcpPrefix_WithChineseText()
    {
        const string combinedVersion = "1.20.1-47.4.20";
        var sourceMinecraft = Path.Combine(_temporaryDirectory, "source", ".minecraft");
        var versionDirectory = Path.Combine(sourceMinecraft, "versions", "测试版本");
        Directory.CreateDirectory(versionDirectory);
        var versionJson = Path.Combine(versionDirectory, "forge.json");
        await File.WriteAllTextAsync(
            versionJson,
            """
            {
              "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
              "arguments": {
                "game": [
                  "--fml.forgeVersion", "47.4.20",
                  "--fml.mcVersion", "1.20.1",
                  "--fml.mcpVersion", "20230612.114412"
                ]
              }
            }
            """);

        var stagedPayload = Path.Combine(_temporaryDirectory, "payload");
        var stagedJar = Path.Combine(
            stagedPayload,
            ".minecraft",
            "libraries",
            "net",
            "minecraftforge",
            "forge",
            combinedVersion,
            $"forge-{combinedVersion}-universal.jar");
        CreateForgeJar(stagedJar, "20230612.114412");
        var untouchedEntryBefore = ReadJarEntry(stagedJar, "example.txt");
        var project = new ProjectConfig
        {
            Client = new()
            {
                SourceDirectory = sourceMinecraft,
                MinecraftRootDirectory = sourceMinecraft,
                VersionDirectory = versionDirectory,
                VersionManifestPath = Path.Combine("versions", "测试版本", "forge.json")
            },
            Launch = new()
            {
                CustomizeForgeMcpBranding = true,
                ForgeMcpBrandingText = "最后防线 2.2 自定义客户端"
            }
        };

        var relativePath = await new ForgeBrandingService().ApplyAsync(project, stagedPayload);

        Assert.Equal(
            Path.Combine(".minecraft", "libraries", "net", "minecraftforge", "forge",
                combinedVersion, $"forge-{combinedVersion}-universal.jar"),
            relativePath);
        var manifest = ReadJarEntry(stagedJar, "META-INF/MANIFEST.MF");
        Assert.Contains("Implementation-Version: 最后防线 2.2 自定义客户端", manifest);
        Assert.DoesNotContain("Implementation-Version: 20230612.114412", manifest);
        var brandingClass = ReadJarEntryBytes(
            stagedJar,
            "net/minecraftforge/internal/BrandingControl.class");
        Assert.False(ContainsSequence(brandingClass, "MCP \u0001"u8));
        Assert.True(ContainsSequence(brandingClass, [0x01]));
        Assert.Equal(untouchedEntryBefore, ReadJarEntry(stagedJar, "example.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("第一行\n第二行")]
    public async Task ApplyAsync_RejectsInvalidDisplayText(string text)
    {
        var project = new ProjectConfig
        {
            Launch = new()
            {
                CustomizeForgeMcpBranding = true,
                ForgeMcpBrandingText = text
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ForgeBrandingService().ApplyAsync(project, _temporaryDirectory));
    }

    private static void CreateForgeJar(string path, string mcpVersion)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(
            archive,
            "META-INF/MANIFEST.MF",
            "Manifest-Version: 1.0\r\n\r\n" +
            "Name: net/minecraftforge/versions/mcp/\r\n" +
            "Specification-Title: Minecraft\r\n" +
            $"Implementation-Version: {mcpVersion}\r\n\r\n");
        WriteEntry(archive, "example.txt", "保持不变");
        WriteBinaryEntry(
            archive,
            "net/minecraftforge/internal/BrandingControl.class",
            [
                0xCA, 0xFE, 0xBA, 0xBE,
                0x00, 0x00, 0x00, 0x3D,
                0x00, 0x02,
                0x01, 0x00, 0x05,
                0x4D, 0x43, 0x50, 0x20, 0x01,
                0x00, 0x21
            ]);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteBinaryEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string ReadJarEntry(string path, string name)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"缺少 {name}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] ReadJarEntryBytes(string path, string name)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"缺少 {name}");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> sequence)
    {
        for (var index = 0; index <= bytes.Length - sequence.Length; index++)
        {
            if (bytes.Slice(index, sequence.Length).SequenceEqual(sequence))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, true);
        }
    }
}
