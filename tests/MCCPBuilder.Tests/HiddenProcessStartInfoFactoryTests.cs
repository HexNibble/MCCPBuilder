using System.Diagnostics;
using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class HiddenProcessStartInfoFactoryTests
{
    [Fact]
    public void Create_ConfiguresProcessWithoutAConsoleWindow()
    {
        var result = HiddenProcessStartInfoFactory.Create(
            @"C:\Program Files\Java\bin\java.exe",
            @"C:\Games\示例 客户端");

        Assert.False(result.UseShellExecute);
        Assert.True(result.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, result.WindowStyle);
        Assert.Equal(@"C:\Program Files\Java\bin\java.exe", result.FileName);
        Assert.Equal(@"C:\Games\示例 客户端", result.WorkingDirectory);
    }
}
