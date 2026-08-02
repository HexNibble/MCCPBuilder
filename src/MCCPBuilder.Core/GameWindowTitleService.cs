using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MCCPBuilder.Core;

public static class GameWindowTitleService
{
    public const int MaximumTitleLength = 128;

    public static void ApplyWhileRunning(
        Process process,
        string title,
        TimeSpan? refreshInterval = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        Validate(title);

        var interval = refreshInterval ?? TimeSpan.FromMilliseconds(750);
        var milliseconds = Math.Max(100, (int)Math.Min(interval.TotalMilliseconds, int.MaxValue));
        while (true)
        {
            ApplyToVisibleWindows(process.Id, title);
            if (process.WaitForExit(milliseconds))
            {
                return;
            }
        }
    }

    public static void Validate(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidDataException("自定义游戏标题不能为空。");
        }

        if (title.Length > MaximumTitleLength || title.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"自定义游戏标题不能包含控制字符，且最多 {MaximumTitleLength} 个字符。");
        }
    }

    private static void ApplyToVisibleWindows(int processId, string title)
    {
        _ = EnumWindows((window, parameter) =>
        {
            GetWindowThreadProcessId(window, out var windowProcessId);
            if (windowProcessId == processId && IsWindowVisible(window))
            {
                SetWindowText(window, title);
            }

            return true;
        }, IntPtr.Zero);
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowText(IntPtr window, string text);
}
