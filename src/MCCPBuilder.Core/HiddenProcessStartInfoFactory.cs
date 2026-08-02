using System.Diagnostics;

namespace MCCPBuilder.Core;

public static class HiddenProcessStartInfoFactory
{
    public static ProcessStartInfo Create(
        string executablePath,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        return new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }
}
