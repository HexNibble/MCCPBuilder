using MCCPBuilder.Core;
using System.Text.Json;

namespace MCCPBuilder.Tests;

public sealed class OfficialDownloadSecurityTests
{
    [Theory]
    [InlineData("https://piston-data.mojang.com/v1/objects/hash/client.jar")]
    [InlineData("https://libraries.minecraft.net/com/example/library.jar")]
    [InlineData("https://maven.minecraftforge.net/net/minecraftforge/forge/file.jar")]
    public void OfficialGame_AllowsOnlyConfiguredOfficialHosts(string address) =>
        OfficialGameInstallService.ValidateOfficialUri(new Uri(address));

    [Theory]
    [InlineData("http://libraries.minecraft.net/library.jar")]
    [InlineData("https://libraries.minecraft.net.evil.example/library.jar")]
    [InlineData("https://bmclapi2.bangbang93.com/version_manifest.json")]
    public void OfficialGame_RejectsNonOfficialOrInsecureHosts(string address) =>
        Assert.Throws<InvalidDataException>(() =>
            OfficialGameInstallService.ValidateOfficialUri(new Uri(address)));

    [Fact]
    public void ResourceDownloads_AcceptOnlySelectedOfficialPlatformCdn()
    {
        Assert.True(ExternalResourceInstallService.IsAllowedProviderUri(
            "Modrinth", new Uri("https://cdn.modrinth.com/data/a/file.jar")));
        Assert.True(ExternalResourceInstallService.IsAllowedProviderUri(
            "CurseForge", new Uri("https://mediafilez.forgecdn.net/files/1/2/file.jar")));
        Assert.False(ExternalResourceInstallService.IsAllowedProviderUri(
            "Modrinth", new Uri("https://example.com/file.jar")));
    }

    [Fact]
    public void ForgeBranding_ConstructsOfficialUniversalMavenUrl()
    {
        var minecraftRoot = Path.Combine("C:\\Games", ".minecraft");
        var jar = Path.Combine(
            minecraftRoot,
            "libraries",
            "net",
            "minecraftforge",
            "forge",
            "1.20.1-47.4.20",
            "forge-1.20.1-47.4.20-universal.jar");

        var uri = OfficialGameInstallService.CreateForgeUniversalUri(
            minecraftRoot,
            jar);

        Assert.Equal(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.20/forge-1.20.1-47.4.20-universal.jar",
            uri.AbsoluteUri);
    }

    [Fact]
    public void ForgeBranding_RejectsUniversalJarOutsideOfficialMavenLayout()
    {
        var minecraftRoot = Path.Combine("C:\\Games", ".minecraft");
        var jar = Path.Combine(minecraftRoot, "mods", "forge-universal.jar");

        Assert.Throws<InvalidDataException>(() =>
            OfficialGameInstallService.CreateForgeUniversalUri(
                minecraftRoot,
                jar));
    }

    [Fact]
    public void ForgeInstallerPlan_RecognizesRuntimeLibrariesAndOfficialInstaller()
    {
        using var manifest = JsonDocument.Parse("""
        {
          "arguments": {
            "game": [
              "--launchTarget", "forgeclient",
              "--fml.forgeVersion", "47.4.20",
              "--fml.mcVersion", "1.20.1",
              "--fml.mcpVersion", "20230612.114412"
            ]
          }
        }
        """);

        var plan = OfficialGameInstallService.CreateForgeInstallerPlan(
            manifest.RootElement,
            Path.Combine("C:\\Games", ".minecraft"));

        Assert.NotNull(plan);
        Assert.Equal(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.20/forge-1.20.1-47.4.20-installer.jar",
            plan.InstallerUri.AbsoluteUri);
        Assert.Equal(4, plan.RequiredFiles.Count);
        Assert.Contains(plan.RequiredFiles, item => item.RelativePath.EndsWith(
            "client-1.20.1-20230612.114412-slim.jar",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.RequiredFiles, item => item.RelativePath.EndsWith(
            "client-1.20.1-20230612.114412-extra.jar",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.RequiredFiles, item => item.RelativePath.EndsWith(
            "client-1.20.1-20230612.114412-srg.jar",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.RequiredFiles, item => item.RelativePath.EndsWith(
            "forge-1.20.1-47.4.20-client.jar",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForgeInstallerPlan_RejectsUnsafeVersionIdentifier()
    {
        using var manifest = JsonDocument.Parse("""
        {
          "arguments": {
            "game": [
              "--launchTarget", "forgeclient",
              "--fml.forgeVersion", "../47.4.20",
              "--fml.mcVersion", "1.20.1",
              "--fml.mcpVersion", "20230612.114412"
            ]
          }
        }
        """);

        Assert.Throws<InvalidDataException>(() =>
            OfficialGameInstallService.CreateForgeInstallerPlan(
                manifest.RootElement,
                Path.Combine("C:\\Games", ".minecraft")));
    }
}
