using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class LauncherChannelServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MCCPBuilderChannelTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void MarkerEnablesIndependentTestRootAndProduct()
    {
        Directory.CreateDirectory(Path.Combine(_root, "LauncherConfig"));
        File.WriteAllText(
            Path.Combine(
                _root,
                LauncherChannelService.TestMarkerRelativePath),
            "");
        var production = CreateProductionBootstrap();

        var formal = LauncherChannelService.Prepare(_root, production);
        LauncherChannelService.SelectTestChannel(_root, true);
        var test = LauncherChannelService.Prepare(_root, production);

        Assert.True(formal.TestChannelAvailable);
        Assert.False(formal.IsTestChannel);
        Assert.Equal(Path.GetFullPath(_root), formal.RuntimeRoot);
        Assert.True(test.IsTestChannel);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(_root), "TestChannel"),
            test.RuntimeRoot);
        Assert.Equal("test-client-test", test.Bootstrap.ProductId);
        Assert.False(test.Bootstrap.RequireLauncherUpdateCheck);
        Assert.Equal(
            production.DownloadConcurrency,
            test.Bootstrap.DownloadConcurrency);
    }

    [Fact]
    public void RemovingMarkerDeletesAllTestFilesAndRestoresFormalChannel()
    {
        Directory.CreateDirectory(Path.Combine(_root, "LauncherConfig"));
        var marker = Path.Combine(
            _root,
            LauncherChannelService.TestMarkerRelativePath);
        File.WriteAllText(marker, "");
        LauncherChannelService.SelectTestChannel(_root, true);
        var testRoot = Path.Combine(
            _root,
            LauncherChannelService.TestDirectoryName);
        Directory.CreateDirectory(
            Path.Combine(testRoot, ".minecraft", "saves"));
        File.WriteAllText(
            Path.Combine(
                testRoot,
                ".minecraft",
                "saves",
                "test-world.dat"),
            "test");
        var readOnlyFile = Path.Combine(testRoot, "read-only.dat");
        File.WriteAllText(readOnlyFile, "test");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
        File.Delete(marker);

        var channel = LauncherChannelService.Prepare(
            _root,
            CreateProductionBootstrap());

        Assert.False(channel.TestChannelAvailable);
        Assert.False(channel.IsTestChannel);
        Assert.Equal(Path.GetFullPath(_root), channel.RuntimeRoot);
        Assert.False(Directory.Exists(testRoot));
        Assert.False(File.Exists(Path.Combine(
            _root,
            LauncherChannelService.TestSelectionRelativePath)));
    }

    [Fact]
    public void TestProductIdIsNormalizedAndIndependent()
    {
        Assert.Equal(
            "my-client-test",
            LauncherChannelService.CreateTestProductId(" My Client "));
        Assert.Equal(
            "my-client-test-test",
            LauncherChannelService.CreateTestProductId("my-client-test"));
    }

    [Fact]
    public void FormalAndTestSelectionDoNotModifyEachOthersFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "LauncherConfig"));
        File.WriteAllText(
            Path.Combine(
                _root,
                LauncherChannelService.TestMarkerRelativePath),
            "");
        var formalFile = Path.Combine(_root, "formal.txt");
        File.WriteAllText(formalFile, "formal");
        LauncherChannelService.SelectTestChannel(_root, true);
        var test = LauncherChannelService.Prepare(
            _root,
            CreateProductionBootstrap());
        File.WriteAllText(
            Path.Combine(test.RuntimeRoot, "test.txt"),
            "test");

        LauncherChannelService.SelectTestChannel(_root, false);
        var formal = LauncherChannelService.Prepare(
            _root,
            CreateProductionBootstrap());

        Assert.False(formal.IsTestChannel);
        Assert.Equal("formal", File.ReadAllText(formalFile));
        Assert.True(File.Exists(Path.Combine(
            test.RuntimeRoot,
            "test.txt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static UpdateBootstrapConfig CreateProductionBootstrap() =>
        new()
        {
            ServerBaseUrl = "https://updates.example/",
            ProductId = "test-client",
            LauncherVersion = "1.2.3",
            DownloadConcurrency = 200,
            RequireSuccessfulCheck = true,
            RequireLauncherUpdateCheck = true,
            RequireAdministrator = true
        };
}
