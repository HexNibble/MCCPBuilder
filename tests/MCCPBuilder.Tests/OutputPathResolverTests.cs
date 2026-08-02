using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class OutputPathResolverTests
{
    [Fact]
    public void RelativeOutputUsesExecutableDirectory()
    {
        var executableDirectory =
            Path.Combine("D:\\", "缓存", "打包器");

        var result = OutputPathResolver.Resolve(
            Path.Combine("输出 文件", "release"),
            executableDirectory);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                executableDirectory,
                "输出 文件",
                "release")),
            result);
    }

    [Fact]
    public void AbsoluteOutputDoesNotUseExecutableDirectory()
    {
        var absoluteOutput = Path.Combine(
            "E:\\",
            "自定义 输出",
            "MCCP");

        var result = OutputPathResolver.Resolve(
            absoluteOutput,
            Path.Combine("D:\\", "缓存"));

        Assert.Equal(
            Path.GetFullPath(absoluteOutput),
            result);
    }

    [Fact]
    public void EmptyOutputIsRejected()
    {
        Assert.Throws<InvalidDataException>(
            () => OutputPathResolver.Resolve(
                " ",
                Path.GetTempPath()));
    }
}
