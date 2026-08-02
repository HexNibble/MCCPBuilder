using System.Diagnostics;

namespace MCCPBuilder.Core;

public sealed record LauncherPublishResult(string ExecutablePath, string CompilerOutput);

public sealed class LauncherPublisherService
{
    public async Task<LauncherPublishResult> PublishAsync(
        string destinationExecutable,
        string launcherVersion,
        string applicationIconPath,
        string configuration = "Release",
        CancellationToken cancellationToken = default)
    {
        var iconError = ExecutableIconService.ValidateIcon(applicationIconPath);
        if (iconError is not null)
        {
            throw new InvalidDataException($"主程序图标无效：{iconError}");
        }

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "MCCPBuilder",
            "LauncherPublish",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var templateRoot = Path.Combine(
                temporaryRoot,
                "BuildTemplate");
            var publishRoot = Path.Combine(
                temporaryRoot,
                "Published");
            await LauncherBuildTemplateService.ExtractAsync(
                templateRoot,
                cancellationToken);
            var launcherProject = Path.Combine(
                templateRoot,
                "MCCPBuilder.Launcher",
                "MCCPBuilder.Launcher.csproj");
            var startInfo = new ProcessStartInfo
            {
                FileName = FindDotNet(),
                WorkingDirectory = templateRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("publish");
            startInfo.ArgumentList.Add(launcherProject);
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(configuration);
            startInfo.ArgumentList.Add("--nologo");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(publishRoot);
            startInfo.ArgumentList.Add(
                "/p:EmbedLauncherBuildTemplate=false");
            startInfo.ArgumentList.Add(
                "/p:RestoreIgnoreFailedSources=true");
            startInfo.ArgumentList.Add(
                $"/p:Version={launcherVersion}");
            startInfo.ArgumentList.Add(
                $"/p:AssemblyVersion={launcherVersion}.0");
            startInfo.ArgumentList.Add(
                $"/p:FileVersion={launcherVersion}.0");
            startInfo.ArgumentList.Add(
                $"/p:InformationalVersion={launcherVersion}");
            if (!string.IsNullOrWhiteSpace(applicationIconPath))
            {
                startInfo.ArgumentList.Add($"/p:ApplicationIcon={Path.GetFullPath(applicationIconPath)}");
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 dotnet publish。");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                throw;
            }

            var output = (await standardOutput) + (await standardError);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"嵌入式 Launcher 模板发布失败，退出代码 " +
                    $"{process.ExitCode}：{Abbreviate(output)}");
            }

            var publishedExecutable = Path.Combine(
                publishRoot,
                "Launcher.exe");
            if (!File.Exists(publishedExecutable))
            {
                throw new FileNotFoundException("dotnet publish 未生成 Launcher.exe。", publishedExecutable);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationExecutable)!);
            File.Copy(publishedExecutable, destinationExecutable, true);
            return new(destinationExecutable, output.Trim());
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, true);
            }
        }
    }

    private static string FindDotNet()
    {
        var installedDotNet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "dotnet.exe");
        return File.Exists(installedDotNet) ? installedDotNet : "dotnet";
    }

    private static string Abbreviate(string value) =>
        value.Length <= 2000 ? value : value[^2000..];
}
