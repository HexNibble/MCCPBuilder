namespace MCCPBuilder.Core;

public sealed record LauncherChannelContext(
    string InstallationRoot,
    string RuntimeRoot,
    bool TestChannelAvailable,
    bool IsTestChannel,
    UpdateBootstrapConfig Bootstrap);

public static class LauncherChannelService
{
    public const string TestMarkerRelativePath =
        @"LauncherConfig\enable-test-channel.mccptest";
    public const string TestSelectionRelativePath =
        @"LauncherConfig\selected-test-channel.mccpstate";
    public const string TestDirectoryName = "TestChannel";
    public const string TestProductSuffix = "-test";

    public static LauncherChannelContext Prepare(
        string installationDirectory,
        UpdateBootstrapConfig productionBootstrap)
    {
        ArgumentNullException.ThrowIfNull(productionBootstrap);
        var installationRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(installationDirectory));
        var markerPath = ResolveInside(
            installationRoot,
            TestMarkerRelativePath);
        var selectionPath = ResolveInside(
            installationRoot,
            TestSelectionRelativePath);
        var testRoot = ResolveInside(
            installationRoot,
            TestDirectoryName);
        var available = File.Exists(markerPath);
        if (!available)
        {
            DeleteTestDirectory(testRoot);
            DeleteFileIfPresent(selectionPath);
            return new(
                installationRoot,
                installationRoot,
                false,
                false,
                productionBootstrap);
        }

        var selected = File.Exists(selectionPath);
        if (!selected)
        {
            return new(
                installationRoot,
                installationRoot,
                true,
                false,
                productionBootstrap);
        }

        Directory.CreateDirectory(testRoot);
        return new(
            installationRoot,
            testRoot,
            true,
            true,
            CreateTestBootstrap(productionBootstrap));
    }

    public static void SelectTestChannel(
        string installationDirectory,
        bool useTestChannel)
    {
        var installationRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(installationDirectory));
        var markerPath = ResolveInside(
            installationRoot,
            TestMarkerRelativePath);
        var selectionPath = ResolveInside(
            installationRoot,
            TestSelectionRelativePath);
        if (!useTestChannel)
        {
            DeleteFileIfPresent(selectionPath);
            return;
        }

        if (!File.Exists(markerPath))
        {
            throw new InvalidOperationException(
                $"缺少测试资格文件：{TestMarkerRelativePath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(selectionPath)!);
        File.WriteAllText(selectionPath, "");
        try
        {
            File.SetAttributes(
                selectionPath,
                File.GetAttributes(selectionPath) | FileAttributes.Hidden);
        }
        catch (UnauthorizedAccessException)
        {
            // 状态文件可用即可，无法设置隐藏属性不影响渠道隔离。
        }
    }

    public static string CreateTestProductId(string productionProductId)
    {
        var normalized =
            ReleaseBundleService.NormalizeProductId(productionProductId);
        return ReleaseBundleService.NormalizeProductId(
            normalized + TestProductSuffix);
    }

    private static UpdateBootstrapConfig CreateTestBootstrap(
        UpdateBootstrapConfig production) =>
        new()
        {
            SchemaVersion = production.SchemaVersion,
            ServerBaseUrl = production.ServerBaseUrl,
            ProductId = CreateTestProductId(production.ProductId),
            LauncherVersion = production.LauncherVersion,
            DownloadConcurrency = production.DownloadConcurrency,
            RequireSuccessfulCheck = production.RequireSuccessfulCheck,
            RequireLauncherUpdateCheck = false,
            RequireAdministrator = production.RequireAdministrator
        };

    private static void DeleteTestDirectory(string testRoot)
    {
        if (!Directory.Exists(testRoot))
        {
            return;
        }

        var info = new DirectoryInfo(testRoot);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            info.Attributes = FileAttributes.Normal;
            Directory.Delete(testRoot, false);
            return;
        }

        DeleteDirectoryTree(info);
    }

    private static void DeleteDirectoryTree(DirectoryInfo directory)
    {
        foreach (var file in directory.EnumerateFiles())
        {
            file.Attributes = FileAttributes.Normal;
            file.Delete();
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                child.Attributes = FileAttributes.Normal;
                child.Delete(false);
                continue;
            }

            DeleteDirectoryTree(child);
        }

        directory.Attributes = FileAttributes.Normal;
        directory.Delete(false);
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private static string ResolveInside(string root, string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("测试渠道路径超出安装目录。");
        }

        return candidate;
    }
}
