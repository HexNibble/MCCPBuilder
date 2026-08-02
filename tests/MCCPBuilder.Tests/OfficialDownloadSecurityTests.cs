using MCCPBuilder.Core;

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
}
