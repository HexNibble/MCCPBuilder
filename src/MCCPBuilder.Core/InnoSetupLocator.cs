namespace MCCPBuilder.Core;

public sealed class InnoSetupLocator
{
    public string? FindCompiler(string? configuredPath = null)
    {
        foreach (var candidate in GetCandidates(configuredPath)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or
                PathTooLongException or UnauthorizedAccessException)
            {
                // 继续检查其他受信任的本机安装位置。
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidates(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return configuredPath;
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Inno Setup 6",
            "ISCC.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Inno Setup 6",
            "ISCC.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Inno Setup 6",
            "ISCC.exe");

        var pathEnvironment = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathEnvironment.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(directory.Trim('"'), "ISCC.exe");
        }
    }
}
