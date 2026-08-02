using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class JavaDetectionServiceTests
{
    [Theory]
    [InlineData("java version \"1.8.0_401\"", 8)]
    [InlineData("openjdk version \"17.0.10\" 2024-01-16", 17)]
    [InlineData("openjdk version \"21\" 2023-09-19", 21)]
    public void ParseMajorVersion_HandlesCommonJavaFormats(string output, int expected) =>
        Assert.Equal(expected, JavaDetectionService.ParseMajorVersion(output));

    [Theory]
    [InlineData("OpenJDK 64-Bit Server VM", "x64")]
    [InlineData("os.arch = amd64", "x64")]
    [InlineData("os.arch = aarch64", "arm64")]
    public void ParseArchitecture_HandlesCommonArchitectures(string output, string expected) =>
        Assert.Equal(expected, JavaDetectionService.ParseArchitecture(output));
}
