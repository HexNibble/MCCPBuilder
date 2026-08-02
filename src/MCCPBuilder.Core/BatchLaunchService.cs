using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MCCPBuilder.Models;

namespace MCCPBuilder.Core;

public sealed partial class BatchLaunchService
{
    private const long MaximumBatchSize = 2 * 1024 * 1024;

    public IReadOnlyList<string> Validate(ProjectConfig project)
    {
        var errors = new List<string>();
        var batchPath = project.Launch.BatchFilePath;
        if (string.IsNullOrWhiteSpace(batchPath) || !File.Exists(batchPath))
        {
            errors.Add("BAT 启动文件不存在。");
            return errors;
        }

        if (!string.Equals(Path.GetExtension(batchPath), ".bat", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("启动脚本必须是 .bat 文件。");
        }

        if (new FileInfo(batchPath).Length > MaximumBatchSize)
        {
            errors.Add("BAT 文件超过 2 MB 安全上限。");
        }

        var content = File.ReadAllText(batchPath);
        if (!JavaCommandRegex().IsMatch(content))
        {
            errors.Add("BAT 中未找到 java.exe 或 javaw.exe 启动命令。");
        }

        foreach (var line in content.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) ||
                trimmed.StartsWith("::", StringComparison.Ordinal) ||
                trimmed.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) ||
                IsAllowedCommand(trimmed))
            {
                continue;
            }

            errors.Add($"BAT 包含不允许自动打包的命令：{Abbreviate(trimmed)}");
        }

        return errors;
    }

    public async Task PrepareAsync(
        ProjectConfig project,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var errors = Validate(project);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        var content = await File.ReadAllTextAsync(project.Launch.BatchFilePath, cancellationToken);
        var minecraftRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(project.Client.MinecraftRootDirectory));
        var versionDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(project.Client.VersionDirectory));

        content = Nide8AgentRegex().Replace(content, "");
        content = AuthlibAgentRegex().Replace(content, "");
        content = JavaExecutableRegex().Replace(
            content,
            "\"%~dp0..\\JAVA\\bin\\java.exe\"",
            1);
        content = content.Replace(minecraftRoot, "%MCCP_GAME_ROOT%", StringComparison.OrdinalIgnoreCase);
        content = SensitiveArgumentRegex().Replace(
            content,
            match => $"{match.Groups["name"].Value} ${{{EnvironmentVariableFor(match.Groups["name"].Value)}}}");
        content = QuotePathOptionRegex().Replace(
            content,
            match => $"{match.Groups["option"].Value} \"{match.Groups["value"].Value}\"");
        content = QuoteSystemPropertyPathRegex().Replace(
            content,
            match => $"\"{match.Value}\"");
        var generated = GeneratePortableLaunchFiles(content, minecraftRoot, versionDirectory);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllTextAsync(
            destinationPath,
            generated.BatchContent,
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(destinationPath)!, "launch.arguments.json"),
            generated.ArgumentJson,
            new UTF8Encoding(false),
            cancellationToken);
    }

    private static bool IsAllowedCommand(string line) =>
        line.StartsWith("@echo ", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("echo ", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("chcp ", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("title ", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("pause", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("set ", StringComparison.OrdinalIgnoreCase) ||
        JavaCommandRegex().IsMatch(line);

    private static GeneratedLaunchFiles GeneratePortableLaunchFiles(
        string content,
        string minecraftRoot,
        string versionDirectory)
    {
        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        var javaLineIndex = Array.FindIndex(lines, line => JavaExecutableRegex().IsMatch(line));
        if (javaLineIndex < 0)
        {
            throw new InvalidDataException("BAT 中未找到可转换的 Java 启动行。");
        }

        var executableMatch = JavaExecutableRegex().Match(lines[javaLineIndex]);
        var argumentText = lines[javaLineIndex][(executableMatch.Index + executableMatch.Length)..].Trim();
        var arguments = TokenizeCommandLine(argumentText)
            .Select(argument => argument.Replace(
                "%MCCP_GAME_ROOT%",
                "${MCCP_GAME_ROOT}",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (arguments.Length == 0)
        {
            throw new InvalidDataException("BAT Java 启动行没有可用参数。");
        }

        var versionRelativePath = Path.GetRelativePath(minecraftRoot, versionDirectory);
        var generatedArguments = new GeneratedJavaArguments(
            Path.Combine(".minecraft", versionRelativePath),
            arguments);
        var argumentJson = JsonSerializer.Serialize(
            generatedArguments,
            new JsonSerializerOptions { WriteIndented = true });
        var batch = """
            @echo off
            setlocal
            set "MCCP_APP_ROOT=%~dp0.."
            if not defined MCCP_USERNAME set "MCCP_USERNAME=Player"
            if not defined MCCP_UUID set "MCCP_UUID=00000000000000000000000000000000"
            if not defined MCCP_ACCESS_TOKEN set "MCCP_ACCESS_TOKEN=0"
            if not defined MCCP_CLIENT_ID set "MCCP_CLIENT_ID=0"
            if not defined MCCP_XUID set "MCCP_XUID=0"
            if not defined MCCP_USER_TYPE set "MCCP_USER_TYPE=legacy"
            "%MCCP_APP_ROOT%\Launcher.exe" --run-generated
            set "MCCP_EXIT_CODE=%ERRORLEVEL%"
            if not "%MCCP_EXIT_CODE%"=="0" pause
            exit /b %MCCP_EXIT_CODE%
            """;
        return new(batch + Environment.NewLine, argumentJson);
    }

    private static IReadOnlyList<string> TokenizeCommandLine(string commandLine)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (quoted)
        {
            throw new InvalidDataException("BAT Java 启动行包含未闭合的双引号。");
        }

        if (current.Length > 0)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    private static string EnvironmentVariableFor(string argumentName) =>
        argumentName.ToLowerInvariant() switch
        {
            "--username" => "MCCP_USERNAME",
            "--uuid" => "MCCP_UUID",
            "--accesstoken" => "MCCP_ACCESS_TOKEN",
            "--clientid" => "MCCP_CLIENT_ID",
            "--xuid" => "MCCP_XUID",
            "--usertype" => "MCCP_USER_TYPE",
            _ => throw new InvalidOperationException($"未知敏感参数：{argumentName}")
        };

    private static string Abbreviate(string value) =>
        value.Length <= 100 ? value : value[..100] + "...";

    [GeneratedRegex(@"(?im)^\s*(?:""[^""]*\\bin\\javaw?\.exe""|[^\s]*\\bin\\javaw?\.exe)\s+")]
    private static partial Regex JavaCommandRegex();

    [GeneratedRegex(@"(?im)^\s*(?:""[^""]*\\bin\\javaw?\.exe""|[^\s]*\\bin\\javaw?\.exe)")]
    private static partial Regex JavaExecutableRegex();

    [GeneratedRegex(@"(?i)\s*(?:""-javaagent:[^""]*nide8auth\.jar=[^""]*""|-javaagent:(?:""[^""]*nide8auth\.jar""|[^\s]*nide8auth\.jar)=(?:""?[A-Za-z0-9_-]{8,128}""?))")]
    private static partial Regex Nide8AgentRegex();

    [GeneratedRegex(@"(?i)\s*(?:""-javaagent:[^""]*authlib-injector[^""]*""|-javaagent:(?:""[^""]*authlib-injector[^""]*""|[^\s]*authlib-injector[^\s]*))")]
    private static partial Regex AuthlibAgentRegex();

    [GeneratedRegex(@"(?i)(?<name>--username|--uuid|--accessToken|--clientId|--xuid|--userType)\s+(?:""[^""]*""|\S+)")]
    private static partial Regex SensitiveArgumentRegex();

    [GeneratedRegex(@"(?i)(?<option>-cp|-classpath|-p|--module-path|--gameDir|--assetsDir)\s+(?<value>%MCCP_GAME_ROOT%\\?\S+)")]
    private static partial Regex QuotePathOptionRegex();

    [GeneratedRegex(@"(?i)(?:-D[\w.]+)=%MCCP_GAME_ROOT%\\?\S+")]
    private static partial Regex QuoteSystemPropertyPathRegex();

    private sealed record GeneratedLaunchFiles(string BatchContent, string ArgumentJson);

    private sealed record GeneratedJavaArguments(
        string WorkingDirectory,
        IReadOnlyList<string> Arguments);
}
