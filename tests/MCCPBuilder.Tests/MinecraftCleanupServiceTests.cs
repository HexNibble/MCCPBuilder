using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class MinecraftCleanupServiceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "MCCPBuilderCleanupTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Clean_SelectedCacheAndLogs_LeavesGameAndUserDataUntouched()
    {
        var minecraft = Path.Combine(_temporaryDirectory, ".minecraft");
        CreateFile(minecraft, @"cache\index.bin");
        CreateFile(minecraft, @"mods\example\mod_cache\entry.bin");
        CreateFile(minecraft, @"logs\latest.log");
        CreateFile(minecraft, @"crash-reports\crash.txt");
        CreateFile(minecraft, "debug.log");
        CreateFile(minecraft, @"saves\世界一\level.dat");
        CreateFile(minecraft, @"saves\世界一\cache\region-index.bin");
        CreateFile(minecraft, @"resourcepacks\测试资源包\cache\texture.bin");
        CreateFile(minecraft, @"mods\example.jar");
        CreateFile(minecraft, @"config\example.toml");

        var result = new MinecraftCleanupService().Clean(minecraft, true, true);

        Assert.False(Directory.Exists(Path.Combine(minecraft, "cache")));
        Assert.False(Directory.Exists(Path.Combine(minecraft, @"mods\example\mod_cache")));
        Assert.False(Directory.Exists(Path.Combine(minecraft, "logs")));
        Assert.False(Directory.Exists(Path.Combine(minecraft, "crash-reports")));
        Assert.False(File.Exists(Path.Combine(minecraft, "debug.log")));
        Assert.True(File.Exists(Path.Combine(minecraft, @"saves\世界一\level.dat")));
        Assert.True(File.Exists(Path.Combine(minecraft, @"saves\世界一\cache\region-index.bin")));
        Assert.True(File.Exists(Path.Combine(minecraft, @"resourcepacks\测试资源包\cache\texture.bin")));
        Assert.True(File.Exists(Path.Combine(minecraft, @"mods\example.jar")));
        Assert.True(File.Exists(Path.Combine(minecraft, @"config\example.toml")));
        Assert.Equal(4, result.DeletedDirectoryCount);
        Assert.Equal(1, result.DeletedFileCount);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Clean_CacheOnly_DoesNotDeleteLogs()
    {
        var minecraft = Path.Combine(_temporaryDirectory, ".minecraft");
        CreateFile(minecraft, @"caches\entry.bin");
        CreateFile(minecraft, @"logs\latest.log");

        _ = new MinecraftCleanupService().Clean(minecraft, true, false);

        Assert.False(Directory.Exists(Path.Combine(minecraft, "caches")));
        Assert.True(File.Exists(Path.Combine(minecraft, @"logs\latest.log")));
    }

    [Fact]
    public void Clean_LogsOnly_DoesNotDeleteCaches()
    {
        var minecraft = Path.Combine(_temporaryDirectory, ".minecraft");
        CreateFile(minecraft, @".cache\entry.bin");
        CreateFile(minecraft, @"logs\latest.log");

        _ = new MinecraftCleanupService().Clean(minecraft, false, true);

        Assert.True(File.Exists(Path.Combine(minecraft, @".cache\entry.bin")));
        Assert.False(Directory.Exists(Path.Combine(minecraft, "logs")));
    }

    private static void CreateFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, true);
        }
    }
}
