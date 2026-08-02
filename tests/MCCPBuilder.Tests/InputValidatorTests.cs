using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class InputValidatorTests
{
    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.20.1-forge")]
    [InlineData("2.0.0+build.8")]
    public void Version_AcceptsSupportedFormats(string version) =>
        Assert.True(InputValidator.IsValidVersion(version));

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("v1.2.3")]
    public void Version_RejectsUnsupportedFormats(string version) =>
        Assert.False(InputValidator.IsValidVersion(version));

    [Theory]
    [InlineData("ClientSetup")]
    [InlineData("中文 客户端")]
    [InlineData("ClientSetup.exe")]
    public void FileName_AcceptsValidNames(string fileName) =>
        Assert.True(InputValidator.IsValidFileName(fileName));

    [Theory]
    [InlineData("")]
    [InlineData("bad/name")]
    [InlineData("bad:name")]
    [InlineData("trailing.")]
    public void FileName_RejectsInvalidNames(string fileName) =>
        Assert.False(InputValidator.IsValidFileName(fileName));

    [Fact]
    public void PathInside_RejectsSiblingWithSamePrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "client");
        var sibling = Path.Combine(Path.GetTempPath(), "client-other", "file.txt");
        Assert.False(InputValidator.IsPathInside(root, sibling));
    }

    [Theory]
    [InlineData("mc.example.com")]
    [InlineData("mc.example.com:25565")]
    [InlineData("127.0.0.1:25565")]
    [InlineData("[2001:db8::1]:25565")]
    [InlineData("中文服务器.example:25565")]
    public void MinecraftServerAddress_AcceptsHostAndOptionalPort(string address) =>
        Assert.True(InputValidator.IsValidMinecraftServerAddress(address));

    [Theory]
    [InlineData("")]
    [InlineData("https://mc.example.com")]
    [InlineData("mc.example.com/server")]
    [InlineData("mc example.com")]
    [InlineData("mc.example.com:0")]
    [InlineData("mc.example.com:65536")]
    [InlineData("2001:db8::1")]
    public void MinecraftServerAddress_RejectsUrlWhitespaceAndInvalidPort(string address) =>
        Assert.False(InputValidator.IsValidMinecraftServerAddress(address));

    [Theory]
    [InlineData("")]
    [InlineData("最后防线 2.2")]
    [InlineData("POTATO LIGHT STUDIO")]
    public void GameWindowTitle_AcceptsEmptyOrNormalText(string title) =>
        Assert.True(InputValidator.IsValidOptionalGameWindowTitle(title));

    [Fact]
    public void GameWindowTitle_RejectsControlCharactersAndOverlongText()
    {
        Assert.False(InputValidator.IsValidOptionalGameWindowTitle("标题\n第二行"));
        Assert.False(InputValidator.IsValidOptionalGameWindowTitle(new string('A', 129)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("最后防线启动器")]
    [InlineData("POTATO LIGHT STUDIO")]
    public void LauncherTitle_AcceptsEmptyOrNormalText(string title) =>
        Assert.True(InputValidator.IsValidOptionalLauncherTitle(title));

    [Fact]
    public void LauncherTitle_RejectsControlCharactersAndOverlongText()
    {
        Assert.False(InputValidator.IsValidOptionalLauncherTitle("标题\r第二行"));
        Assert.False(InputValidator.IsValidOptionalLauncherTitle(new string(
            'A',
            InputValidator.MaximumLauncherTitleLength + 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"E:\素材 文件\背景.PNG")]
    [InlineData(@"C:\images\background.jpg")]
    [InlineData(@"C:\images\background.JPEG")]
    [InlineData(@"C:\images\background.bmp")]
    public void LauncherBackground_AcceptsSupportedImageExtensions(string path) =>
        Assert.True(InputValidator.IsSupportedLauncherBackgroundImagePath(path));

    [Theory]
    [InlineData(@"E:\素材\background.gif")]
    [InlineData(@"E:\素材\background.webp")]
    [InlineData(@"E:\素材\background.exe")]
    public void LauncherBackground_RejectsUnsupportedImageExtensions(string path) =>
        Assert.False(InputValidator.IsSupportedLauncherBackgroundImagePath(path));
}
