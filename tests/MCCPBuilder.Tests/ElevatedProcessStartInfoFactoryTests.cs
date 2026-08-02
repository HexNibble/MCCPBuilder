using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class ElevatedProcessStartInfoFactoryTests
{
    [Fact]
    public void Create_UsesWindowsRunAsAndPreservesArguments()
    {
        var result = ElevatedProcessStartInfoFactory.Create(
            @"C:\Program Files\客户端\Launcher.exe",
            @"C:\Program Files\客户端",
            ["--post-update", "包含 空格"]);

        Assert.True(result.UseShellExecute);
        Assert.Equal("runas", result.Verb);
        Assert.Equal(
            @"C:\Program Files\客户端\Launcher.exe",
            result.FileName);
        Assert.Equal(
            @"C:\Program Files\客户端",
            result.WorkingDirectory);
        Assert.Equal(
            ["--post-update", "包含 空格"],
            result.ArgumentList);
    }
}
