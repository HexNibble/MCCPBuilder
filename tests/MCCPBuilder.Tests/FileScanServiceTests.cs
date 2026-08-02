using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class FileScanServiceTests
{
    [Theory]
    [InlineData("logs/latest.log", "**/logs/**", true)]
    [InlineData("mods/example.jar", "**/*.jar", true)]
    [InlineData("config/options.txt", "config/*.txt", true)]
    [InlineData("config/sub/options.txt", "config/*.txt", false)]
    public void WildcardMatch_HandlesGlobPatterns(string path, string pattern, bool expected) =>
        Assert.Equal(expected, FileScanService.WildcardMatch(path, pattern));

    [Fact]
    public void ShouldInclude_AppliesIncludeBeforeExclude()
    {
        Assert.True(FileScanService.ShouldInclude("mods/a.jar", ["**/*"], ["**/*.log"]));
        Assert.False(FileScanService.ShouldInclude("logs/a.log", ["**/*"], ["**/*.log"]));
        Assert.False(FileScanService.ShouldInclude("config/a.txt", ["mods/**"], []));
    }

    [Theory]
    [InlineData("versions/Client/nide8auth.cache")]
    [InlineData("versions/Client/ChatImageCache/image.png")]
    [InlineData("versions/Client/usercache.json")]
    [InlineData("launcher_profiles.json")]
    [InlineData("PCL.ini")]
    [InlineData("versions/Client/screenshots/private.png")]
    public void DefaultProjectRules_ExcludeAuthenticationAndUserCaches(string relativePath)
    {
        var options = new MCCPBuilder.Models.ClientContentOptions();

        Assert.False(FileScanService.ShouldInclude(
            relativePath,
            options.IncludeRules,
            options.ExcludeRules));
    }

    [Theory]
    [InlineData("nide8auth.cache")]
    [InlineData("versions/Client/usercache.json")]
    [InlineData("launcher_profiles.json")]
    [InlineData("PCL.ini")]
    [InlineData("config/login-token.json")]
    [InlineData("versions/Client/screenshots/private.png")]
    public void MandatorySensitiveRules_CannotBeDisabledByProjectRules(string relativePath) =>
        Assert.True(FileScanService.IsMandatorySensitiveFile(relativePath));

    [Fact]
    public async Task ScanAsync_SelectedVersion_ExcludesSiblingVersionsAndKeepsSharedFiles()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "MCCPBuilderSelectedVersionScanTests",
            Guid.NewGuid().ToString("N"));
        var minecraftRoot = Path.Combine(temporaryDirectory, ".minecraft");
        var selectedVersion = Path.Combine(minecraftRoot, "versions", "最后防线");
        var siblingVersion = Path.Combine(minecraftRoot, "versions", "最后防线 - 副本");
        try
        {
            WriteFile(Path.Combine(selectedVersion, "最后防线.jar"));
            WriteFile(Path.Combine(siblingVersion, "副本.jar"));
            WriteFile(Path.Combine(minecraftRoot, "libraries", "shared.jar"));
            var options = new MCCPBuilder.Models.ClientContentOptions
            {
                SourceDirectory = minecraftRoot,
                MinecraftRootDirectory = minecraftRoot,
                VersionDirectory = selectedVersion,
                IncludeRules = ["**/*"],
                ExcludeRules = []
            };

            var result = await new FileScanService().ScanAsync(options);

            Assert.Contains(
                result.IncludedFiles,
                file => file.RelativePath == "versions/最后防线/最后防线.jar");
            Assert.Contains(
                result.IncludedFiles,
                file => file.RelativePath == "libraries/shared.jar");
            Assert.DoesNotContain(
                result.IncludedFiles,
                file => file.RelativePath.Contains("最后防线 - 副本", StringComparison.Ordinal));
            Assert.Contains(
                "versions/最后防线 - 副本/副本.jar",
                result.ExcludedFiles);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }
    }

    private static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
    }
}
