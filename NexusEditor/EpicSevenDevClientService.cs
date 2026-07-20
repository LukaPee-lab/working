using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NexusEditor;

public sealed record EpicSevenDevInstance(
    int ProcessId,
    string GameWindowTitle,
    string ExecutablePath,
    DateTime? StartedAt,
    IntPtr GameWindowHandle,
    IntPtr ConsoleWindowHandle,
    IntPtr ConsoleInputHandle,
    IntPtr ConsoleOutputHandle)
{
    public bool HasConsole => ConsoleWindowHandle != IntPtr.Zero && ConsoleInputHandle != IntPtr.Zero;

    public string DisplayLabel
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(GameWindowTitle) ? "제목 없는 DEV" : GameWindowTitle;
            var started = StartedAt?.ToString("HH:mm:ss") ?? "시각 확인 불가";
            return $"{title}  |  PID {ProcessId}  |  시작 {started}";
        }
    }
}

public sealed record EpicSevenDevCommandResult(bool Success, string Message);

public static class EpicSevenDevClientService
{
    private const uint WmSetText = 0x000C;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VkReturn = 0x0D;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly Regex EventIdPattern = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    public static IReadOnlyList<EpicSevenDevInstance> FindInstances()
    {
        var results = new List<EpicSevenDevInstance>();
        foreach (var process in Process.GetProcessesByName("ur"))
        {
            try
            {
                var executablePath = TryGetExecutablePath(process);
                if (!LooksLikeDevClientExecutable(executablePath))
                    continue;

                var windows = EnumerateTopLevelWindows(process.Id);
                var console = FindConsoleWindow(windows);
                var input = console == IntPtr.Zero ? IntPtr.Zero : FindConsoleInput(console);
                var output = console == IntPtr.Zero ? IntPtr.Zero : FindConsoleOutput(console);
                var gameWindow = windows
                    .Where(window => window != console && IsWindowVisible(window))
                    .Select(window => new { Handle = window, Title = GetWindowTitle(window), Area = GetWindowArea(window) })
                    .Where(window => !string.IsNullOrWhiteSpace(window.Title))
                    .OrderByDescending(window => window.Area)
                    .FirstOrDefault();

                DateTime? startedAt = null;
                try { startedAt = process.StartTime; } catch { }

                results.Add(new EpicSevenDevInstance(
                    process.Id,
                    gameWindow?.Title ?? "",
                    executablePath!,
                    startedAt,
                    gameWindow?.Handle ?? IntPtr.Zero,
                    console,
                    input,
                    output));
            }
            catch
            {
                // A process can exit while the list is being collected.
            }
            finally
            {
                process.Dispose();
            }
        }

        return results
            .OrderBy(instance => instance.GameWindowTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.ProcessId)
            .ToList();
    }

    public static EpicSevenDevCommandResult SendRunEvent(EpicSevenDevInstance selected, string eventId)
    {
        var normalizedEventId = eventId.Trim().ToLowerInvariant();
        if (!EventIdPattern.IsMatch(normalizedEventId))
            return new EpicSevenDevCommandResult(false, $"이벤트 ID 형식이 올바르지 않습니다: {eventId}");

        EpicSevenDevInstance? current;
        try
        {
            current = FindInstances().FirstOrDefault(instance => instance.ProcessId == selected.ProcessId);
        }
        catch (Exception ex)
        {
            return new EpicSevenDevCommandResult(false, $"DEV 클라이언트를 다시 확인하지 못했습니다. {ex.Message}");
        }

        if (current is null)
            return new EpicSevenDevCommandResult(false, $"선택한 DEV 클라이언트(PID {selected.ProcessId})가 종료되었습니다.");
        if (!current.HasConsole)
            return new EpicSevenDevCommandResult(false, $"선택한 DEV 클라이언트(PID {selected.ProcessId})의 console 입력창을 찾지 못했습니다.");

        var command = $"#ct:nexus_run_event( '{normalizedEventId}' )";
        var sent = SendMessageTimeout(
            current.ConsoleInputHandle,
            WmSetText,
            IntPtr.Zero,
            command,
            SmtoAbortIfHung,
            1000,
            out _);
        if (sent == IntPtr.Zero)
            return new EpicSevenDevCommandResult(false, "DEV console 입력창에 치트를 쓰지 못했습니다.");

        if (!PostMessage(current.ConsoleInputHandle, WmKeyDown, (IntPtr)VkReturn, (IntPtr)1)
            || !PostMessage(current.ConsoleInputHandle, WmKeyUp, (IntPtr)VkReturn, unchecked((IntPtr)(long)0xC0000001)))
        {
            return new EpicSevenDevCommandResult(false, "DEV console에 Enter 입력을 전달하지 못했습니다.");
        }

        BringGameWindowToFront(current.GameWindowHandle);

        return new EpicSevenDevCommandResult(
            true,
            $"{current.DisplayLabel}에 {command} 치트를 전송했습니다. 실행 결과는 DEV console에서 확인하세요.");
    }

    public static string ReadConsoleText(EpicSevenDevInstance selected)
    {
        var current = FindInstances().FirstOrDefault(instance => instance.ProcessId == selected.ProcessId);
        if (current?.ConsoleOutputHandle is null or 0)
            return "";

        var lengthCall = SendMessageTimeout(
            current.ConsoleOutputHandle,
            WmGetTextLength,
            IntPtr.Zero,
            IntPtr.Zero,
            SmtoAbortIfHung,
            1000,
            out var lengthResult);
        if (lengthCall == IntPtr.Zero || lengthResult.ToInt64() <= 0)
            return "";

        var length = (int)Math.Min(lengthResult.ToInt64(), 2_000_000);
        var buffer = new StringBuilder(length + 1);
        var textCall = SendMessageTimeoutText(
            current.ConsoleOutputHandle,
            WmGetText,
            (IntPtr)buffer.Capacity,
            buffer,
            SmtoAbortIfHung,
            1500,
            out _);
        return textCall == IntPtr.Zero ? "" : buffer.ToString();
    }

    public static bool LooksLikeDevClientExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !string.Equals(Path.GetFileName(executablePath), "ur.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var x64Directory = Directory.GetParent(executablePath);
            var gameRoot = x64Directory?.Parent?.Parent?.Parent;
            if (gameRoot is null)
                return false;

            return string.Equals(x64Directory?.Name, "x64", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x64Directory?.Parent?.Name, "release", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x64Directory?.Parent?.Parent?.Name, "bin", StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(Path.Combine(gameRoot.FullName, "Resources"))
                   && File.Exists(Path.Combine(gameRoot.FullName, "ur.udf"));
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private static List<IntPtr> EnumerateTopLevelWindows(int processId)
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId == processId)
                windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static IntPtr FindConsoleWindow(IEnumerable<IntPtr> windows)
    {
        var candidates = windows
            .Select(window => new
            {
                Handle = window,
                Title = GetWindowTitle(window),
                Input = FindConsoleInput(window),
                Visible = IsWindowVisible(window)
            })
            .Where(window => window.Input != IntPtr.Zero)
            .OrderByDescending(window => string.Equals(window.Title, "console", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(window => window.Visible)
            .ToList();
        return candidates.FirstOrDefault()?.Handle ?? IntPtr.Zero;
    }

    private static IntPtr FindConsoleInput(IntPtr consoleWindow)
    {
        var candidates = new List<(IntPtr Handle, Rect Rect)>();
        EnumChildWindows(consoleWindow, (child, _) =>
        {
            var className = GetClassNameText(child);
            if (className.Contains("edit", StringComparison.OrdinalIgnoreCase)
                && IsWindowVisible(child)
                && IsWindowEnabled(child)
                && GetWindowRect(child, out var rect)
                && rect.Width >= 120
                && rect.Height is >= 12 and <= 80)
            {
                candidates.Add((child, rect));
            }
            return true;
        }, IntPtr.Zero);

        return candidates
            .OrderByDescending(candidate => candidate.Rect.Bottom)
            .ThenBy(candidate => candidate.Rect.Height)
            .ThenByDescending(candidate => candidate.Rect.Width)
            .Select(candidate => candidate.Handle)
            .FirstOrDefault();
    }

    private static IntPtr FindConsoleOutput(IntPtr consoleWindow)
    {
        var candidates = new List<(IntPtr Handle, Rect Rect)>();
        EnumChildWindows(consoleWindow, (child, _) =>
        {
            var className = GetClassNameText(child);
            if ((className.Contains("edit", StringComparison.OrdinalIgnoreCase)
                 || className.Contains("rich", StringComparison.OrdinalIgnoreCase))
                && IsWindowVisible(child)
                && GetWindowRect(child, out var rect)
                && rect.Width >= 200
                && rect.Height >= 80)
            {
                candidates.Add((child, rect));
            }
            return true;
        }, IntPtr.Zero);

        return candidates
            .OrderByDescending(candidate => (long)candidate.Rect.Width * candidate.Rect.Height)
            .Select(candidate => candidate.Handle)
            .FirstOrDefault();
    }

    private static string GetWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
            return "";
        var buffer = new StringBuilder(length + 1);
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetClassNameText(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        GetClassName(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static long GetWindowArea(IntPtr window)
    {
        return GetWindowRect(window, out var rect) ? (long)rect.Width * rect.Height : 0;
    }

    private static void BringGameWindowToFront(IntPtr gameWindow)
    {
        if (gameWindow == IntPtr.Zero)
            return;

        if (IsIconic(gameWindow))
            ShowWindowAsync(gameWindow, 9); // SW_RESTORE
        else
            ShowWindowAsync(gameWindow, 5); // SW_SHOW

        BringWindowToTop(gameWindow);
        SetForegroundWindow(gameWindow);
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Math.Max(0, Right - Left);
        public int Height => Math.Max(0, Bottom - Top);
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder buffer, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder buffer, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutText(
        IntPtr window,
        uint message,
        IntPtr wParam,
        StringBuilder lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
