namespace MCCPBuilder.Core;

public static class ProjectRootLocator
{
    public const string ProjectRootEnvironmentVariable =
        "MCCPBUILDER_PROJECT_ROOT";

    public static string FindCurrent()
    {
        var candidates = new List<string?>
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Environment.GetEnvironmentVariable(
                ProjectRootEnvironmentVariable)
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady ||
                    drive.DriveType is not (
                        DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                candidates.Add(Path.Combine(
                    drive.RootDirectory.FullName,
                    "MCCP",
                    "MCCPBuilder"));
                candidates.Add(Path.Combine(
                    drive.RootDirectory.FullName,
                    "MCCPBuilder"));
            }
            catch (IOException)
            {
                // 无法读取的磁盘不影响继续检查其他候选目录。
            }
            catch (UnauthorizedAccessException)
            {
                // 无权限磁盘不影响继续检查其他候选目录。
            }
        }

        return Locate(candidates);
    }

    public static string Locate(IEnumerable<string?> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var checkedPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            DirectoryInfo? directory;
            try
            {
                directory = new DirectoryInfo(
                    Path.GetFullPath(candidate.Trim()));
            }
            catch (Exception exception)
                when (exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {
                continue;
            }

            while (directory is not null)
            {
                if (checkedPaths.Add(directory.FullName) &&
                    IsProjectRoot(directory.FullName))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        var checkedSummary = checkedPaths.Count == 0
            ? "没有有效候选目录"
            : string.Join(
                "；",
                checkedPaths.Take(12));
        throw new DirectoryNotFoundException(
            "找不到 MCCPBuilder 开发项目，无法发布 Launcher。" +
            $"请保留完整项目，或将环境变量 " +
            $"{ProjectRootEnvironmentVariable} 设置为项目根目录。" +
            $"已检查：{checkedSummary}");
    }

    public static bool IsProjectRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string root;
        try
        {
            root = Path.GetFullPath(directory);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }

        return File.Exists(Path.Combine(root, "MCCPBuilder.sln")) &&
               File.Exists(Path.Combine(
                   root,
                   "src",
                   "MCCPBuilder.Launcher",
                   "MCCPBuilder.Launcher.csproj"));
    }
}
