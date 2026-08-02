using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MCCPBuilder.Core;
using MCCPBuilder.Models;
using MCCPBuilder.Packaging;

namespace MCCPBuilder.Tests;

public sealed class JavaArchiveServiceTests : IDisposable
{
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), "MCCPBuilderJavaArchiveTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InspectAndStage_StripsSingleJreRootAndCreatesJavaDirectory()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var archivePath = Path.Combine(_temporaryDirectory, "中文 JRE 17.zip");
        CreateValidJreArchive(archivePath, "jdk-17.0.12+7/");
        var options = CreateOptions(archivePath);
        var service = new JavaArchiveService();

        var inspection = await service.InspectAsync(options);
        var payload = Path.Combine(_temporaryDirectory, "ClientPayload");
        await service.StageAsync(options, payload);

        Assert.True(inspection.IsValid, string.Join(Environment.NewLine, inspection.Errors));
        Assert.Equal("jdk-17.0.12+7/", inspection.RootPrefix);
        Assert.Equal(17, inspection.MajorVersion);
        Assert.Equal("x64", inspection.Architecture);
        Assert.True(File.Exists(Path.Combine(payload, "JAVA", "bin", "java.exe")));
        Assert.True(File.Exists(Path.Combine(payload, "JAVA", "bin", "server", "jvm.dll")));
        Assert.False(Directory.Exists(Path.Combine(payload, "jdk-17.0.12+7")));
    }

    [Fact]
    public async Task Inspect_RejectsPathTraversalEntry()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var archivePath = Path.Combine(_temporaryDirectory, "unsafe.zip");
        CreateValidJreArchive(archivePath, "runtime/", archive =>
            archive.CreateEntry("runtime/../../outside.txt"));

        var inspection = await new JavaArchiveService().InspectAsync(CreateOptions(archivePath));

        Assert.False(inspection.IsValid);
        Assert.Contains(inspection.Errors, error => error.Contains("不安全路径", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspect_RejectsWrongArchitecture()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var archivePath = Path.Combine(_temporaryDirectory, "arm.zip");
        CreateValidJreArchive(archivePath, "", architecture: "aarch64");

        var inspection = await new JavaArchiveService().InspectAsync(CreateOptions(archivePath));

        Assert.False(inspection.IsValid);
        Assert.Contains(inspection.Errors, error => error.Contains("要求为 x64", StringComparison.Ordinal));
    }

    [Fact]
    public void LauncherConfig_AlwaysUsesBundledJavaWithoutFallback()
    {
        var project = new ProjectConfig
        {
            Client = new() { LaunchEntryPath = @"game\client.jar" }
        };

        using var document = JsonDocument.Parse(new LauncherConfigGenerator().Generate(project));
        var java = document.RootElement.GetProperty("java");

        Assert.Equal(@"JAVA\bin\java.exe", java.GetProperty("executable").GetString());
        Assert.Equal("JAVA", java.GetProperty("home").GetString());
        Assert.False(java.GetProperty("allowSystemJavaFallback").GetBoolean());
    }

    [Theory]
    [InlineData("JAVA_VERSION=\"1.8.0_421\"", 8)]
    [InlineData("JAVA_VERSION=\"17.0.12\"", 17)]
    [InlineData("JAVA_VERSION=\"21\"", 21)]
    public void ParseReleaseMajorVersion_HandlesCommonVersions(string release, int expected) =>
        Assert.Equal(expected, JavaArchiveService.ParseReleaseMajorVersion(release));

    private static JavaOptions CreateOptions(string archivePath) => new()
    {
        Mode = JavaMode.Bundled,
        JavaArchivePath = archivePath,
        BundleJava = true,
        ForceConfiguredJava = true,
        MinimumMajorVersion = 17,
        MaximumMajorVersion = 21,
        RequiredArchitecture = "x64"
    };

    private static void CreateValidJreArchive(
        string archivePath,
        string root,
        Action<ZipArchive>? customize = null,
        string architecture = "amd64")
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        WriteEntry(archive, root + "release", $"JAVA_VERSION=\"17.0.12\"{Environment.NewLine}OS_ARCH=\"{architecture}\"");
        WriteEntry(archive, root + "bin/java.exe", "java");
        WriteEntry(archive, root + "bin/javaw.exe", "javaw");
        WriteEntry(archive, root + "bin/server/jvm.dll", "jvm");
        WriteEntry(archive, root + "legal/java.base/LICENSE", "license");
        customize?.Invoke(archive);
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, true);
        }
    }
}
