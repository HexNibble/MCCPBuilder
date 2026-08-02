using System.Diagnostics;

namespace MCCPBuilder.Core;

public static class ElevatedProcessStartInfoFactory
{
    public static ProcessStartInfo Create(
        string executablePath,
        string workingDirectory,
        IEnumerable<string>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var result = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            Verb = "runas"
        };

        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                result.ArgumentList.Add(argument);
            }
        }

        return result;
    }
}
