namespace MCCPBuilder.Core;

public static class OutputPathResolver
{
    public static string Resolve(
        string configuredOutputDirectory,
        string executableDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredOutputDirectory))
        {
            throw new InvalidDataException("输出目录不能为空。");
        }

        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            throw new InvalidDataException(
                "无法确定打包器 EXE 所在目录。");
        }

        var expanded = Environment.ExpandEnvironmentVariables(
            configuredOutputDirectory.Trim());
        var executableRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(executableDirectory));
        return Path.IsPathFullyQualified(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(
                executableRoot,
                expanded));
    }
}
