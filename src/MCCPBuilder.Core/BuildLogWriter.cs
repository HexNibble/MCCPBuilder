using System.Text;
using System.Text.RegularExpressions;
using MCCPBuilder.Models;

namespace MCCPBuilder.Core;

public sealed partial class BuildLogWriter
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private bool _completed;

    public BuildLogWriter(string outputDirectory, ProjectConfig project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var logDirectory = Path.Combine(
            Path.GetFullPath(outputDirectory),
            "BuildLogs");
        Directory.CreateDirectory(logDirectory);
        FilePath = Path.Combine(
            logDirectory,
            $"build-{_startedAt:yyyyMMdd-HHmmss-fff}.log");
        Write("INFO", $"开始时间：{_startedAt:O}");
        Write("INFO", $"项目名称：{project.Basic.ClientName}");
        Write("INFO", $"项目版本：{project.Basic.ClientVersion}");
        Write("INFO", $"应用版本：{project.ApplicationVersion}");
    }

    public string FilePath { get; }

    public void Info(string message) => Write("INFO", message);

    public void Warning(string message) => Write("WARN", message);

    public void Error(Exception exception) =>
        Write(
            "ERROR",
            $"{exception.GetType().Name}: {exception.Message}");

    public void Complete(bool success)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        var finishedAt = DateTimeOffset.Now;
        Write("INFO", $"构建结果：{(success ? "成功" : "失败")}");
        Write("INFO", $"结束时间：{finishedAt:O}");
        Write(
            "INFO",
            $"总耗时：{(finishedAt - _startedAt).TotalSeconds:F1} 秒");
    }

    private void Write(string level, string message)
    {
        var safeMessage = SensitiveValuePattern().Replace(
            message ?? "",
            "$1=<redacted>");
        File.AppendAllText(
            FilePath,
            $"[{DateTimeOffset.Now:O}] [{level}] {safeMessage}{Environment.NewLine}",
            new UTF8Encoding(false));
    }

    [GeneratedRegex(
        @"(?i)\b(access[_ -]?token|password|cookie)\b\s*[:=]\s*[^\s,;]+")]
    private static partial Regex SensitiveValuePattern();
}
