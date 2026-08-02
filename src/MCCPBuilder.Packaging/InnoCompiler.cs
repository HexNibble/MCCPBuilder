using System.Diagnostics;

namespace MCCPBuilder.Packaging;

public sealed record CompilerResult(bool Success, int ExitCode, string Output);

public sealed class InnoCompiler
{
    public async Task<CompilerResult> CompileAsync(
        string compilerPath,
        string scriptPath,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(compilerPath))
            return new(false, -1, $"未找到 Inno Setup 编译器：{compilerPath}");
        if (!File.Exists(scriptPath))
            return new(false, -1, $"未找到安装脚本：{scriptPath}");
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(Path.GetFullPath(outputDirectory));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = compilerPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            process.StartInfo.ArgumentList.Add(
                "/O" + Path.GetFullPath(outputDirectory));
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }

        var output = (await outputTask) + Environment.NewLine + (await errorTask);
        return new(process.ExitCode == 0, process.ExitCode, output.Trim());
    }
}
